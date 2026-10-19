using System.Text.Json;
using System.Text.Json.Serialization;
namespace IxIFlow.Core;

public enum DebugCommandKind { Pause, Continue, SetBreakpoints, Step, EditData }
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
    IReadOnlyList<Breakpoint>? Breakpoints = null, string? ContinuationId = null, DebugStepKind? StepKind = null,
    DataEditRequest? Edit = null, DataEditResult? EditResult = null);
public sealed record DebugCommandRequest(string Id, DebugCommandKind Kind, long PauseGeneration, string Actor,
    IReadOnlyList<Breakpoint>? Breakpoints = null, string? ContinuationId = null, DebugStepKind? StepKind = null, DataEditRequest? Edit = null);
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
    /// <summary>Subscribes to local committed changes. Remote owners require bounded polling until coordinator notices are available.</summary>
    IDisposable WatchChanges(string instanceId, Action changed);
    Task<IReadOnlyList<StepTableEntry>> GetStepTableAsync(string instanceId, CancellationToken cancellationToken = default);
    /// <summary>Loads bounded, detached replay through the same privacy projection as inspection.</summary>
    Task<TraceReplay> OpenReplayAsync(string instanceId, int maxEntries = 10000, CancellationToken cancellationToken = default);
    /// <summary>Reads only a source named by the instance's published step table; null when unavailable locally.</summary>
    Task<string?> GetSourceAsync(string instanceId, string path, CancellationToken cancellationToken = default);
    /// <summary>Starts a registered definition with an optional hold saved before any workflow code runs.</summary>
    Task<string> LaunchAsync(string instanceId, string workflow, int version, JsonElement input, bool stopOnEntry,
        string actor, CancellationToken cancellationToken = default);
    /// <summary>Reads a projection of the saved checkpoint, never the checkpoint itself.</summary>
    Task<InstanceInspection?> InspectAsync(string instanceId, CancellationToken cancellationToken = default);
    /// <summary>Reads at most 1000 entries strictly after the given sequence. Sequence orders both clocks.</summary>
    Task<IReadOnlyList<StoredTraceEntry>> GetTraceAsync(string instanceId, long afterSequence = 0, int limit = 1000,
        CancellationToken cancellationToken = default);
    /// <summary>Requests a durable pause; running activities finish before it is committed.</summary>
    Task<WorkflowExecutionResult> PauseAsync(string instanceId, string actor, CancellationToken cancellationToken = default);
    /// <summary>Continues a pause under a fresh lease on this host.</summary>
    Task<WorkflowExecutionResult> ContinueAsync(string instanceId, string actor, CancellationToken cancellationToken = default);
    /// <summary>Accepts a durable continue and schedules it on the host, without waiting for workflow activities to finish.</summary>
    Task RequestContinueAsync(string instanceId, string actor, CancellationToken cancellationToken = default);
    /// <summary>Runs only the selected continuation and branches it creates, under a fresh lease.</summary>
    Task<WorkflowExecutionResult> StepAsync(string instanceId, string continuationId, DebugStepKind kind,
        string actor, CancellationToken cancellationToken = default);
    /// <summary>Edits a paused root or branch view with an immutable, caller-supplied edit ID.</summary>
    Task<DataEditResult> EditDataAsync(string instanceId, string editId, long expectedRevision,
        DataEditTarget target, IReadOnlyList<DataPatch> patches, string actor, CancellationToken cancellationToken = default);
    /// <summary>Replaces the instance's breakpoint set and returns the resolved addresses.</summary>
    Task<IReadOnlyList<Breakpoint>> SetBreakpointsAsync(string instanceId, IReadOnlyList<Breakpoint> breakpoints,
        string actor, CancellationToken cancellationToken = default);
    /// <summary>Reads the latest requested set with committed hit counts.</summary>
    Task<IReadOnlyList<Breakpoint>> GetBreakpointsAsync(string instanceId, CancellationToken cancellationToken = default);
}

/// <summary>Application access to source files in a published workflow bundle, when build paths are absent locally.</summary>
public interface IWorkflowDebugSourceProvider
{
    Task<string?> ReadAsync(string workflow, int version, string sourcePath, CancellationToken cancellationToken = default);
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
                JsonSerializer.Serialize(existing.Edit) != JsonSerializer.Serialize(request.Edit) ||
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
        if (request.Kind == DebugCommandKind.EditData)
        {
            ArgumentNullException.ThrowIfNull(request.Edit);
            ArgumentNullException.ThrowIfNull(request.Edit.Target);
            ArgumentNullException.ThrowIfNull(request.Edit.Patches);
            if (request.Edit.Patches.Count == 0) throw new ArgumentException("An edit requires at least one patch");
            if (request.Edit.Target.PauseGeneration != request.PauseGeneration ||
                request.Edit.ExpectedRevision != instance.Revision || instance.Status != WorkflowStatus.Paused ||
                commands.Any(c => c.AcknowledgedRevision == null))
                return new(DebugCommandStatus.InvalidState);
        }
        else if (request.Edit != null) throw new ArgumentException("Only EditData carries an edit");
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
            ContinuationId: request.ContinuationId, StepKind: request.StepKind,
            Edit: request.Edit == null ? null : JsonSerializer.Deserialize<DataEditRequest>(JsonSerializer.Serialize(request.Edit))));
    }

    public static IReadOnlyList<DebugCommand> Acknowledge(WorkflowInstance? previous, WorkflowInstance next,
        IReadOnlyList<DebugCommand> commands, long revision, IReadOnlyList<string> traceEntries)
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
        foreach (var edit in applied.Where(c => c.Kind == DebugCommandKind.EditData))
        {
            if (previous?.Status != WorkflowStatus.Paused || next.Status != WorkflowStatus.Paused ||
                edit.Edit?.ExpectedRevision != previous.Revision || after.PauseGeneration != before.PauseGeneration)
                throw new InvalidOperationException("Edit requires the expected paused revision and generation");
            var audits = traceEntries.Select(json => JsonSerializer.Deserialize<ExecutionTraceEntry>(json)!)
                .Where(e => e.EntryType == TraceEntryType.DataEdited).ToArray();
            if (audits.Length != 1 || audits[0].Payload is not DataEditedTrace audit || audit.EditId != edit.Id ||
                audit.Actor != edit.Actor || audit.Target != edit.Edit.Target || audit.Revision != revision ||
                audit.Changes.Count != edit.Edit.Patches.Count || audit.Changes.Where((c, i) =>
                    !c.Path.SequenceEqual(edit.Edit.Patches[i].Path) || c.BeforeDigest.Length != 64 || c.AfterDigest.Length != 64).Any())
                throw new InvalidOperationException("An edit requires its complete mandatory audit in the same commit");
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
            (last.Kind is DebugCommandKind.Pause or DebugCommandKind.EditData) != (next.Status == WorkflowStatus.Paused))
            throw new InvalidOperationException("Debug command acknowledgement does not match committed status");
        return commands.Select(c => c.Order <= after.AppliedOrder && c.AcknowledgedRevision == null
            ? c with { AcknowledgedRevision = revision, EditResult = c.Kind == DebugCommandKind.EditData
                ? new(next.InstanceId, c.Id, revision, c.Edit!.Target) : null } : c).ToArray();
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
