using System.Globalization;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

public sealed record BreakpointSource(string Path, int Line);

/// <summary>A per-instance breakpoint. Hit conditions are "== n", ">= n", or "% n".</summary>
public sealed record Breakpoint(string Id, string StepId = "", BreakpointSource? Source = null,
    string? HitCondition = null, bool Enabled = true, string? Condition = null)
{
    public string? RequestedStepId { get; init; }
    public BreakpointSource? VerifiedSource { get; init; }
    public string? StructureHash { get; init; }
    public long HitCount { get; init; }
}

/// <summary>Resolves addresses against one definition, retaining the requested source and verified line.</summary>
public static class BreakpointResolver
{
    public static IReadOnlyList<Breakpoint> Resolve(WorkflowDefinition definition, IReadOnlyList<Breakpoint> breakpoints)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(breakpoints);
        var scopes = WorkflowDefinitionValidator.ForExecution(definition);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        return breakpoints.Select(b =>
        {
            ArgumentNullException.ThrowIfNull(b);
            ArgumentException.ThrowIfNullOrWhiteSpace(b.Id);
            if (!ids.Add(b.Id)) throw new ArgumentException("Breakpoint IDs must be unique");
            _ = Matches(b.HitCondition, 1);
            StepTableEntry? step;
            if (b.Source is { } source)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(source.Path);
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(source.Line);
                step = definition.StepTable.Where(s => ParseSource(s.SourceLocation) is { } location &&
                        SamePath(location.Path, source.Path) && location.Line >= source.Line)
                    .OrderBy(s => ParseSource(s.SourceLocation)!.Line).FirstOrDefault();
            }
            else
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(b.StepId);
                step = definition.StepTable.FirstOrDefault(s => s.CatalogId == b.StepId);
                if (step == null)
                {
                    var matches = definition.StepTable.Where(s => s.AuthorId == b.StepId).ToArray();
                    if (matches.Length > 1) throw new ArgumentException($"Ambiguous author step ID '{b.StepId}'");
                    step = matches.SingleOrDefault();
                }
            }
            if (step == null) throw new ArgumentException($"Breakpoint '{b.Id}' does not resolve to a step");
            return b with { StepId = step.CatalogId, RequestedStepId = b.RequestedStepId ?? b.StepId,
                VerifiedSource = ParseSource(step.SourceLocation), StructureHash = scopes.StructureHash, HitCount = 0 };
        }).ToArray();
    }

    internal static bool Matches(string? condition, long count)
    {
        if (condition == null) return true;
        var parts = condition.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts[0] is not ("==" or ">=" or "%") ||
            !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n <= 0)
            throw new ArgumentException("Hit condition must be '== n', '>= n', or '% n', with positive n");
        return parts[0] switch { "==" => count == n, ">=" => count >= n, _ => count % n == 0 };
    }

    private static BreakpointSource? ParseSource(string? source)
    {
        var colon = source?.LastIndexOf(':') ?? -1;
        return colon > 0 && int.TryParse(source![(colon + 1)..], out var line) && line > 0
            ? new(source![..colon], line) : null;
    }

    private static bool SamePath(string left, string right) => string.Equals(
        left.Replace('\\', '/'), right.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
}

/// <summary>A stopped occurrence; activation and iteration distinguish repeated visits to a step.</summary>
public sealed record BreakpointStop(string BranchId, string StepId, string BreakpointId,
    string ActivationId, int Iteration, long HitCount, DateTime HitAtUtc)
{
    public string? Reason { get; init; }
    public IReadOnlyList<BreakpointConditionDiagnostic> Diagnostics { get; init; } = [];
}

/// <summary>A counted occurrence awaiting condition evaluation, committed before evaluation starts.</summary>
public sealed record BreakpointEvaluation(string BranchId, string StepId, string ActivationId,
    int Iteration, DateTime HitAtUtc);
