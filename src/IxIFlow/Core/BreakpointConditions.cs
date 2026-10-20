namespace IxIFlow.Core;

/// <summary>A diagnostic positioned in the condition text, with one-based line and column.</summary>
public sealed record BreakpointConditionDiagnostic(string Code, string Message, int Start, int Length, int Line, int Column);

/// <summary>The typed inputs available at a breakpoint. This is a local compiler request, not an execution message.</summary>
public sealed record BreakpointConditionRequest(string StructureHash, string Text, Type DataType,
    Type? PreviousType = null, Type? ItemType = null);

/// <summary>A local compiled predicate, or positioned diagnostics when compilation was refused.</summary>
public sealed record BreakpointConditionCompilation(
    Func<object, object?, object?, int?, DateTimeOffset, bool>? Evaluate,
    IReadOnlyList<BreakpointConditionDiagnostic> Diagnostics);

/// <summary>Optional host capability. The Roslyn implementation ships in IxIFlow.Debugging.</summary>
public interface IBreakpointConditionCompiler
{
    BreakpointConditionCompilation Compile(BreakpointConditionRequest request);
}

/// <summary>Read-only condition inputs. Item and Index name the innermost ForEach body.</summary>
public sealed class BreakpointConditionContext<TData, TPrevious, TItem>(TData data, TPrevious previous,
    TItem item, int? index, DateTimeOffset utcNow)
{
    public TData WorkflowData { get; } = data;
    public TPrevious PreviousStep { get; } = previous;
    public TItem Item { get; } = item;
    public int? Index { get; } = index;
    public DateTimeOffset UtcNow { get; } = utcNow;
}

/// <summary>A refused condition leaves the previously installed breakpoint set intact.</summary>
public sealed class BreakpointConditionException(IReadOnlyList<BreakpointConditionDiagnostic> diagnostics)
    : ArgumentException(string.Join(Environment.NewLine, diagnostics.Select(d =>
        $"{d.Code} ({d.Line},{d.Column}): {d.Message}")))
{
    public IReadOnlyList<BreakpointConditionDiagnostic> Diagnostics { get; } = diagnostics;
}
