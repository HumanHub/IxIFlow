using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

/// <summary>The operator intervention holding an instance. An explicit stop takes precedence over activities.</summary>
public enum WorkflowStopKind
{
    ActivityResolution,
    ResourceLimit,
    Quarantine,
    Verification
}

/// <summary>Combined row filters. Time ranges include From and exclude To. Pages use creation descending, ID ordinal ascending.</summary>
public sealed record InstanceQuery(
    IReadOnlyCollection<WorkflowStatus>? Statuses = null,
    WorkflowStopKind? StopKind = null, string? WorkflowName = null, int? WorkflowVersion = null,
    string? CorrelationId = null, string? ParentInstanceId = null,
    DateTime? CreatedFrom = null, DateTime? CreatedTo = null,
    DateTime? EndedFrom = null, DateTime? EndedTo = null,
    string? After = null, int Limit = 100, string? InstanceId = null)
{
    internal (DateTime Created, string Id)? Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Limit);
        if (Statuses?.Any(status => !Enum.IsDefined(status)) == true || StopKind is { } kind && !Enum.IsDefined(kind))
            throw new ArgumentException("Unknown status or stop kind");
        if (CreatedFrom > CreatedTo || EndedFrom > EndedTo)
            throw new ArgumentException("A range must be ordered");
        if (After == null) return null;
        try
        {
            var cursor = JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(After));
            if (cursor is not { Version: 1 } || cursor.Scope != Scope() || string.IsNullOrEmpty(cursor.Id))
                throw new FormatException();
            return (new DateTime(cursor.Ticks, DateTimeKind.Utc), cursor.Id);
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException)
        {
            throw new ArgumentException("Invalid instance cursor for these filters", nameof(After), error);
        }
    }

    private string Scope() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
        this with { After = null, Limit = 1, Statuses = Statuses?.Distinct().Order().ToArray() }))));

    internal InstancePage Page(IReadOnlyList<InstanceSummary> rows)
    {
        var items = rows.Take(Limit).ToArray();
        return new(items, rows.Count > Limit ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            new Cursor(1, items[^1].CreatedAtUtc.Ticks, items[^1].InstanceId, Scope()))) : null);
    }

    private sealed record Cursor(int Version, long Ticks, string Id, string Scope);
}

/// <summary>A persisted row projection, without workflow data, execution state or capability identifiers.</summary>
public sealed record InstanceSummary(string InstanceId, string WorkflowName, int WorkflowVersion,
    WorkflowStatus Status, WorkflowStopKind? StopKind, int? CrashCount, string? CorrelationId,
    string? ParentInstanceId, DateTime CreatedAtUtc, DateTime? EndedAtUtc, DateTime? RunnableAtUtc,
    long Revision, Guid Incarnation)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ParentWaitId { get; init; }

    internal static InstanceSummary Of(WorkflowInstance instance)
    {
        var checkpoint = ExecutionCheckpoint.ReadTrunk(instance.ExecutionStateJson);
        var terminal = WorkflowStoreRules.IsTerminal(instance.Status);
        WorkflowStopKind? kind = terminal ? null : checkpoint.OperatorStop?.Kind switch
        {
            OperatorStopKind.ResourceLimit => WorkflowStopKind.ResourceLimit,
            OperatorStopKind.Quarantine => WorkflowStopKind.Quarantine,
            OperatorStopKind.Verification => WorkflowStopKind.Verification,
            _ => (WorkflowStopKind?)null
        } ??
            (instance.Status == WorkflowStatus.NeedsResolution ? WorkflowStopKind.ActivityResolution : (WorkflowStopKind?)null);
        return new(instance.InstanceId, instance.WorkflowName, instance.WorkflowVersion, instance.Status,
            kind, kind == WorkflowStopKind.Quarantine ? checkpoint.OperatorStop?.CrashCount : null,
            instance.CorrelationId, instance.ParentInstanceId, instance.CreatedAt, instance.CompletedAt,
            instance.RunnableAtUtc, instance.Revision, instance.Incarnation) { ParentWaitId = instance.ParentWaitId };
    }
}

public sealed record InstancePage(IReadOnlyList<InstanceSummary> Items, string? NextCursor);

/// <summary>An open event wait. Contains no capability ID or reply token.</summary>
public sealed record PendingWait(string InstanceId, string? Key, string? EventType,
    string? DefaultPayloadJson, DateTime? DeadlineUtc, DateTime OpenedAtUtc);
