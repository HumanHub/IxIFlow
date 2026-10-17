using System.Linq;
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
        Input = compilation.GetTypeByMetadataName("IxIFlow.ActivitySdk.WorkflowInputAttribute");
        Output = compilation.GetTypeByMetadataName("IxIFlow.ActivitySdk.WorkflowOutputAttribute");
        Combiner = compilation.GetTypeByMetadataName("IxIFlow.Core.ICombiner`1");
        Context = compilation.GetTypeByMetadataName("IxIFlow.Core.WorkflowContext`1");
        Expression = compilation.GetTypeByMetadataName("System.Linq.Expressions.Expression`1");
    }

    public Compilation Compilation { get; }
    public INamedTypeSymbol Activity { get; }
    public INamedTypeSymbol? Input { get; }
    public INamedTypeSymbol? Output { get; }
    public INamedTypeSymbol? Combiner { get; }
    public INamedTypeSymbol? Context { get; }
    public INamedTypeSymbol? Expression { get; }

    /// <summary>The engine's symbols, or none when the compilation does not reference IxIFlow.</summary>
    public static EngineSymbols? For(Compilation compilation) =>
        compilation.GetTypeByMetadataName("IxIFlow.Core.IAsyncActivity") is { } activity
            ? new EngineSymbols(compilation, activity)
            : null;

    /// <summary>True for a method of the engine's fluent builders, not one the compilation declares itself.</summary>
    public bool IsBuilderMethod(IMethodSymbol method) =>
        IsEngine(method) && InNamespace(method.ContainingType, "IxIFlow.Builders");

    /// <summary>True for a member of the engine, declared in an IxIFlow namespace of a referenced assembly.</summary>
    public bool IsEngine(ISymbol member) =>
        member.ContainingType != null && InNamespace(member.ContainingType, "IxIFlow") &&
        !SymbolEqualityComparer.Default.Equals(member.ContainingAssembly, Compilation.Assembly);

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
