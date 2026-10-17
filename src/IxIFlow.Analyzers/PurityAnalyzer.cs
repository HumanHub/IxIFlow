using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace IxIFlow.Analyzers;

/// <summary>
/// The purity rule of D-07 and D-42 at compile time. Conditions, mappings, wait predicates, loop conditions,
/// selectors and combiners are evaluated again after a recovery, so they may not read the clock or the environment,
/// make values that differ per run, wait, or do I/O. Workflow code is what is given to a <c>Func</c> or
/// <c>Expression&lt;Func&gt;</c> parameter of the engine's builders: a lambda, a method group, or a local, field,
/// property or call that holds one. A combiner is a class that implements <c>ICombiner&lt;T&gt;</c>: its
/// <c>Combine</c>, its constructors and its initializers. Unlike the build check, the analyzer also looks into the
/// methods, accessors, operators and constructors that code calls when they are in the same compilation (see
/// <see cref="WorkflowCode"/>), and reports the call that leads to the impure member.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PurityAnalyzer : DiagnosticAnalyzer
{
    // Selectors whose lambda only names a property: the builder takes the name and never runs the code.
    private static readonly HashSet<string> NameOnly = ["Input", "Output", "Combine"];

    // Lambdas that name a destination: the property they end in is set, not read.
    private static readonly HashSet<string> Destinations = ["To", "Into"];

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(Descriptors.ImpureExpression, Descriptors.ImpureCombiner);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            if (EngineSymbols.For(start.Compilation) is not { } engine)
                return;
            var code = new WorkflowCode(engine.Compilation);
            start.RegisterOperationAction(operation =>
                CheckExpressions(operation, engine, code), OperationKind.Invocation);
            if (engine.Combiner != null)
                start.RegisterSymbolAction(symbol => CheckCombiner(symbol, engine, code), SymbolKind.NamedType);
        });
    }

    private static void CheckExpressions(OperationAnalysisContext context, EngineSymbols engine, WorkflowCode code)
    {
        var invocation = (IInvocationOperation)context.Operation;
        if (!engine.IsBuilderMethod(invocation.TargetMethod) || NameOnly.Contains(invocation.TargetMethod.Name))
            return;
        var destination = Destinations.Contains(invocation.TargetMethod.Name);
        foreach (var argument in invocation.Arguments)
        {
            if (argument.Parameter == null || argument.ArgumentKind == ArgumentKind.DefaultValue ||
                !engine.TakesCode(argument.Parameter.Type))
                continue;
            var value = EngineSymbols.Unwrapped(argument.Value);
            if (value is ILocalReferenceOperation local && EngineSymbols.Initializer(local) is { } stored &&
                EngineSymbols.Unwrapped(stored) is IAnonymousFunctionOperation or IMethodReferenceOperation)
                value = EngineSymbols.Unwrapped(stored);
            foreach (var (use, member) in code.Uses(value, destination, context.CancellationToken))
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.ImpureExpression, use.Syntax.GetLocation(),
                    ClockFix(engine, use, member), member));
        }
    }

    // A combiner runs its Combine, its constructors and its initializers at a join. What it declares is reported where
    // it is used; what it inherits is reported at the class, naming the inherited member.
    private static void CheckCombiner(SymbolAnalysisContext context, EngineSymbols engine, WorkflowCode code)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        var combiners = type.AllInterfaces
            .Where(each => SymbolEqualityComparer.Default.Equals(each.OriginalDefinition, engine.Combiner))
            .ToList();
        if (combiners.Count == 0 || type.IsAbstract || type.Locations.FirstOrDefault(each => each.IsInSource) is not { } at)
            return;
        var cancellation = context.CancellationToken;
        var reported = new HashSet<(Location, string)>();
        void Report(Location location, string member)
        {
            if (reported.Add((location, member)))
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.ImpureCombiner, location, type.Name, member));
        }
        void Uses(IOperation body)
        {
            // The combiner's own fields are reported at their initializers.
            foreach (var (use, member) in code.Uses(body, destination: false, cancellation))
                if (use is not IFieldReferenceOperation field ||
                    !SymbolEqualityComparer.Default.Equals(field.Field.ContainingType.OriginalDefinition, type.OriginalDefinition))
                    Report(use.Syntax.GetLocation(), member);
        }

        var runs = combiners
            .SelectMany(each => each.GetMembers("Combine"))
            .Select(combine => type.FindImplementationForInterfaceMember(combine))
            .OfType<IMethodSymbol>()
            .Concat(type.InstanceConstructors)
            .Concat(type.StaticConstructors);
        foreach (var method in runs)
        {
            if (!SymbolEqualityComparer.Default.Equals(method.ContainingType.OriginalDefinition, type.OriginalDefinition))
            {
                if (code.Leads(method, cancellation) is { } inherited)
                    Report(at, inherited);
                continue;
            }
            foreach (var body in code.Bodies(method, withInitializers: false, cancellation))
                Uses(body);
            if (WorkflowCode.ImplicitBase(method, cancellation) is { } constructor &&
                code.Leads(constructor, cancellation) is { } inBase)
                Report(at, inBase);
        }
        foreach (var initializer in code.Initializers(type, isStatic: false, cancellation)
                     .Concat(code.Initializers(type, isStatic: true, cancellation)))
            Uses(initializer);
    }

    // The fix for DateTime.UtcNow and DateTimeOffset.UtcNow in a lambda that has the workflow context in scope: the
    // context's UtcNow, which reads the run's clock. None when the name would bind to something else at the use, or
    // the use sits in a static lambda or local function that cannot capture the context.
    private static ImmutableDictionary<string, string?>? ClockFix(EngineSymbols engine, IOperation use, string member)
    {
        if (member is not ("DateTime.UtcNow" or "DateTimeOffset.UtcNow") || use is not IPropertyReferenceOperation ||
            use.SemanticModel == null)
            return null;
        for (var each = use.Parent; each != null; each = each.Parent)
        {
            if (each is ILocalFunctionOperation function)
            {
                if (function.Symbol.IsStatic)
                    return null;
                continue;
            }
            if (each is not IAnonymousFunctionOperation lambda)
                continue;
            var context = lambda.Symbol.Parameters.FirstOrDefault(parameter =>
                engine.IsContext(parameter.Type) && !parameter.IsDiscard &&
                SyntaxFacts.GetKeywordKind(parameter.Name) == SyntaxKind.None);
            if (context != null)
            {
                var bound = use.SemanticModel.LookupSymbols(use.Syntax.SpanStart, name: context.Name);
                return bound.Length == 1 && SymbolEqualityComparer.Default.Equals(bound[0], context)
                    ? ImmutableDictionary<string, string?>.Empty.Add(Descriptors.ClockReplacement,
                        member == "DateTime.UtcNow" ? $"{context.Name}.UtcNow.UtcDateTime" : $"{context.Name}.UtcNow")
                    : null;
            }
            if (lambda.Symbol.IsStatic)
                return null;
        }
        return null;
    }
}
