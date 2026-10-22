using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace IxIFlow.Analyzers;

/// <summary>
/// The mapping rules at compile time, as the engine applies them when a fluent workflow is
/// built. An activity that marks its properties with <c>[ActivityInput]</c> or <c>[ActivityOutput]</c> takes inputs
/// only into its marked inputs and gives outputs only from its marked outputs, and each input it requires is mapped
/// unless it has a default. An activity without the attributes takes inputs into properties with a public setter and
/// gives outputs from properties with a public getter. Child mappings are checked against their caller view when
/// the enclosing Invoke is known to use one; explicit or unknown contracts get accessor checks. An event takes
/// suggestion inputs into public settable properties and gives outputs from public readable ones.
/// A required input is reported only where every mapping of the step is in sight: when the step's setup is a lambda
/// that does not hand its builder elsewhere.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MappingAnalyzer : DiagnosticAnalyzer
{
    private const string ActivityKind = "Activity";
    private const string WorkflowKind = "Child workflow";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        Descriptors.NotAnInput, Descriptors.NotAnOutput, Descriptors.RequiredInputNotMapped,
        Descriptors.NoPublicProperty, Descriptors.InputNeedsSetter, Descriptors.OutputNeedsGetter,
        Descriptors.RequiredOnOutput, Descriptors.DefaultNeedsSetter, Descriptors.InvalidDefault,
        Descriptors.WorkflowNotAnInput, Descriptors.WorkflowNotAnOutput, Descriptors.WorkflowRequiredInputNotMapped,
        Descriptors.WorkflowInputNeedsSetter, Descriptors.WorkflowOutputNeedsGetter, Descriptors.WorkflowRequiredOnOutput,
        Descriptors.WorkflowDefaultNeedsSetter, Descriptors.WorkflowInvalidDefault);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            if (EngineSymbols.For(start.Compilation) is not { } engine)
                return;
            var checkedActivities = new ConcurrentDictionary<INamedTypeSymbol, byte>(SymbolEqualityComparer.Default);
            var checkedWorkflows = new ConcurrentDictionary<INamedTypeSymbol, byte>(SymbolEqualityComparer.Default);
            start.RegisterOperationAction(operation =>
            {
                var invocation = (IInvocationOperation)operation.Operation;
                if (!engine.IsBuilderMethod(invocation.TargetMethod))
                    return;
                CheckMapping(operation, engine, invocation);
                CheckContract(operation, engine, invocation, invocation.TargetMethod.Name == "Invoke" ? checkedWorkflows : checkedActivities);
                CheckRequiredInputs(operation, engine, invocation);
                CheckWaitRequiredInputs(operation, engine, invocation);
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
        var properties = PublicProperties(owner);
        if (!properties.TryGetValue(name, out var property))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.NoPublicProperty, location, kind, owner.MetadataName, name));
            return;
        }
        var workflow = kind == WorkflowKind;
        var marked = (kind == ActivityKind || workflow && UsesCallerView(engine, mapping)) &&
            PublicProperties(owner, publicOnly: false).Values.Any(each => Marked(engine, each, workflow));
        var qualified = $"{owner.MetadataName}.{property.Name}";
        // Accessor errors remain actionable even when the effective contract is supplied separately.
        if (input && property.SetMethod is not { DeclaredAccessibility: Accessibility.Public })
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.NotAnInput, location, kind, qualified, "public setter"));
        else if (!input && property.GetMethod is not { DeclaredAccessibility: Accessibility.Public })
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.NotAnOutput, location, kind, qualified, "public getter"));
        else if (input && marked && !Input(engine, property, true, workflow))
        {
            if (workflow) context.ReportDiagnostic(Diagnostic.Create(Descriptors.WorkflowNotAnInput, location, qualified));
            else context.ReportDiagnostic(Diagnostic.Create(Descriptors.NotAnInput, location, kind, qualified, "[ActivityInput]"));
        }
        else if (!input && marked && !Output(engine, property, true, workflow))
        {
            if (workflow) context.ReportDiagnostic(Diagnostic.Create(Descriptors.WorkflowNotAnOutput, location, qualified));
            else context.ReportDiagnostic(Diagnostic.Create(Descriptors.NotAnOutput, location, kind, qualified, "[ActivityOutput]"));
        }
    }

    private static void CheckRequiredInputs(OperationAnalysisContext context, EngineSymbols engine,
        IInvocationOperation invocation)
    {
        if (Declared(engine, invocation) is not ({ } activity, var setup))
            return;
        var properties = PublicProperties(activity).Values.ToList();
        var workflow = invocation.TargetMethod.Name == "Invoke";
        var marked = PublicProperties(activity, publicOnly: false).Values.Any(property => Marked(engine, property, workflow));
        if (workflow && (!marked || HasContract(invocation))) return;
        var required = properties
            .Where(property => Input(engine, property, marked, workflow) && Attribute(engine.Required, property) != null &&
                Attribute(engine.DefaultValue, property) == null)
            .ToList();
        if (required.Count == 0)
            return;
        HashSet<string> mapped;
        if (setup == null || setup.ArgumentKind == ArgumentKind.DefaultValue ||
            EngineSymbols.Unwrapped(setup.Value) is ILiteralOperation { ConstantValue: { HasValue: true, Value: null } })
            mapped = [];
        else if (EngineSymbols.Unwrapped(setup.Value) is IAnonymousFunctionOperation lambda &&
                 MappedInputs(engine, lambda, activity, workflow) is { } seen)
            mapped = seen;
        else
            return;
        var location = NameLocation(invocation);
        foreach (var property in required.Where(property => !mapped.Contains(property.Name)))
            if (workflow) context.ReportDiagnostic(Diagnostic.Create(Descriptors.WorkflowRequiredInputNotMapped, location,
                $"{activity.MetadataName}.{property.Name}"));
            else context.ReportDiagnostic(Diagnostic.Create(Descriptors.RequiredInputNotMapped, location, ActivityKind,
                $"{activity.MetadataName}.{property.Name}"));
    }

    private static void CheckWaitRequiredInputs(OperationAnalysisContext context, EngineSymbols engine,
        IInvocationOperation invocation)
    {
        if (invocation.TargetMethod.Name is not ("WaitFor" or "Suspend"))
            return;
        foreach (var argument in invocation.Arguments)
        {
            if (argument.Parameter?.Type is not INamedTypeSymbol { Name: "Action", TypeArguments.Length: 1 } action ||
                action.TypeArguments[0] is not INamedTypeSymbol builder || !builder.Name.Contains("Suspend") ||
                InputOwner(builder) is not INamedTypeSymbol owner ||
                EngineSymbols.Unwrapped(argument.Value) is not IAnonymousFunctionOperation setup)
                continue;
            // A supplied payload may provide required values; inspect its value at build time instead.
            if (setup.Body.Descendants().OfType<IInvocationOperation>().Any(call =>
                    call.TargetMethod.Name == "DefaultPayload" && engine.IsBuilderMethod(call.TargetMethod)))
                return;
            if (MappedInputs(engine, setup, owner) is not { Count: > 0 } mapped)
                return;
            foreach (var property in PublicProperties(owner).Values.Where(property =>
                         Attribute(engine.Required, property) != null && !mapped.Contains(property.Name)))
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.RequiredInputNotMapped, NameLocation(invocation),
                    "Event", $"{owner.MetadataName}.{property.Name}"));
        }
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
                return Kind(engine, owner, builder) is ActivityKind or WorkflowKind ? (owner, argument) : (null, null);
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
        INamedTypeSymbol activity, bool workflow = false)
    {
        if (ExclusiveBuilders(engine, setup, traceContinuations: workflow) is not { } exclusive)
            return null;
        var mapped = new HashSet<string>();
        var builders = new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default);
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
                    var receiverRoot = Root(engine, receiver, resolveLocals: !workflow);
                    if (workflow && ReferencedBuilder(receiverRoot) is { } symbol && exclusive.Contains(symbol))
                    {
                        mapped.Add(name);
                        break;
                    }
                    switch (receiverRoot)
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
        // ExclusiveBuilders already checks workflow-local captures, with the nameof exemption.
        // Activity/event continuations retain their existing nested-use check.
        foreach (var inner in workflow ? Enumerable.Empty<IOperation>() : nested)
            if (inner.Descendants().Any(each => each switch
                {
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
            return WorkflowKind;
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

    // Workflow contracts validate local provenance through ExclusiveBuilders, without WrittenLocals.
    // Other mapping analyses retain initializer-based receiver tracing.
    private static IOperation Root(EngineSymbols engine, IOperation receiver, bool resolveLocals = true)
    {
        for (var depth = 0; depth < 64; depth++)
        {
            receiver = resolveLocals ? Resolved(receiver) : EngineSymbols.Unwrapped(receiver);
            if (receiver is not IInvocationOperation { Instance: { } instance } call || !engine.IsBuilderMethod(call.TargetMethod))
                return receiver;
            receiver = instance;
        }
        return receiver;
    }

    // Public instance properties by name, the most derived first, as the runtime mapping lookup chooses them.
    private static Dictionary<string, IPropertySymbol> PublicProperties(INamedTypeSymbol type, bool publicOnly = true)
    {
        var properties = new Dictionary<string, IPropertySymbol>();
        for (var each = type; each != null; each = each.BaseType)
            foreach (var property in each.GetMembers().OfType<IPropertySymbol>())
                if (property is { IsStatic: false, IsIndexer: false } &&
                    (!publicOnly || property.DeclaredAccessibility == Accessibility.Public) &&
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

    private static bool Marked(EngineSymbols engine, IPropertySymbol property, bool workflow = false) =>
        Attribute(workflow ? engine.WorkflowInput : engine.Input, property) != null || Attribute(workflow ? engine.WorkflowOutput : engine.Output, property) != null ||
        Attribute(workflow ? engine.WorkflowInputOutput : engine.InputOutput, property) != null;

    private static bool Input(EngineSymbols engine, IPropertySymbol property, bool marked, bool workflow = false) => marked
        ? Attribute(workflow ? engine.WorkflowInput : engine.Input, property) != null || Attribute(workflow ? engine.WorkflowInputOutput : engine.InputOutput, property) != null
        : property.SetMethod is { DeclaredAccessibility: Accessibility.Public };

    private static bool Output(EngineSymbols engine, IPropertySymbol property, bool marked, bool workflow = false) => marked
        ? Attribute(workflow ? engine.WorkflowOutput : engine.Output, property) != null || Attribute(workflow ? engine.WorkflowInputOutput : engine.InputOutput, property) != null
        : property.GetMethod is { DeclaredAccessibility: Accessibility.Public };

    // Check the contract when a type is used as an activity, including properties no mapping selects. The same type
    // may also be child data; that use follows the data rules. Report each activity contract once, at its property declarations.
    private static void CheckContract(OperationAnalysisContext context, EngineSymbols engine,
        IInvocationOperation invocation, ConcurrentDictionary<INamedTypeSymbol, byte> checkedActivities)
    {
        if (Declared(engine, invocation) is not ({ } activity, _))
            return;
        var workflow = invocation.TargetMethod.Name == "Invoke";
        if (workflow && HasContract(invocation) || !checkedActivities.TryAdd(activity, 0)) return;
        var properties = PublicProperties(activity, publicOnly: false).Values.ToList();
        var marked = properties.Any(property => Marked(engine, property, workflow));
        foreach (var property in properties)
        {
            var location = property.Locations.FirstOrDefault(each => each.IsInSource) ?? NameLocation(invocation);
            var qualified = $"{activity.MetadataName}.{property.Name}";
            if (Marked(engine, property, workflow))
            {
                if (Input(engine, property, true, workflow) && property.SetMethod is not { DeclaredAccessibility: Accessibility.Public })
                    Report(workflow ? Descriptors.WorkflowInputNeedsSetter : Descriptors.InputNeedsSetter);
                if (Output(engine, property, true, workflow) && property.GetMethod is not { DeclaredAccessibility: Accessibility.Public })
                    Report(workflow ? Descriptors.WorkflowOutputNeedsGetter : Descriptors.OutputNeedsGetter);
            }
            if (Attribute(engine.Required, property) != null && Output(engine, property, marked, workflow) &&
                !Input(engine, property, marked, workflow))
                Report(workflow ? Descriptors.WorkflowRequiredOnOutput : Descriptors.RequiredOnOutput);
            if (Attribute(engine.DefaultValue, property) is { } fallback)
            {
                if (property.SetMethod is not { DeclaredAccessibility: Accessibility.Public })
                    Report(workflow ? Descriptors.WorkflowDefaultNeedsSetter : Descriptors.DefaultNeedsSetter);
                if (!ActivityDefaults.CanConvert(engine.Compilation, fallback, property.Type))
                    context.ReportDiagnostic(Diagnostic.Create(workflow ? Descriptors.WorkflowInvalidDefault : Descriptors.InvalidDefault, location, qualified, property.Type.MetadataName));
            }
            void Report(DiagnosticDescriptor descriptor) =>
                context.ReportDiagnostic(Diagnostic.Create(descriptor, location, qualified));
        }
    }

    private static bool HasContract(IInvocationOperation invocation) => invocation.Arguments.Any(argument =>
        argument.Parameter?.Type is INamedTypeSymbol { Name: "WorkflowContract" } type && EngineSymbols.InNamespace(type, "IxIFlow.Core"));

    private static ISymbol? ReferencedBuilder(IOperation operation) => operation switch
    {
        IParameterReferenceOperation parameter => parameter.Parameter,
        ILocalReferenceOperation local => local.Local,
        _ => null
    };

    // Every reference must start a builder receiver chain (except nameof). For workflow contracts,
    // follow those chains into non-ref local declarations and apply the same rule to every link.
    // Assignment sources/targets, arguments, ref aliases and captures invalidate the entire setup.
    private static HashSet<ISymbol>? ExclusiveBuilders(EngineSymbols engine, IAnonymousFunctionOperation setup,
        bool traceContinuations)
    {
        if (setup.Symbol.Parameters.FirstOrDefault() is not { } parameter)
            return null;
        var builders = new HashSet<ISymbol>(SymbolEqualityComparer.Default) { parameter };
        var pending = new Queue<ISymbol>();
        pending.Enqueue(parameter);
        var references = setup.Body.Descendants().Where(operation => ReferencedBuilder(operation) != null).ToList();
        while (pending.Count > 0)
        {
            var builder = pending.Dequeue();
            foreach (var reference in references.Where(reference =>
                         SymbolEqualityComparer.Default.Equals(ReferencedBuilder(reference), builder)))
            {
                var nameOnly = false;
                for (var parent = reference.Parent; parent != null && parent != setup; parent = parent.Parent)
                {
                    if (parent is INameOfOperation)
                    {
                        nameOnly = true;
                        break;
                    }
                    if (parent is IAnonymousFunctionOperation or ILocalFunctionOperation)
                        return null;
                }
                if (nameOnly)
                    continue;
                if (reference.Parent is not IInvocationOperation call || call.Instance != reference ||
                    !engine.IsBuilderMethod(call.TargetMethod))
                    return null;
                if (!traceContinuations)
                    continue;
                IOperation chain = call;
                while (true)
                {
                    if (chain.Parent is IInvocationOperation next && next.Instance == chain &&
                        engine.IsBuilderMethod(next.TargetMethod))
                        chain = next;
                    else if (chain.Parent is IConversionOperation or IParenthesizedOperation)
                        chain = chain.Parent;
                    else
                        break;
                }
                if (chain.Parent is IVariableInitializerOperation { Parent: IVariableDeclaratorOperation declaration })
                {
                    if (declaration.Symbol.RefKind != RefKind.None)
                        return null;
                    if (builders.Add(declaration.Symbol))
                        pending.Enqueue(declaration.Symbol);
                }
                else if (chain.Parent is IAssignmentOperation)
                    return null;
            }
        }
        return builders;
    }

    private static bool UsesCallerView(EngineSymbols engine, IInvocationOperation mapping)
    {
        // A containing setup establishes the contract only for mappings rooted in its own builder parameter.
        if (mapping.Instance == null ||
            ReferencedBuilder(Root(engine, mapping.Instance, resolveLocals: false)) is not { } root)
            return false;
        for (var each = mapping.Parent; each != null; each = each.Parent)
            if (each is IArgumentOperation { Parent: IInvocationOperation invocation } argument &&
                engine.IsWorkflowInvoke(invocation) && Declared(engine, invocation).Setup == argument &&
                EngineSymbols.Unwrapped(argument.Value) is IAnonymousFunctionOperation setup &&
                ExclusiveBuilders(engine, setup, traceContinuations: true) is { } builders && builders.Contains(root))
                return !HasContract(invocation);
        // A helper or extracted delegate may be used with an explicit contract.
        return false;
    }

    private static Location NameLocation(IInvocationOperation invocation) =>
        invocation.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access }
            ? access.Name.GetLocation()
            : invocation.Syntax.GetLocation();
}
