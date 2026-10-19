using System.Text.Json;
using System.Text.Json.Serialization;
namespace IxIFlow.Core;

public enum DebugCommandKind { Pause, Continue, SetBreakpoints, Step }
public enum DebugStepKind { Over, Into, Out, Run }

/// <summary>A pick occurrence, including progress through repeated loop and item visits.</summary>
public sealed record DebugStepPosition(string ActivationId, int StepIndex, int Iteration,
    int ItemCursor, bool ItemsJoined);

/// <summary>The durable origin and the continuations admitted by one isolated step.</summary>
public sealed record DebugStepping(string ContinuationId, DebugStepKind Kind, DebugStepPosition Origin,
    IReadOnlyList<string> OriginScopes, IReadOnlyList<string> EligibleContinuations,
    string CommandId, string Actor, bool Started = false);
public enum DebugCommandStatus { Accepted, AlreadyAccepted, StaleGeneration, InvalidState, NotFound }

/// <summary>A durable ordered command. Acknowledgement is the revision that applied it.</summary>
public sealed record DebugCommand(string Id, long Order, DebugCommandKind Kind,
    long PauseGeneration, string Actor, long? AcknowledgedRevision = null,
    IReadOnlyList<Breakpoint>? Breakpoints = null, string? ContinuationId = null, DebugStepKind? StepKind = null);
public sealed record DebugCommandRequest(string Id, DebugCommandKind Kind, long PauseGeneration, string Actor,
    IReadOnlyList<Breakpoint>? Breakpoints = null, string? ContinuationId = null, DebugStepKind? StepKind = null);
public sealed record DebugCommandResult(DebugCommandStatus Status, DebugCommand? Command = null);

/// <summary>Versioned commands outside the checkpoint. Version is the last allocated order.</summary>
public sealed record WorkflowDebugState(long Version, long PauseGeneration, IReadOnlyList<DebugCommand> Commands);
public sealed record WorkflowControlRequests(IReadOnlyDictionary<string, CancellationReason> Cancellations,
    IReadOnlyDictionary<string, WorkflowDebugState> DebugStates);

