using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace IxIFlow.Analyzers;

/// <summary>
/// The mapping rules of D-26 (spec F-22, F-24) at compile time, as the engine applies them when a fluent workflow is
/// built. An activity that marks its properties with <c>[WorkflowInput]</c> or <c>[WorkflowOutput]</c> takes inputs
/// only into its marked inputs and gives outputs only from its marked outputs, and each input it requires is mapped
/// unless it has a default. An activity without the attributes takes inputs into properties with a public setter and
/// gives outputs from properties with a public getter. A child workflow's data takes inputs into public settable
/// properties and gives outputs from public readable ones, and an event gives outputs from public readable ones.
/// A required input is reported only where every mapping of the step is in sight: when the step's setup is a lambda
/// that does not hand its builder elsewhere.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MappingAnalyzer : DiagnosticAnalyzer
{
    private const string ActivityKind = "Activity";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        Descriptors.NotAnInput, Descriptors.NotAnOutput, Descriptors.RequiredInputNotMapped,
        Descriptors.NoPublicProperty);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            if (EngineSymbols.For(start.Compilation) is not { } engine)
                return;
            start.RegisterOperationAction(operation =>
            {
                var invocation = (IInvocationOperation)operation.Operation;
                if (!engine.IsBuilderMethod(invocation.TargetMethod))
                    return;
                CheckMapping(operation, engine, invocation);
                CheckRequiredInputs(operation, engine, invocation);
            }, OperationKind.Invocation);
        });
    }

    // A mapping counts once it is completed: Input(...).From(...) or Output(...).To(...), as the builder records it.
    private static void CheckMapping(OperationAnalysisContext context, EngineSymbols engine, IInvocationOperation completion)
    {
        var input = completion.TargetMethod.Name == "From";
        if (!input && completion.TargetMethod.Name != "To" || completion.Instance == null ||
            Resolved(completion.Instance) is not IInvocationOperation mapping ||
            mapping.TargetMethod.Name != (input ? "Input" : "Output") || !engine.IsBuilderMethod(mapping.TargetMethod) ||
            Selected(mapping) is not ({ } owner, { } name, { } location) ||
            Kind(engine, owner, mapping.TargetMethod.ContainingType) is not { } kind)
            return;
        // The engine does not check the inputs of an event, which a wait does not use.
        if (input && kind == "Event")
            return;
        var properties = PublicProperties(owner);
        if (!properties.TryGetValue(name, out var property))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.NoPublicProperty, location, kind, owner.MetadataName, name));
            return;
        }
        var marked = kind == ActivityKind && properties.Values.Any(each =>
            Attribute(engine.Input, each) != null || Attribute(engine.Output, each) != null);
        var qualified = $"{owner.MetadataName}.{property.Name}";
        if (input && marked && Attribute(engine.Input, property) == null)
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.NotAnInput, location, kind, qualified, "[WorkflowInput]"));
        else if (input && !marked && property.SetMethod is not { DeclaredAccessibility: Accessibility.Public })
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.NotAnInput, location, kind, qualified, "public setter"));
        else if (!input && marked && Attribute(engine.Output, property) == null)
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.NotAnOutput, location, kind, qualified, "[WorkflowOutput]"));
        else if (!input && !marked && property.GetMethod is not { DeclaredAccessibility: Accessibility.Public })
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.NotAnOutput, location, kind, qualified, "public getter"));
    }

    private static void CheckRequiredInputs(OperationAnalysisContext context, EngineSymbols engine,
        IInvocationOperation invocation)
    {
        if (Declared(engine, invocation) is not ({ } activity, var setup))
            return;
        var required = PublicProperties(activity).Values
            .Where(property => Attribute(engine.Input, property) is { } attribute && Required(attribute))
            .ToList();
        if (required.Count == 0)
            return;
        HashSet<string> mapped;
        if (setup == null || setup.ArgumentKind == ArgumentKind.DefaultValue ||
            EngineSymbols.Unwrapped(setup.Value) is ILiteralOperation { ConstantValue: { HasValue: true, Value: null } })
            mapped = [];
        else if (EngineSymbols.Unwrapped(setup.Value) is IAnonymousFunctionOperation lambda &&
                 MappedInputs(engine, lambda, activity) is { } seen)
            mapped = seen;
        else
            return;
        var location = NameLocation(invocation);
        foreach (var property in required.Where(property => !mapped.Contains(property.Name)))
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.RequiredInputNotMapped, location, ActivityKind,
                $"{activity.MetadataName}.{property.Name}"));
    }

    // The activity a builder call adds, and the argument that sets it up: the Action whose builder maps into the
    // activity, or none for the overloads that take no setup, such as CompensateWith<T>().
    private static (INamedTypeSymbol? Activity, IArgumentOperation? Setup) Declared(EngineSymbols engine,
        IInvocationOperation invocation)
    {
        foreach (var argument in invocation.Arguments)
            if (argument.Parameter?.Type is INamedTypeSymbol { Name: "Action", TypeArguments.Length: 1 } action &&
                action.ContainingNamespace?.ToDisplayString() == "System" &&
                action.TypeArguments[0] is INamedTypeSymbol builder && EngineSymbols.InNamespace(builder, "IxIFlow.Builders") &&
                InputOwner(builder) is INamedTypeSymbol owner)
                return Kind(engine, owner, builder) == ActivityKind ? (owner, argument) : (null, null);
        var method = invocation.TargetMethod;
        if (method.Name is "Step" or "CompensateWith" &&
            !method.Parameters.Any(parameter => parameter.Type is INamedTypeSymbol { Name: "Action" }) &&
            method.TypeArguments.FirstOrDefault(engine.IsActivity) is INamedTypeSymbol activity)
            return (activity, null);
        return (null, null);
    }

    // The type a builder's Input selects properties of: X in Input<TProperty>(Expression<Func<X, TProperty>>).
    private static ITypeSymbol? InputOwner(INamedTypeSymbol builder) =>
        new[] { builder }.Concat(builder.AllInterfaces)
            .SelectMany(type => type.GetMembers("Input").OfType<IMethodSymbol>())
            .Select(method => method.Parameters.FirstOrDefault()?.Type)
            .OfType<INamedTypeSymbol>()
            .Where(expression => expression.TypeArguments.Length == 1)
            .Select(expression => expression.TypeArguments[0])
            .OfType<INamedTypeSymbol>()
            .Where(func => func.TypeArguments.Length == 2)
            .Select(func => func.TypeArguments[0])
            .FirstOrDefault();

    // The inputs a setup lambda maps into the activity; none when the builder leaves the lambda's sight, since
    // whatever receives it may map more.
    private static HashSet<string>? MappedInputs(EngineSymbols engine, IAnonymousFunctionOperation setup,
        INamedTypeSymbol activity)
    {
        var mapped = new HashSet<string>();
        var builders = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        builders.UnionWith(setup.Symbol.Parameters);
        var nested = new List<IOperation>();
        foreach (var operation in Outside(setup.Body, nested))
        {
            switch (operation)
            {
                case IVariableDeclaratorOperation declarator when IsBuilder(declarator.Symbol.Type):
                    builders.Add(declarator.Symbol);
                    break;
                case IArgumentOperation argument when IsBuilder(EngineSymbols.Unwrapped(argument.Value).Type):
                case ISimpleAssignmentOperation assignment when IsBuilder(assignment.Value.Type) &&
                                                               assignment.Target is not ILocalReferenceOperation:
                case IReturnOperation { ReturnedValue: { } returned } when IsBuilder(returned.Type):
                    return null;
                case IInvocationOperation { TargetMethod.Name: "From", Instance: { } instance } completion
                    when engine.IsBuilderMethod(completion.TargetMethod):
                    if (Resolved(instance) is not IInvocationOperation { TargetMethod.Name: "Input", Instance: { } receiver } input ||
                        !engine.IsBuilderMethod(input.TargetMethod) || Selected(input) is not ({ } owner, { } name, _))
                        return null;
                    if (!SymbolEqualityComparer.Default.Equals(owner, activity))
                        break;
                    // A mapping counts on the step's own builder, not on another lambda's; one whose builder cannot be
                    // traced may be either.
                    switch (Root(engine, receiver))
                    {
                        case IParameterReferenceOperation root
                            when SymbolEqualityComparer.Default.Equals(root.Parameter, setup.Symbol.Parameters.FirstOrDefault()):
                            mapped.Add(name);
                            break;
                        case IParameterReferenceOperation:
                            break;
                        default:
                            return null;
                    }
                    break;
            }
        }
        // A nested lambda or local function that uses the builder may map more, whenever it runs.
        foreach (var inner in nested)
            if (inner.Descendants().Any(each => each switch
                {
                    IParameterReferenceOperation parameter => builders.Contains(parameter.Parameter),
                    ILocalReferenceOperation local => builders.Contains(local.Local),
                    _ => false
                }))
                return null;
        return mapped;
    }

    // The operations of a lambda body outside the lambdas and local functions it contains, which are collected.
    private static IEnumerable<IOperation> Outside(IOperation root, List<IOperation> nested)
    {
        var pending = new Stack<IOperation>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var operation = pending.Pop();
            if (operation != root && operation is IAnonymousFunctionOperation or ILocalFunctionOperation)
            {
                nested.Add(operation);
                continue;
            }
            yield return operation;
            foreach (var child in operation.ChildOperations)
                pending.Push(child);
        }
    }

    private static bool IsBuilder(ITypeSymbol? type) =>
        type is INamedTypeSymbol named && EngineSymbols.InNamespace(named, "IxIFlow.Builders");

    // The owner type, the member name and its location in Input(x => x.Member) or Output(x => x.Member). The engine
    // takes the member's name and looks it up on the owner, so that is what is checked.
    private static (INamedTypeSymbol? Owner, string? Name, Location? Location) Selected(IInvocationOperation mapping)
    {
        if (mapping.Arguments.Length != 1 ||
            EngineSymbols.Unwrapped(mapping.Arguments[0].Value) is not IAnonymousFunctionOperation selector ||
            selector.Symbol.Parameters.Length != 1 ||
            selector.Symbol.Parameters[0].Type is not INamedTypeSymbol owner ||
            selector.Body.Operations.Length != 1 ||
            selector.Body.Operations[0] is not IReturnOperation { ReturnedValue: IMemberReferenceOperation member })
            return (null, null, null);
        var syntax = member.Syntax is MemberAccessExpressionSyntax access ? access.Name : member.Syntax;
        return (owner, member.Member.Name, syntax.GetLocation());
    }

    // What the mapped type is to the step, by the builder that maps it: a child's data or an event whatever it
    // implements, and otherwise an activity when it is one.
    private static string? Kind(EngineSymbols engine, INamedTypeSymbol owner, INamedTypeSymbol builder)
    {
        if (builder.Name.Contains("WorkflowInvocationSetup"))
            return "Child workflow";
        if (builder.Name.Contains("Suspend"))
            return "Event";
        return engine.IsActivity(owner) ? ActivityKind : null;
    }

    // The value an operation stands for, through locals that are set once where they are declared.
    private static IOperation Resolved(IOperation operation)
    {
        for (var depth = 0; depth < 8; depth++)
        {
            operation = EngineSymbols.Unwrapped(operation);
            if (operation is not ILocalReferenceOperation reference ||
                EngineSymbols.Initializer(reference, default) is not { } value)
                return operation;
            operation = value;
        }
        return operation;
    }

    // Where a chain of builder calls starts: the receiver of its first call, through locals.
    private static IOperation Root(EngineSymbols engine, IOperation receiver)
    {
        for (var depth = 0; depth < 64; depth++)
        {
            receiver = Resolved(receiver);
            if (receiver is not IInvocationOperation { Instance: { } instance } call || !engine.IsBuilderMethod(call.TargetMethod))
                return receiver;
            receiver = instance;
        }
        return receiver;
    }

    // Public instance properties by name, the most derived first, as reflection's GetProperty finds them.
    private static Dictionary<string, IPropertySymbol> PublicProperties(INamedTypeSymbol type)
    {
        var properties = new Dictionary<string, IPropertySymbol>();
        for (var each = type; each != null; each = each.BaseType)
            foreach (var property in each.GetMembers().OfType<IPropertySymbol>())
                if (property is { IsStatic: false, IsIndexer: false, DeclaredAccessibility: Accessibility.Public } &&
                    !properties.ContainsKey(property.Name))
                    properties.Add(property.Name, property);
        return properties;
    }

    // The attribute on the property or a property it overrides, as reflection finds it with inheritance.
    private static AttributeData? Attribute(INamedTypeSymbol? attribute, IPropertySymbol property)
    {
        if (attribute == null)
            return null;
        for (var each = property; each != null; each = each.OverriddenProperty)
            if (each.GetAttributes().FirstOrDefault(data =>
                    SymbolEqualityComparer.Default.Equals(data.AttributeClass, attribute)) is { } found)
                return found;
        return null;
    }

    private static bool Required(AttributeData input)
    {
        var required = false;
        string? fallback = null;
        foreach (var argument in input.NamedArguments)
            if (argument.Key == "Required" && argument.Value.Value is bool value)
                required = value;
            else if (argument.Key == "Default")
                fallback = argument.Value.Value as string;
        return required && fallback == null;
    }

    private static Location NameLocation(IInvocationOperation invocation) =>
        invocation.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access }
            ? access.Name.GetLocation()
            : invocation.Syntax.GetLocation();
}
