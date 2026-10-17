using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace IxIFlow.Analyzers;

/// <summary>
/// Finds the impure members workflow code uses, directly or through what it runs in the same compilation: methods,
/// local functions, property accessors, user-defined operators and conversions, constructors with their
/// initializers and the base constructors they call implicitly, and readonly fields with their initializers. Local
/// functions are followed where they are called, not where they are declared. What a member leads to is kept per
/// compilation; a chain deeper than <see cref="MaxDepth"/> is not followed.
/// </summary>
internal sealed class WorkflowCode(Compilation compilation)
{
    private const int MaxDepth = 32;

    private readonly ConcurrentDictionary<ISymbol, Found> _found = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<SyntaxTree, SemanticModel> _models = new();

    private sealed class Found(string? member)
    {
        public string? Member { get; } = member;
    }

    /// <summary>
    /// Each use in <paramref name="code"/> of an impure member, or of a member that leads to one. In a
    /// <paramref name="destination"/> lambda the property the lambda ends in is set, so its setter is what runs.
    /// </summary>
    public List<(IOperation Use, string Member)> Uses(IOperation code, bool destination, CancellationToken cancellation)
    {
        var uses = new List<(IOperation, string)>();
        var assigned = destination && code is IAnonymousFunctionOperation { Body.Operations.Length: 1 } lambda &&
                       lambda.Body.Operations[0] is IReturnOperation { ReturnedValue: { } returned }
            ? EngineSymbols.Unwrapped(returned)
            : null;
        Walk(code, assigned, new HashSet<ISymbol>(SymbolEqualityComparer.Default), (use, member) =>
        {
            uses.Add((use, member));
            return false;
        }, cancellation, out _);
        return uses;
    }

    /// <summary>The impure member <paramref name="target"/> leads to, named with the way there; none when pure.</summary>
    public string? Leads(ISymbol target, CancellationToken cancellation)
    {
        var truncated = false;
        return Through(target, new HashSet<ISymbol>(SymbolEqualityComparer.Default), cancellation, ref truncated);
    }

    // Visits code in source order, calling note for each impure use, whose operands are not visited again; stops
    // when note returns true. Truncated is set when a member on the current path was skipped as already in progress
    // or too deep, so the result may be incomplete.
    private void Walk(IOperation operation, IOperation? assigned, HashSet<ISymbol> path,
        System.Func<IOperation, string, bool> note, CancellationToken cancellation, out bool truncated)
    {
        truncated = false;
        var pending = new Stack<IOperation>();
        pending.Push(operation);
        while (pending.Count > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            var each = pending.Pop();
            if (each is INameOfOperation or ITypeOfOperation || each is ILocalFunctionOperation && each != operation)
                continue;
            string? member = null;
            foreach (var target in Targets(each, assigned))
                if ((member = ImpureMembers.Named(target) ?? Through(target, path, cancellation, ref truncated)) != null)
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

    // The members an operation runs or uses: what it calls, reads, sets or creates, and the operator it applies.
    private static IEnumerable<ISymbol> Targets(IOperation operation, IOperation? assigned)
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
                var set = operation == assigned ||
                          property.Parent is IAssignmentOperation assignment && assignment.Target == property;
                if ((set ? property.Property.SetMethod : property.Property.GetMethod) is { } accessor)
                    yield return accessor;
                break;
            case IMemberReferenceOperation member:
                yield return member.Member;
                break;
            case IBinaryOperation { OperatorMethod: { } binary }:
                yield return binary;
                break;
            case IUnaryOperation { OperatorMethod: { } unary }:
                yield return unary;
                break;
            case IIncrementOrDecrementOperation { OperatorMethod: { } step }:
                yield return step;
                break;
            case ICompoundAssignmentOperation { OperatorMethod: { } compound }:
                yield return compound;
                break;
            case IConversionOperation { OperatorMethod: { } conversion }:
                yield return conversion;
                break;
        }
    }