/// <summary>The durable execution hold and the command prefix applied by a commit.</summary>
public sealed record DebugCheckpoint(long PauseGeneration = 0, long AppliedOrder = 0)
{
    public IReadOnlyList<Breakpoint> Breakpoints { get; init; } = [];
    public BreakpointStop? Stop { get; init; }
    public BreakpointEvaluation? Evaluation { get; init; }
    public IReadOnlyList<BreakpointStop> SkipOnce { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DebugStepping? Stepping { get; init; }
}

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
    /// <summary>Runs only the selected continuation and branches it creates, under a fresh lease.</summary>
    Task<WorkflowExecutionResult> StepAsync(string instanceId, string continuationId, DebugStepKind kind,
        string actor, CancellationToken cancellationToken = default);
    /// <summary>Replaces the instance's breakpoint set and returns the resolved addresses.</summary>
    Task<IReadOnlyList<Breakpoint>> SetBreakpointsAsync(string instanceId, IReadOnlyList<Breakpoint> breakpoints,
        string actor, CancellationToken cancellationToken = default);
    /// <summary>Reads the latest requested set with committed hit counts.</summary>
    Task<IReadOnlyList<Breakpoint>> GetBreakpointsAsync(string instanceId, CancellationToken cancellationToken = default);
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
            if (existing.Kind != request.Kind || existing.Actor != request.Actor || existing.PauseGeneration != request.PauseGeneration ||
                existing.ContinuationId != request.ContinuationId || existing.StepKind != request.StepKind ||
                JsonSerializer.Serialize(existing.Breakpoints) != JsonSerializer.Serialize(request.Breakpoints))
                throw new ArgumentException("A debug command ID cannot be reused with different fields");
            return new(DebugCommandStatus.AlreadyAccepted, existing);
        }
        if (instance == null) return new(DebugCommandStatus.NotFound);
        if (WorkflowStoreRules.IsTerminal(instance.Status)) return new(DebugCommandStatus.InvalidState);
        var checkpoint = Checkpoint(instance);
        if (request.Kind == DebugCommandKind.Step)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request.ContinuationId);
            if (request.StepKind == null || !Enum.IsDefined(request.StepKind.Value))
                throw new ArgumentException("Step requires a valid kind");
        }
        else if (request.ContinuationId != null || request.StepKind != null)
            throw new ArgumentException("Only Step carries a continuation and step kind");
        if (request.Kind == DebugCommandKind.SetBreakpoints)
        {
            ArgumentNullException.ThrowIfNull(request.Breakpoints);
            using var state = JsonDocument.Parse(instance.ExecutionStateJson);
            var hash = state.RootElement.GetProperty("StructureHash").GetString();
            if (request.Breakpoints.Select(b => b.Id).Distinct(StringComparer.Ordinal).Count() != request.Breakpoints.Count ||
                request.Breakpoints.Any(b => string.IsNullOrWhiteSpace(b.Id) || string.IsNullOrWhiteSpace(b.StepId) ||
                    b.StructureHash != hash || b.HitCount != 0))
                throw new ArgumentException("Breakpoint commands require unique resolved breakpoints for this structure hash");
            foreach (var breakpoint in request.Breakpoints) _ = BreakpointResolver.Matches(breakpoint.HitCondition, 1);
        }
        else if (request.Breakpoints != null) throw new ArgumentException("Only SetBreakpoints carries breakpoints");
        var pendingPause = commands.Any(c => c.AcknowledgedRevision == null && c.Kind == DebugCommandKind.Pause);
        var generation = checkpoint.PauseGeneration + (request.Kind != DebugCommandKind.Pause &&
            instance.Status != WorkflowStatus.Paused && pendingPause ? 1 : 0);
        if (request.Kind is DebugCommandKind.Continue or DebugCommandKind.Step && instance.Status != WorkflowStatus.Paused &&
            !pendingPause && checkpoint.Stop != null) generation++;
        if (request.PauseGeneration != generation) return new(DebugCommandStatus.StaleGeneration);
        if (request.Kind != DebugCommandKind.SetBreakpoints && commands.Any(c => c.AcknowledgedRevision == null &&
            (c.Kind == request.Kind || request.Kind is DebugCommandKind.Continue or DebugCommandKind.Step &&
                c.Kind is DebugCommandKind.Continue or DebugCommandKind.Step)))
            return new(DebugCommandStatus.InvalidState);
        if (request.Kind is DebugCommandKind.Continue or DebugCommandKind.Step && instance.Status != WorkflowStatus.Paused && !pendingPause && checkpoint.Stop == null ||
            request.Kind == DebugCommandKind.Pause && instance.Status == WorkflowStatus.Paused)
            return new(DebugCommandStatus.InvalidState);
        return new(DebugCommandStatus.Accepted, new(request.Id,
            commands.Count == 0 ? 1 : checked(commands.Max(c => c.Order) + 1),
            request.Kind, request.PauseGeneration, request.Actor, Breakpoints: request.Breakpoints?.ToArray(),
            ContinuationId: request.ContinuationId, StepKind: request.StepKind));
    }

    public static IReadOnlyList<DebugCommand> Acknowledge(WorkflowInstance? previous, WorkflowInstance next,
        IReadOnlyList<DebugCommand> commands, long revision)
    {
        var before = previous == null ? new DebugCheckpoint() : Checkpoint(previous);
        var after = Checkpoint(next);
        var applied = commands.Where(c => c.Order > before.AppliedOrder && c.Order <= after.AppliedOrder).ToArray();
        bool AlreadyPaused(DebugCommand command) => previous?.Status == WorkflowStatus.Paused && before.Stop != null &&
            command.Kind == DebugCommandKind.Pause && command.PauseGeneration == before.PauseGeneration - 1;
        if (applied.Count(c => c.Kind != DebugCommandKind.SetBreakpoints && !AlreadyPaused(c)) > 1)
            throw new InvalidOperationException("A pause and its continue require separate lease assignments");
        if (applied.Any(c => c.Kind is DebugCommandKind.Continue or DebugCommandKind.Step) && previous?.Status != WorkflowStatus.Paused)
            throw new InvalidOperationException("Continue requires a committed pause");
        foreach (var resume in applied.Where(c => c.Kind is DebugCommandKind.Step or DebugCommandKind.Continue))
        {
            if (resume.Kind == DebugCommandKind.Step
                ? after.Stepping is not { Started: false } step || step.ContinuationId != resume.ContinuationId ||
                    step.Kind != resume.StepKind || step.CommandId != resume.Id || step.Actor != resume.Actor ||
                    !step.EligibleContinuations.SequenceEqual([resume.ContinuationId!])
                : after.Stepping != null)
                throw new InvalidOperationException("Resume must durably record its stepping state before dispatch");
        }
        var generation = before.PauseGeneration;
        foreach (var command in applied)
        {
            // An automatic hit may have committed the hold before the watch delivered a manual Pause.
            // Its acknowledgement consumes that redundant request without creating another generation.
            if (AlreadyPaused(command)) continue;
            // A configuration accepted before an automatic breakpoint pause remains ordered and applicable
            // after that pause. Pause/Continue still require their exact generation at application.
            if (command.Kind == DebugCommandKind.SetBreakpoints ? command.PauseGeneration > generation : command.PauseGeneration != generation)
                throw new InvalidOperationException("Stale debug command in commit");
            if (command.Kind == DebugCommandKind.Pause) generation++;
        }
        if (after.Stop != null && next.Status == WorkflowStatus.Paused && previous?.Status != WorkflowStatus.Paused &&
            !applied.Any(c => c.Kind == DebugCommandKind.Pause)) generation++;
        if (after.AppliedOrder < before.AppliedOrder || after.PauseGeneration != generation ||
            after.AppliedOrder != (applied.LastOrDefault()?.Order ?? before.AppliedOrder))
            throw new InvalidOperationException("Commit does not apply a valid ordered debug command prefix");
        if (applied.LastOrDefault(c => c.Kind != DebugCommandKind.SetBreakpoints) is { } last && !WorkflowStoreRules.IsTerminal(next.Status) &&
            (last.Kind == DebugCommandKind.Pause) != (next.Status == WorkflowStatus.Paused))
            throw new InvalidOperationException("Debug command acknowledgement does not match committed status");
        return commands.Select(c => c.Order <= after.AppliedOrder && c.AcknowledgedRevision == null
            ? c with { AcknowledgedRevision = revision } : c).ToArray();
    }
    public static bool NeedsWake(WorkflowStatus status, IReadOnlyList<DebugCommand> commands) =>
        !WorkflowStoreRules.IsTerminal(status) && commands.Any(c => c.AcknowledgedRevision == null);

    public static IReadOnlyList<Breakpoint> Breakpoints(WorkflowInstance instance, WorkflowDebugState state)
    {
        var checkpoint = Checkpoint(instance);
        var requested = state.Commands.LastOrDefault(c => c.Kind == DebugCommandKind.SetBreakpoints &&
            c.Order > checkpoint.AppliedOrder)?.Breakpoints;
        return requested?.Select(b => b with { HitCount = checkpoint.Breakpoints
            .FirstOrDefault(old => old.Id == b.Id && old.StepId == b.StepId)?.HitCount ?? 0 }).ToArray() ?? checkpoint.Breakpoints;
    }
}
