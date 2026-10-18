using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace IxIFlow.Analyzers;

/// <summary>The engine's types the analyzers recognise, resolved once per compilation.</summary>
internal sealed class EngineSymbols
{
    private EngineSymbols(Compilation compilation, INamedTypeSymbol activity)
    {
        Compilation = compilation;
        Activity = activity;
        Input = compilation.GetTypeByMetadataName("IxIFlow.ActivitySdk.ActivityInputAttribute");
        Output = compilation.GetTypeByMetadataName("IxIFlow.ActivitySdk.ActivityOutputAttribute");
        InputOutput = compilation.GetTypeByMetadataName("IxIFlow.ActivitySdk.ActivityInputOutputAttribute");
        Required = compilation.GetTypeByMetadataName("System.ComponentModel.DataAnnotations.RequiredAttribute");
        DefaultValue = compilation.GetTypeByMetadataName("System.ComponentModel.DefaultValueAttribute");
        Combiner = compilation.GetTypeByMetadataName("IxIFlow.Core.ICombiner`1");
        Context = compilation.GetTypeByMetadataName("IxIFlow.Core.WorkflowContext`1");
        Expression = compilation.GetTypeByMetadataName("System.Linq.Expressions.Expression`1");
        Builders = compilation.GetTypeByMetadataName("IxIFlow.Builders.Workflow")?.ContainingAssembly;
    }

    public Compilation Compilation { get; }
    public INamedTypeSymbol Activity { get; }
    public INamedTypeSymbol? Input { get; }
    public INamedTypeSymbol? Output { get; }
    public INamedTypeSymbol? InputOutput { get; }
    public INamedTypeSymbol? Required { get; }
    public INamedTypeSymbol? DefaultValue { get; }
    public INamedTypeSymbol? Combiner { get; }
    public INamedTypeSymbol? Context { get; }
    public INamedTypeSymbol? Expression { get; }

    // The assembly of the fluent builders, the engine as the compilation references it.
    private IAssemblySymbol? Builders { get; }

    /// <summary>The engine's symbols, or none when the compilation does not reference IxIFlow.</summary>
    public static EngineSymbols? For(Compilation compilation) =>
        compilation.GetTypeByMetadataName("IxIFlow.Core.IAsyncActivity") is { } activity
            ? new EngineSymbols(compilation, activity)
            : null;

    /// <summary>True for a method of the engine's fluent builders, not one the compilation declares itself.</summary>
    public bool IsBuilderMethod(IMethodSymbol method) =>
        Builders != null && SymbolEqualityComparer.Default.Equals(method.ContainingAssembly, Builders) &&
        !SymbolEqualityComparer.Default.Equals(Builders, Compilation.Assembly) &&
        InNamespace(method.ContainingType, "IxIFlow.Builders");

    public static bool InNamespace(ITypeSymbol type, string name)
    {
        var space = type.ContainingNamespace?.ToDisplayString();
        return space != null && (space == name || space.StartsWith(name + ".", System.StringComparison.Ordinal));
    }

    public bool IsActivity(ITypeSymbol type) =>
        type is INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Struct } named &&
        named.AllInterfaces.Contains(Activity, SymbolEqualityComparer.Default);

    /// <summary>True for a <c>Func</c> or an <c>Expression</c> of one: a parameter that takes workflow code.</summary>
    public bool TakesCode(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol expression && Expression != null &&
            SymbolEqualityComparer.Default.Equals(expression.OriginalDefinition, Expression))
            type = expression.TypeArguments[0];
        return type is INamedTypeSymbol { TypeKind: TypeKind.Delegate, Name: "Func" } func &&
               func.ContainingNamespace?.ToDisplayString() == "System";
    }

    /// <summary>True when <paramref name="type"/> is a workflow context, whose <c>UtcNow</c> is the run's clock.</summary>
    public bool IsContext(ITypeSymbol type)
    {
        for (var each = type as INamedTypeSymbol; each != null; each = each.BaseType)
            if (Context != null && SymbolEqualityComparer.Default.Equals(each.OriginalDefinition, Context))
                return true;
        return false;
    }

    // The locals each operation tree writes after declaring them, found once per tree.
    private static readonly ConditionalWeakTable<IOperation, HashSet<ILocalSymbol>> Written = new();

    /// <summary>The value a local is declared with, when nothing writes it afterwards; none otherwise.</summary>
    public static IOperation? Initializer(ILocalReferenceOperation reference, CancellationToken cancellation)
    {
        var local = reference.Local;
        if (local.DeclaringSyntaxReferences.Length != 1 || reference.SemanticModel == null ||
            reference.SemanticModel.GetOperation(local.DeclaringSyntaxReferences[0].GetSyntax(cancellation), cancellation)
                is not IVariableDeclaratorOperation { Initializer.Value: { } value })
            return null;
        var root = (IOperation)reference;
        while (root.Parent != null)
            root = root.Parent;
        return Written.GetValue(root, WrittenLocals).Contains(local) ? null : value;
    }

    private static HashSet<ILocalSymbol> WrittenLocals(IOperation root)
    {
        var written = new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default);
        foreach (var operation in root.Descendants())
            switch (operation)
            {
                case IAssignmentOperation assignment:
                    Writes(assignment.Target, written);
                    break;
                case IIncrementOrDecrementOperation step:
                    Writes(step.Target, written);
                    break;
                case IArgumentOperation { Parameter.RefKind: RefKind.Ref or RefKind.Out } argument:
                    Writes(argument.Value, written);
                    break;
            }
        return written;
    }

    // The locals a target writes: itself, or each element of a tuple it deconstructs into.
    private static void Writes(IOperation target, HashSet<ILocalSymbol> written)
    {
        switch (target)
        {
            case ILocalReferenceOperation local:
                written.Add(local.Local);
                break;
            case ITupleOperation tuple:
                foreach (var element in tuple.Elements)
                    Writes(element, written);
                break;
            case IDeclarationExpressionOperation declaration:
                Writes(declaration.Expression, written);
                break;
            case IConversionOperation conversion:
                Writes(conversion.Operand, written);
                break;
        }
    }

    /// <summary>The value an argument carries, without the conversions and delegate creation around it.</summary>
    public static IOperation Unwrapped(IOperation operation)
    {
        while (true)
        {
            switch (operation)
            {
                case IConversionOperation conversion:
                    operation = conversion.Operand;
                    break;
                case IDelegateCreationOperation creation:
                    operation = creation.Target;
                    break;
                case IParenthesizedOperation parenthesized:
                    operation = parenthesized.Operand;
                    break;
                default:
                    return operation;
            }
        }
    }
}