    // The impure member a member of this compilation leads to, named with the member; none for other members.
    private string? Through(ISymbol target, HashSet<ISymbol> path, CancellationToken cancellation, ref bool truncated)
    {
        var key = Key(target);
        if (key == null)
            return null;
        if (_found.TryGetValue(key, out var known))
            return Named(key, known.Member);
        if (path.Count >= MaxDepth || !path.Add(key))
        {
            truncated = true;
            return null;
        }
        string? first = null;
        var incomplete = false;
        foreach (var body in Runs(key, cancellation))
        {
            Walk(body, null, path, (_, member) =>
            {
                first = member;
                return true;
            }, cancellation, out var cut);
            incomplete |= cut;
            if (first != null)
                break;
        }
        if (first == null && key is IMethodSymbol method && ImplicitBase(method, cancellation) is { } constructor)
            first = Through(constructor, path, cancellation, ref incomplete);
        path.Remove(key);
        if (first != null || !incomplete)
            _found.TryAdd(key, new Found(first));
        truncated |= incomplete && first == null;
        return Named(key, first);
    }

    // The member as it is looked into, when the compilation has its code: a method's implementation, or a readonly
    // field with an initializer.
    private ISymbol? Key(ISymbol target)
    {
        switch (target)
        {
            case IMethodSymbol method:
                method = (method.PartialImplementationPart ?? method).OriginalDefinition;
                // An implicit constructor has no syntax of its own but runs its type's initializers.
                var declared = method is { MethodKind: MethodKind.Constructor, IsImplicitlyDeclared: true }
                    ? method.ContainingType.DeclaringSyntaxReferences
                    : method.DeclaringSyntaxReferences;
                return method.IsAbstract || method.IsExtern || !InCompilation(declared) ? null : method;
            case IFieldSymbol { IsReadOnly: true } field:
                field = field.OriginalDefinition;
                return InCompilation(field.DeclaringSyntaxReferences) ? field : null;
            default:
                return null;
        }
    }

    private bool InCompilation(System.Collections.Immutable.ImmutableArray<SyntaxReference> references) =>
        references.Length > 0 && references.All(reference => compilation.ContainsSyntaxTree(reference.SyntaxTree));

    // The code a member runs: a method's body, a constructor's with its type's initializers, a field's initializer.
    private IEnumerable<IOperation> Runs(ISymbol key, CancellationToken cancellation) => key switch
    {
        IMethodSymbol method => Bodies(method, withInitializers: true, cancellation),
        IFieldSymbol field => field.DeclaringSyntaxReferences
            .Select(reference => reference.GetSyntax(cancellation))
            .OfType<VariableDeclaratorSyntax>()
            .Select(variable => variable.Initializer)
            .Where(initializer => initializer != null)
            .Select(initializer => Model(initializer!.SyntaxTree).GetOperation(initializer, cancellation))
            .OfType<IOperation>(),
        _ => []
    };

    /// <summary>
    /// The code a method runs: its body, and for a constructor, when <paramref name="withInitializers"/>, the
    /// initializers of its type that run with it.
    /// </summary>
    public IEnumerable<IOperation> Bodies(IMethodSymbol method, bool withInitializers, CancellationToken cancellation)
    {
        foreach (var reference in method.DeclaringSyntaxReferences)
        {
            var syntax = reference.GetSyntax(cancellation);
            // A constructor declared on its type is a primary constructor, which runs the arguments it gives its base;
            // the rest of the type's code is not the constructor's.
            if (syntax is TypeDeclarationSyntax type)
            {
                if (type.ParameterList != null && type.BaseList != null)
                    foreach (var call in type.BaseList.Types.OfType<PrimaryConstructorBaseTypeSyntax>())
                        if (Model(reference.SyntaxTree).GetOperation(call, cancellation) is { } arguments)
                            yield return arguments;
                continue;
            }
            if (Model(reference.SyntaxTree).GetOperation(syntax, cancellation) is { } body)
                yield return body;
        }
        if (!withInitializers || method.MethodKind is not (MethodKind.Constructor or MethodKind.StaticConstructor))
            yield break;
        // A constructor that chains to another of its type with this(...) leaves the initializers to that one, which
        // is walked through the call.
        if (method.DeclaringSyntaxReferences.Any(reference =>
                reference.GetSyntax(cancellation) is ConstructorDeclarationSyntax
                {
                    Initializer.ThisOrBaseKeyword.Text: "this"
                }))
            yield break;
        foreach (var initializer in Initializers(method.ContainingType, method.IsStatic, cancellation))
            yield return initializer;
    }

