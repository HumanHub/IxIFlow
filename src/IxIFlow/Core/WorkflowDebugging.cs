using System.Text.Json;
namespace IxIFlow.Core;

public enum DebugCommandKind { Pause, Continue }
public enum DebugCommandStatus { Accepted, AlreadyAccepted, StaleGeneration, InvalidState, NotFound }

/// <summary>A durable ordered command. Acknowledgement is the revision that applied it.</summary>
public sealed record DebugCommand(string Id, long Order, DebugCommandKind Kind,
    long PauseGeneration, string Actor, long? AcknowledgedRevision = null);
public sealed record DebugCommandRequest(string Id, DebugCommandKind Kind, long PauseGeneration, string Actor);
public sealed record DebugCommandResult(DebugCommandStatus Status, DebugCommand? Command = null);

/// <summary>Versioned commands outside the checkpoint. Version is the last allocated order.</summary>
public sealed record WorkflowDebugState(long Version, long PauseGeneration, IReadOnlyList<DebugCommand> Commands);
public sealed record WorkflowControlRequests(IReadOnlyDictionary<string, CancellationReason> Cancellations,
    IReadOnlyDictionary<string, WorkflowDebugState> DebugStates);

/// <summary>The durable execution hold and the command prefix applied by a commit.</summary>
public sealed record DebugCheckpoint(long PauseGeneration = 0, long AppliedOrder = 0);

/// <summary>Command storage, serialized with instance commits, event appends and deletion.</summary>
public interface IWorkflowDebugStore
{
    /// <summary>
    /// Reads controls for distinct instances, omitting absent requests. Successful reads survive
    /// failures of other reads; if every read fails, throws AggregateException.
    /// </summary>
    Task<WorkflowControlRequests> GetControlRequestsAsync(IReadOnlyCollection<string> instanceIds);
    Task<DebugCommandResult> RequestDebugCommandAsync(string instanceId, DebugCommandRequest command);
    Task<IReadOnlyDictionary<string, WorkflowDebugState>> GetDebugStatesAsync(IReadOnlyCollection<string> instanceIds);
}

/// <summary>Authorized in-process debugger operations. The application authorizes the actor.</summary>
public interface IWorkflowOperations
{
    /// <summary>Requests a durable pause; running activities finish before it is committed.</summary>
    Task<WorkflowExecutionResult> PauseAsync(string instanceId, string actor, CancellationToken cancellationToken = default);
    /// <summary>Continues a pause under a fresh lease on this host.</summary>
    Task<WorkflowExecutionResult> ContinueAsync(string instanceId, string actor, CancellationToken cancellationToken = default);
}

/// <summary>Shared command rules used under each store's instance lock.</summary>
public static class WorkflowDebugRules
{
    public static long Version(IReadOnlyList<DebugCommand> commands) => checked(
        (commands.LastOrDefault()?.Order ?? 0) + commands.Count(c => c.AcknowledgedRevision != null));

    public static DebugCheckpoint Checkpoint(WorkflowInstance instance)
    {
        using var json = JsonDocument.Parse(instance.ExecutionStateJson);
        return json.RootElement.TryGetProperty("Debug", out var debug) && debug.ValueKind != JsonValueKind.Null
            ? debug.Deserialize<DebugCheckpoint>()! : new();
    }

    public static DebugCommandResult Validate(WorkflowInstance? instance, IReadOnlyList<DebugCommand> commands,
        DebugCommandRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Actor);
        ArgumentOutOfRangeException.ThrowIfNegative(request.PauseGeneration);
        if (!Enum.IsDefined(request.Kind)) throw new ArgumentOutOfRangeException(nameof(request));
        if (commands.FirstOrDefault(c => c.Id == request.Id) is { } existing)
        {
            if (existing.Kind != request.Kind || existing.Actor != request.Actor || existing.PauseGeneration != request.PauseGeneration)
                throw new ArgumentException("A debug command ID cannot be reused with different fields");
            return new(DebugCommandStatus.AlreadyAccepted, existing);
        }
        if (instance == null) return new(DebugCommandStatus.NotFound);
        if (WorkflowStoreRules.IsTerminal(instance.Status)) return new(DebugCommandStatus.InvalidState);
        var checkpoint = Checkpoint(instance);
        var pendingPause = commands.Any(c => c.AcknowledgedRevision == null && c.Kind == DebugCommandKind.Pause);
        var generation = checkpoint.PauseGeneration + (request.Kind == DebugCommandKind.Continue &&
            instance.Status != WorkflowStatus.Paused && pendingPause ? 1 : 0);
        if (request.PauseGeneration != generation) return new(DebugCommandStatus.StaleGeneration);
        if (commands.Any(c => c.AcknowledgedRevision == null && c.Kind == request.Kind))
            return new(DebugCommandStatus.InvalidState);
        if (request.Kind == DebugCommandKind.Continue && instance.Status != WorkflowStatus.Paused && !pendingPause ||
            request.Kind == DebugCommandKind.Pause && instance.Status == WorkflowStatus.Paused)
            return new(DebugCommandStatus.InvalidState);
        return new(DebugCommandStatus.Accepted, new(request.Id,
            commands.Count == 0 ? 1 : checked(commands.Max(c => c.Order) + 1),
            request.Kind, request.PauseGeneration, request.Actor));
    }

    public static IReadOnlyList<DebugCommand> Acknowledge(WorkflowInstance? previous, WorkflowInstance next,
        IReadOnlyList<DebugCommand> commands, long revision)
    {
        var before = previous == null ? new DebugCheckpoint() : Checkpoint(previous);
        var after = Checkpoint(next);
        var applied = commands.Where(c => c.Order > before.AppliedOrder && c.Order <= after.AppliedOrder).ToArray();
        if (applied.Length > 1) throw new InvalidOperationException("A pause and its continue require separate lease assignments");
        if (applied.SingleOrDefault() is { Kind: DebugCommandKind.Continue } && previous?.Status != WorkflowStatus.Paused)
            throw new InvalidOperationException("Continue requires a committed pause");
        var generation = before.PauseGeneration;
        foreach (var command in applied)
        {
            if (command.PauseGeneration != generation) throw new InvalidOperationException("Stale debug command in commit");
            if (command.Kind == DebugCommandKind.Pause) generation++;
        }
        if (after.AppliedOrder < before.AppliedOrder || after.PauseGeneration != generation ||
            after.AppliedOrder != (applied.LastOrDefault()?.Order ?? before.AppliedOrder))
            throw new InvalidOperationException("Commit does not apply a valid ordered debug command prefix");
        if (applied.LastOrDefault() is { } last && !WorkflowStoreRules.IsTerminal(next.Status) &&
            (last.Kind == DebugCommandKind.Pause) != (next.Status == WorkflowStatus.Paused))
            throw new InvalidOperationException("Debug command acknowledgement does not match committed status");
        return commands.Select(c => c.Order <= after.AppliedOrder && c.AcknowledgedRevision == null
            ? c with { AcknowledgedRevision = revision } : c).ToArray();
    }
    public static bool NeedsWake(WorkflowStatus status, IReadOnlyList<DebugCommand> commands) =>
        !WorkflowStoreRules.IsTerminal(status) && commands.Any(c => c.AcknowledgedRevision == null &&
            (status != WorkflowStatus.Paused || c.Kind == DebugCommandKind.Continue));
}
