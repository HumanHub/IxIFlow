using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace IxIFlow.Analyzers;

/// <summary>
/// The purity rule of D-07 and D-42 at compile time. Conditions, mappings, wait predicates, loop conditions,
/// selectors and combiners are evaluated again after a recovery, so they may not read the clock or the environment,
/// make values that differ per run, wait, or do I/O. Workflow code is every lambda or method group given to a
/// <c>Func</c> or <c>Expression&lt;Func&gt;</c> parameter of the engine's builders; a combiner is a class that
/// implements <c>ICombiner&lt;T&gt;</c>, its <c>Combine</c>, constructors and instance initializers. Unlike the
/// build check, the analyzer also looks into the methods, property getters and constructors that code calls when
/// they are in the same compilation, and reports the call that leads to the impure member.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PurityAnalyzer : DiagnosticAnalyzer
{
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
            var helpers = new Helpers(engine.Compilation);
            start.RegisterOperationAction(operation =>
                CheckExpressions(operation, engine, helpers), OperationKind.Invocation);
            if (engine.Combiner != null)
                start.RegisterOperationBlockAction(block => CheckCombiner(block, engine, helpers));
        });
    }

    private static void CheckExpressions(OperationAnalysisContext context, EngineSymbols engine, Helpers helpers)
    {
        var invocation = (IInvocationOperation)context.Operation;
        if (!engine.IsBuilderMethod(invocation.TargetMethod))
            return;
        foreach (var argument in invocation.Arguments)
        {
            if (argument.Parameter == null || argument.ArgumentKind == ArgumentKind.DefaultValue ||
                !engine.TakesCode(argument.Parameter.Type))
                continue;
            var code = EngineSymbols.Unwrapped(argument.Value);
            if (code is not (IAnonymousFunctionOperation or IMethodReferenceOperation))
                continue;
            foreach (var (use, member) in helpers.Uses(code))
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.ImpureExpression, use.Syntax.GetLocation(),
                    ClockFix(engine, use, member), member));
        }
    }

    private static void CheckCombiner(OperationBlockAnalysisContext context, EngineSymbols engine, Helpers helpers)
    {
        var owner = context.OwningSymbol;
        if (owner.ContainingType is not { } type || owner.IsStatic ||
            !type.AllInterfaces.Any(each => SymbolEqualityComparer.Default.Equals(each.OriginalDefinition, engine.Combiner)))
            return;
        var runs = owner switch
        {
            IMethodSymbol { MethodKind: MethodKind.Constructor } => true,
            IMethodSymbol method => type.AllInterfaces
                .Where(each => SymbolEqualityComparer.Default.Equals(each.OriginalDefinition, engine.Combiner))
                .SelectMany(each => each.GetMembers("Combine"))
                .Any(combine => SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(combine), method)),
            IFieldSymbol or IPropertySymbol => true,
            _ => false
        };
        if (!runs)
            return;
        foreach (var block in context.OperationBlocks)
            foreach (var (use, member) in helpers.Uses(block))
                context.ReportDiagnostic(Diagnostic.Create(Descriptors.ImpureCombiner, use.Syntax.GetLocation(),
                    type.Name, member));
    }

    // The fix for DateTime.UtcNow and DateTimeOffset.UtcNow in a lambda that has the workflow context in scope: the
    // context's UtcNow, which reads the run's clock.
    private static ImmutableDictionary<string, string?>? ClockFix(EngineSymbols engine, IOperation use, string member)
    {
        if (member is not ("DateTime.UtcNow" or "DateTimeOffset.UtcNow") || use is not IPropertyReferenceOperation)
            return null;
        var names = new HashSet<string>();
        for (var each = use.Parent; each != null; each = each.Parent)
        {
            if (each is not IAnonymousFunctionOperation lambda)
                continue;
            var context = lambda.Symbol.Parameters.FirstOrDefault(parameter =>
                engine.IsContext(parameter.Type) && !parameter.IsDiscard && !names.Contains(parameter.Name) &&
                SyntaxFacts.GetKeywordKind(parameter.Name) == SyntaxKind.None);
            if (context != null)
                return ImmutableDictionary<string, string?>.Empty.Add(Descriptors.ClockReplacement,
                    member == "DateTime.UtcNow" ? $"{context.Name}.UtcNow.UtcDateTime" : $"{context.Name}.UtcNow");
            names.UnionWith(lambda.Symbol.Parameters.Select(parameter => parameter.Name));
        }
        return null;
    }

    /// <summary>
    /// Finds the impure members code uses, directly or through the methods, getters and constructors of the
    /// compilation it calls. What a method of the compilation leads to is kept per compilation.
    /// </summary>
    private sealed class Helpers(Compilation compilation)
    {
        private readonly ConcurrentDictionary<ISymbol, Found> _found = new(SymbolEqualityComparer.Default);

        private sealed class Found(string? member)
        {
            public string? Member { get; } = member;
        }

        /// <summary>Each use in <paramref name="code"/> of an impure member, or of a helper that leads to one.</summary>
        public IEnumerable<(IOperation Use, string Member)> Uses(IOperation code)
        {
            var uses = new List<(IOperation, string)>();
            Walk(code, new HashSet<ISymbol>(SymbolEqualityComparer.Default), (use, member) =>
            {
                uses.Add((use, member));
                return false;
            }, out _);
            return uses;
        }

        // Visits code in source order, calling note for each impure use, whose operands are not visited again; stops
        // when note returns true. Truncated is set when a helper on the current path was skipped as already in
        // progress, so the result may be incomplete.
        private void Walk(IOperation operation, HashSet<ISymbol> path, System.Func<IOperation, string, bool> note,
            out bool truncated)
        {
            truncated = false;
            var pending = new Stack<IOperation>();
            pending.Push(operation);
            while (pending.Count > 0)
            {
                var each = pending.Pop();
                if (each is INameOfOperation or ITypeOfOperation)
                    continue;
                string? member = null;
                foreach (var target in Targets(each))
                    if ((member = ImpureMembers.Named(target) ?? Through(target, path, ref truncated)) != null)
                        break;
                if (member != null)
                {
                    if (note(each, member))
                        return;
                    continue;
                }
                foreach (var child in each.ChildOperations.Reverse())
                    pending.Push(child);
            }
        }

        // The members an operation uses: what it calls, reads or creates, and what creating an object runs.
        private static IEnumerable<ISymbol> Targets(IOperation operation)
        {
            switch (operation)
            {
                case IInvocationOperation invocation:
                    yield return invocation.TargetMethod;
                    break;
                case IObjectCreationOperation { Constructor: { } constructor }:
                    yield return constructor;
                    break;
                case IPropertyReferenceOperation property:
                    yield return property.Property;
                    if (Accessor(property) is { } accessor)
                        yield return accessor;
                    break;
                case IMemberReferenceOperation member:
                    yield return member.Member;
                    break;
            }
        }

        // The accessor a property reference runs: the setter when it is assigned, the getter otherwise.
        private static IMethodSymbol? Accessor(IPropertyReferenceOperation property) =>
            property.Parent is ISimpleAssignmentOperation assignment && assignment.Target == property
                ? property.Property.SetMethod
                : property.Property.GetMethod;

        // The impure member a method of this compilation leads to, named with the method; none for other members.
        private string? Through(ISymbol target, HashSet<ISymbol> path, ref bool truncated)
        {
            if (target is not IMethodSymbol method)
                return null;
            method = method.PartialImplementationPart ?? method;
            method = method.OriginalDefinition;
            // An implicit constructor has no syntax of its own but runs its type's initializers.
            var declared = method is { MethodKind: MethodKind.Constructor, IsImplicitlyDeclared: true }
                ? method.ContainingType.DeclaringSyntaxReferences
                : method.DeclaringSyntaxReferences;
            if (method.IsAbstract || method.IsExtern || declared.Length == 0 ||
                !declared.All(reference => compilation.ContainsSyntaxTree(reference.SyntaxTree)))
                return null;
            if (_found.TryGetValue(method, out var known))
                return Named(method, known.Member);
            if (!path.Add(method))
            {
                truncated = true;
                return null;
            }
            string? first = null;
            var incomplete = false;
            foreach (var body in Bodies(method))
            {
                Walk(body, path, (_, member) =>
                {
                    first = member;
                    return true;
                }, out var cut);
                incomplete |= cut;
                if (first != null)
                    break;
            }
            path.Remove(method);
            if (first != null || !incomplete)
                _found.TryAdd(method, new Found(first));
            truncated |= incomplete && first == null;
            return Named(method, first);
        }

        private static string? Named(IMethodSymbol method, string? member) =>
            member == null ? null : $"{member} through {Describe(method)}";

        private static string Describe(IMethodSymbol method) => method switch
        {
            { AssociatedSymbol: IPropertySymbol property } => $"{property.ContainingType.Name}.{property.Name}",
            { MethodKind: MethodKind.Constructor } => $"new {method.ContainingType.Name}(...)",
            { MethodKind: MethodKind.LocalFunction } => $"{method.Name}()",
            _ => $"{method.ContainingType.Name}.{method.Name}()"
        };

        // The code a method runs: its body, and for a constructor the instance initializers of its type.
        [SuppressMessage("MicrosoftCodeAnalysisCorrectness", "RS1030:Do not invoke Compilation.GetSemanticModel()",
            Justification = "A helper's body lives in another tree; the result is kept per compilation.")]
        private IEnumerable<IOperation> Bodies(IMethodSymbol method)
        {
            foreach (var reference in method.DeclaringSyntaxReferences)
            {
                var syntax = reference.GetSyntax();
                if (compilation.GetSemanticModel(reference.SyntaxTree).GetOperation(syntax) is { } body)
                    yield return body;
            }
            if (method.MethodKind != MethodKind.Constructor)
                yield break;
            foreach (var member in method.ContainingType.GetMembers().Where(member => !member.IsStatic))
                foreach (var reference in member.DeclaringSyntaxReferences)
                {
                    var initializer = reference.GetSyntax() switch
                    {
                        VariableDeclaratorSyntax variable => variable.Initializer,
                        PropertyDeclarationSyntax property => property.Initializer,
                        _ => null
                    };
                    if (initializer != null &&
                        compilation.GetSemanticModel(reference.SyntaxTree).GetOperation(initializer) is { } value)
                        yield return value;
                }
        }
    }
}