    /// <summary>The field and property initializers of a type, its static or its instance ones.</summary>
    public IEnumerable<IOperation> Initializers(INamedTypeSymbol type, bool isStatic, CancellationToken cancellation)
    {
        foreach (var member in type.GetMembers().Where(member => member.IsStatic == isStatic))
            foreach (var reference in member.DeclaringSyntaxReferences)
            {
                var initializer = reference.GetSyntax(cancellation) switch
                {
                    VariableDeclaratorSyntax variable => (SyntaxNode?)variable.Initializer,
                    PropertyDeclarationSyntax property => property.Initializer,
                    _ => null
                };
                if (initializer != null &&
                    Model(reference.SyntaxTree).GetOperation(initializer, cancellation) is { } value)
                    yield return value;
            }
    }

    /// <summary>
    /// The base constructor an instance constructor calls without saying so: an implicit constructor's, or that of an
    /// explicit or primary one that names no base call. As the compiler binds it: the base constructor without
    /// parameters, or else the only one whose parameters are all optional.
    /// </summary>
    public static IMethodSymbol? ImplicitBase(IMethodSymbol constructor, CancellationToken cancellation)
    {
        if (constructor.MethodKind != MethodKind.Constructor || constructor.IsStatic ||
            !constructor.IsImplicitlyDeclared && !constructor.DeclaringSyntaxReferences.All(reference =>
                reference.GetSyntax(cancellation) switch
                {
                    ConstructorDeclarationSyntax declaration => declaration.Initializer == null,
                    TypeDeclarationSyntax type => type.BaseList?.Types.OfType<PrimaryConstructorBaseTypeSyntax>().Any() != true,
                    _ => false
                }))
            return null;
        var candidates = constructor.ContainingType.BaseType?.InstanceConstructors ?? [];
        if (candidates.FirstOrDefault(each => each.Parameters.Length == 0) is { } parameterless)
            return parameterless;
        var optional = candidates
            .Where(each => each.Parameters.All(parameter => parameter.IsOptional || parameter.IsParams))
            .ToList();
        return optional.Count == 1 ? optional[0] : null;
    }

    private static string? Named(ISymbol key, string? member) =>
        member == null ? null : $"{member} through {Describe(key)}";

    private static string Describe(ISymbol key) => key switch
    {
        IMethodSymbol { AssociatedSymbol: IPropertySymbol property } => $"{property.ContainingType.Name}.{property.Name}",
        IMethodSymbol { MethodKind: MethodKind.Constructor } method => $"new {method.ContainingType.Name}(...)",
        IMethodSymbol { MethodKind: MethodKind.StaticConstructor } method => $"static {method.ContainingType.Name}()",
        IMethodSymbol { MethodKind: MethodKind.LocalFunction } method => $"{method.Name}()",
        IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion } method =>
            method.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
        IMethodSymbol method => $"{method.ContainingType.Name}.{method.Name}()",
        _ => $"{key.ContainingType.Name}.{key.Name}"
    };

    [SuppressMessage("MicrosoftCodeAnalysisCorrectness", "RS1030:Do not invoke Compilation.GetSemanticModel()",
        Justification = "Code that workflow code calls lives in other trees; one model per tree is kept per compilation.")]
    private SemanticModel Model(SyntaxTree tree) => _models.GetOrAdd(tree, each => compilation.GetSemanticModel(each));
}
