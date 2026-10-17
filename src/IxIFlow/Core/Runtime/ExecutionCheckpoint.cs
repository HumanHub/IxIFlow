using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Core.Runtime;

internal sealed class ExecutionCheckpoint
{
    public string Runtime { get; set; } = "structured-v2";
    public int SchemaVersion { get; set; } = 2;
    public string DefinitionFingerprint { get; set; } = "";
    public List<ContinuationState> Continuations { get; set; } = [];
    public List<ParallelJoinState> Joins { get; set; } = [];
    public List<WaitState> Waits { get; set; } = [];

    /// <summary>
    /// Answers to requests that arrived before their <c>WaitFor</c> was reached (D-33). Each is taken
    /// by the <c>WaitFor</c> of its request's scope occurrence, or dropped when that scope ends.
    /// </summary>
    public List<BoundReplyState> BoundReplies { get; set; } = [];

    /// <summary>
    /// Children this run starts, carried by its next commit with the waits it opened for them (D-17).
    /// Never saved: a run lost before that commit started neither.
    /// </summary>
    [JsonIgnore]
    public List<ChildStart> ChildStarts { get; } = [];

    /// <summary>Cancellation requests for children, carried by the run's next commit.</summary>
    [JsonIgnore]
    public List<ChildCancellation> ChildCancellations { get; } = [];

    /// <summary>
    /// How long the instance may run in all, or null. The store stamps the deadline when it records the
    /// execution deadline's row, <see cref="ExecutionTimerId"/>, in the waits table (D-35).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TimeSpan? ExecutionTimeout { get; set; }

    /// <summary>The waits-table row of the execution deadline, when there is one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExecutionTimerId { get; set; }

    /// <summary>The execution deadline as the store stamped it, or the run's estimate until it reads it.</summary>
    public DateTime? ExecutionDeadlineUtc { get; set; }
    public bool TimedOut { get; set; }
    public SerializedException? UnhandledError { get; set; }
    public bool CancellationRequested { get; set; }

    /// <summary>
    /// Set while the instance is stopped at NeedsResolution for an operator: after a commit exceeded a
    /// resource limit (D-28) or after too many crashes (B-08). Only a release or a cancellation moves it.
    /// </summary>
    public OperatorStopState? OperatorStop { get; set; }

    [JsonIgnore]
    public Exception? RuntimeUnhandledError { get; set; }

    /// <summary>
    /// What the run has done with the inbox and the waits table since its last commit. It is not
    /// saved: the store holds what was committed, and a run that loses it re-reads the inbox.
    /// </summary>
    [JsonIgnore]
    public InboxProgress Inbox { get; private set; } = new();

    /// <summary>The store's clock as this run last read it (D-35). It is not saved.</summary>
    [JsonIgnore]
    public StoreClock Clock { get; } = new();

    /// <summary>
    /// When the host should next look at the instance after its last commit, in the host's clock, or
    /// null when nothing but an event or an operator moves it. It is not saved.
    /// </summary>
    [JsonIgnore]
    public DateTime? LocalWakeUtc { get; set; }

    /// <summary>
    /// True while the execution deadline can still end the run: it is set, and the run has neither
    /// reached it nor been cancelled, and its root has not finished.
    /// </summary>
    [JsonIgnore]
    public bool ExecutionTimerOpen => ExecutionTimerId != null && !CancellationRequested && !TimedOut &&
        Continuations.Count > 0 && !ContinuationTransitions.RootFinished(this);

    public static ExecutionCheckpoint Read(string json)
    {
        var checkpoint = JsonSerializer.Deserialize<ExecutionCheckpoint>(json);
        if (checkpoint?.Runtime != "structured-v2" || checkpoint.SchemaVersion != 2)
            throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                "Unsupported execution checkpoint version");
        // Every wait in a saved checkpoint was committed with it.
        checkpoint.Inbox = InboxProgress.Committed(checkpoint);
        return checkpoint;
    }

    /// <summary>
    /// Takes the deadlines the store stamped from a page it read (D-35). A wait the store has no deadline
    /// for has none yet: it is not committed, or not armed. A page without deadlines reports none.
    /// </summary>
    public void ReadDeadlines(InboxPage page)
    {
        var deadlines = page.Deadlines ?? new Dictionary<string, DateTime>();
        foreach (var wait in Waits.Where(wait => wait.TableKind is not WaitKind.Child))
            wait.DeadlineUtc = deadlines.TryGetValue(wait.Id, out var deadline) ? deadline : null;
        if (ExecutionTimerId != null)
            ExecutionDeadlineUtc = deadlines.TryGetValue(ExecutionTimerId, out var execution) ? execution : null;
    }

    /// <summary>The earliest time, in the host's clock, at which a wait or the execution deadline may be due.</summary>
    public DateTime? NextDeadlineLocal() =>
        Waits.Select(wait => wait.DeadlineUtc is { } deadline ? Clock.ToLocal(deadline) : (DateTime?)null)
            .Append(ExecutionTimerOpen && ExecutionDeadlineUtc is { } execution ? Clock.ToLocal(execution) : null)
            .Min();

    private object? _rootData;

    /// <summary>The instance's workflow data, which the root continuation reads and writes.</summary>
    [JsonIgnore]
    public object RootData
    {
        get => _rootData ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
            "The run has no workflow data");
        set => _rootData = value;
    }

    /// <summary>The workflow data <paramref name="continuation"/> reads and writes.</summary>
    public object DataOf(ContinuationState continuation)
    {
        if (continuation.ParentJoinId == null)
            return RootData;
        return continuation.Data ??= BranchData.Rebuild(DataOf(ForkParent(continuation)),
            continuation.DataChanges ?? []);
    }

    /// <summary>The continuation a branch was forked from.</summary>
    public ContinuationState ForkParent(ContinuationState branch)
    {
        var join = Joins.Single(item => item.Id == branch.ParentJoinId);
        return Continuations.Single(item => item.Id == join.ParentContinuationId);
    }

    /// <summary>
    /// Records, for every branch whose copy may have changed, its changes against the data it was forked
    /// from, so a recovery rebuilds the same copy (spec E-01). A branch is never recorded while its own
    /// activity runs: it is marked only when it settles or advances.
    /// </summary>
    public void RecordBranchChanges()
    {
        // Branches of one fork share their baseline, which is serialized once.
        foreach (var fork in Continuations.Where(item => item.ParentJoinId != null && item.DataChanged &&
                                                         item.Data != null).GroupBy(ForkParent))
        {
            var baseline = DataOf(fork.Key);
            var baselineNode = BranchData.Node(baseline);
            foreach (var branch in fork)
            {
                var changes = BranchData.Changes(baselineNode, branch.Data!, baseline.GetType());
                branch.DataChanges = changes.Count == 0 ? null : changes;
                branch.DataChanged = false;
            }
        }
    }

    /// <summary>
    /// Closes the reply waits, and drops the answers kept, of scope occurrences that have ended: their
    /// iteration or branch finished, failed or was cancelled before the request's <c>WaitFor</c> took the
    /// answer (D-33).
    /// </summary>
    /// A request a saga retry keeps stays open while the saga compensates, until the retry restores it (D-36).
    public void CloseEndedRequests()
    {
        var running = Continuations
            .Where(item => item.Status is not (ContinuationStatus.Completed or ContinuationStatus.Cancelled))
            .ToArray();
        var live = running.SelectMany(item => item.Stack.Select(position => position.ActivationId))
            .ToHashSet(StringComparer.Ordinal);
        var kept = running.SelectMany(item => item.Stack)
            .SelectMany(position => position.SagaState?.KeptWaitIds ?? [])
            .ToHashSet(StringComparer.Ordinal);
        RemoveWaits(wait => wait.IsReply && !live.Contains(wait.ScopeActivationId ?? "") && !kept.Contains(wait.Id),
            WaitCloseReason.Cancelled);
        BoundReplies.RemoveAll(reply => !live.Contains(reply.ScopeActivationId) && !kept.Contains(reply.WaitId));
    }

    /// <summary>Removes the waits that match and records why each one closed, for the next commit (D-33).</summary>
    public void RemoveWaits(Func<WaitState, bool> match, WaitCloseReason reason)
    {
        foreach (var wait in Waits.Where(match).ToArray())
        {
            Waits.Remove(wait);
            Inbox.Closed(wait.Id, reason);
        }
    }

    /// <summary>The operator stop of a saved instance, read without its workflow data.</summary>
    public static OperatorStopState? StopOf(WorkflowInstance instance) =>
        IsStructured(instance.ExecutionStateJson) ? Read(instance.ExecutionStateJson).OperatorStop : null;

    public static bool IsStructured(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return false;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(nameof(Runtime), out var runtime) &&
            runtime.GetString() == "structured-v2";
    }
}

internal sealed class OperatorStopState
{
    public OperatorStopKind Kind { get; set; }
    public string Reason { get; set; } = "";
    public DateTime StoppedAtUtc { get; set; }

    /// <summary>For a quarantine, how many owners in a row lost the instance.</summary>
    public int CrashCount { get; set; }

    /// <summary>What the operator sees, with the actions that move the instance.</summary>
    public string Describe() => Kind switch
    {
        OperatorStopKind.Quarantine =>
            $"The workflow instance is quarantined: {Reason}. Release it to run it again, or cancel it",
        _ => $"The workflow instance is stopped at a resource limit ({Reason}); " +
            "raise the limit and release it, or cancel it"
    };
}

internal enum OperatorStopKind
{
    ResourceLimit,
    Quarantine
}

internal enum ContinuationStatus
{
    Active,
    Waiting,
    Joining,
    Cancelling,
    Completed,
    Cancelled,
    WaitingResolution
}

internal sealed class ContinuationState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? ParentJoinId { get; set; }
    public ContinuationStatus Status { get; set; } = ContinuationStatus.Active;
    public bool CancellationUnwind { get; set; }
    public List<ScopePosition> Stack { get; set; } = [];
    public SerializedValue? Previous { get; set; }
    public ActivityInvocationState? PendingActivity { get; set; }
    public AcceptedWaitState? AcceptedWait { get; set; }

    /// <summary>For a branch, the <c>ForEach</c> items in force where it was forked (D-34).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<InheritedItem>? InheritedItems { get; set; }

    /// <summary>When the continuation's retry is due, on the store's clock as the run last read it (D-35).</summary>
    public DateTime? RetryAfterUtc { get; set; }

    [JsonIgnore]
    public object? RuntimePrevious { get; set; }

    /// <summary>
    /// For a branch, its own copy of the workflow data (D-14); null until it is needed, when it is
    /// rebuilt from the data it was forked from and <see cref="DataChanges"/>. The root reads and writes
    /// the instance's data, <see cref="ExecutionCheckpoint.RootData"/>.
    /// </summary>
    [JsonIgnore]
    public object? Data { get; set; }

    /// <summary>For a branch, its changes to the data it was forked from, as of the last commit (spec E-01).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DataChange>? DataChanges { get; set; }

    /// <summary>Set when the branch may have changed its copy since its changes were last recorded.</summary>
    [JsonIgnore]
    public bool DataChanged { get; set; }

    public object? GetPrevious(IServiceProvider services) =>
        RuntimePrevious ??= Previous?.Read(services);

    public void SetPrevious(object? value)
    {
        RuntimePrevious = value;
        Previous = null;
    }
}

/// <summary>
/// A committed Start for one logical activity invocation. The continuation cannot advance
/// until this invocation has a committed End or an explicit recovery decision.
/// </summary>
internal sealed class ActivityInvocationState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string StepId { get; set; } = "";
    public DateTime StartedAtUtc { get; set; }
    public ActivityInputSnapshot Inputs { get; set; } = new();
    public SerializedValue? RecoveryState { get; set; }
    public string? ChildWorkflowId { get; set; }
    public List<ActivityAttemptState> Attempts { get; set; } = [];
    public string? ResolutionReason { get; set; }
    public ActivityResolutionState? Resolution { get; set; }
}

internal sealed class ActivityResolutionState
{
    public ActivityResolutionKind Kind { get; set; }
    public string Note { get; set; } = "";
    public string? DecidedBy { get; set; }
    public Dictionary<string, SerializedValue?> OutputProperties { get; set; } = [];
    public SerializedValue? InvocationResult { get; set; }
    public SerializedException? Failure { get; set; }
}

internal sealed class ActivityAttemptState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime StartedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public string Kind { get; set; } = "Execute";
    public string? Observation { get; set; }
}

internal sealed class ActivityInputSnapshot
{
    public Dictionary<string, ActivityInputValue> Properties { get; set; } = [];

    public IReadOnlyCollection<string> CodeInputs => Properties
        .Where(item => item.Value.RebindFromDefinition)
        .Select(item => item.Key).ToArray();

    public static ActivityInputSnapshot Capture(IAsyncActivity activity, WorkflowStep step)
    {
        var snapshot = new ActivityInputSnapshot();
        foreach (var mapping in step.InputMappings.Where(item =>
                     item.Direction == PropertyMappingDirection.Input))
        {
            var property = activity.GetType().GetProperty(mapping.TargetProperty)
                ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                    $"Mapped activity input '{mapping.TargetProperty}' was not found");
            var value = property.GetValue(activity);
            snapshot.Properties[property.Name] = value switch
            {
                Delegate => new ActivityInputValue { RebindFromDefinition = true },
                Exception error => new ActivityInputValue
                {
                    Exception = SerializedException.From(error)
                },
                _ => new ActivityInputValue { Value = SerializedValue.From(value) }
            };
        }
        return snapshot;
    }

    public void Restore(IAsyncActivity activity, IServiceProvider services)
    {
        foreach (var (name, saved) in Properties)
        {
            if (saved.RebindFromDefinition)
                continue;
            var property = activity.GetType().GetProperty(name)
                ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                    $"Saved activity input '{activity.GetType().Name}.{name}' is unavailable");
            if (property.GetSetMethod(nonPublic: true) == null)
                throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                    $"Saved activity input '{activity.GetType().Name}.{name}' cannot be restored");
            property.SetValue(activity, saved.Exception?.Restore() ?? saved.Value?.Read(services));
        }
    }
}

internal sealed class ActivityInputValue
{
    public SerializedValue? Value { get; set; }
    public SerializedException? Exception { get; set; }
    public bool RebindFromDefinition { get; set; }
}

internal sealed class ScopePosition
{
    public string ScopeId { get; set; } = "";
    public string ActivationId { get; set; } = Guid.NewGuid().ToString("N");
    public int NextStepIndex { get; set; }
    public int LoopIterationCount { get; set; }
    public Dictionary<int, int> StepRetryCounts { get; set; } = [];
    public TryScopeState? TryState { get; set; }
    public SagaScopeState? SagaState { get; set; }
    public SerializedValue? EntryPrevious { get; set; }
    public bool RestorePreviousOnExit { get; set; }
    public bool ExitParentScopeOnExit { get; set; }

    /// <summary>While the step at <see cref="NextStepIndex"/> is a <c>ForEach</c>: its saved list, next item and results (D-34).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ForEachState? ForEach { get; set; }

    /// <summary>For an item's frame of a <c>ForEach</c> body, the item's position in the list.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ForEachIndex { get; set; }

    /// <summary>For an item's frame, the value its result step gave, as JSON of the result type.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ForEachResult { get; set; }

    [JsonIgnore]
    public object? RuntimeEntryPrevious { get; set; }
}

/// <summary>
/// A <c>ForEach</c>'s list, saved once in item order as JSON of the item type, the next item to run, and,
/// when the step stores results, the results of the items that ran (D-34). The step's definition gives
/// the types.
/// </summary>
internal sealed class ForEachState
{
    public List<string> Items { get; set; } = [];
    public int Next { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string?>? Results { get; set; }

    /// <summary>In parallel, the items whose branches completed, in completion order.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<int>? Finished { get; set; }

    /// <summary>In parallel, set when the join is done and merged; the step then stores its results.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Joined { get; set; }
}

internal enum TryPhase
{
    Try,
    Catch,
    Finally
}

internal sealed class TryScopeState
{
    public TryPhase Phase { get; set; } = TryPhase.Try;
    public int CatchIndex { get; set; } = -1;
    public bool IsCancellationCleanup { get; set; }
    public SerializedValue? EntryPrevious { get; set; }
    public SerializedException? Error { get; set; }

    [JsonIgnore]
    public object? RuntimeEntryPrevious { get; set; }

    [JsonIgnore]
    public Exception? RuntimeError { get; set; }

    public Exception? GetError() => RuntimeError ??= Error?.ForPropagation();

    public void SetError(Exception error)
    {
        RuntimeError = error;
        Error = SerializedException.From(error);
    }
}

internal enum SagaPhase
{
    Forward,
    Compensating
}

internal sealed class SagaScopeState
{
    public SagaPhase Phase { get; set; }
    public List<SagaCompletedStep> CompletedSteps { get; set; } = [];
    public int CompensationCursor { get; set; } = -1;
    public int CompensationFloor { get; set; }
    public int ErrorHandlerIndex { get; set; } = -1;
    public bool ErrorHandlerStarted { get; set; }
    public SagaContinuationAction? ErrorAction { get; set; }
    public bool IgnoreCompensationErrors { get; set; }
    public int RetryCount { get; set; }
    public int MaximumRetries { get; set; }
    public List<SagaAcceptedWait> AcceptedWaits { get; set; } = [];
    public int AcceptedWaitCursor { get; set; }
    public SerializedException? Error { get; set; }
    public List<string> CompensationErrors { get; set; } = [];
    public bool IsCancellationCleanup { get; set; }
    public SerializedValue? PreviousCompensation { get; set; }

    /// <summary>The saga's frames, each recorded once by activation, so a retry can restore a restart point (D-36).</summary>
    public Dictionary<string, SagaFrameRecord> Frames { get; set; } = [];

    /// <summary>Where a retry starts again, with the <c>Previous</c> that step saw; none starts at the saga's first step.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SagaRestartPoint? RestartAt { get; set; }

    /// <summary>The reply waits and kept answers of requests a retry keeps, live until the retry restores them.</summary>
    public List<string> KeptWaitIds { get; set; } = [];

    /// <summary>The failure of a retry handler's own steps, which ends the saga once the kept steps are undone.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SerializedException? HandlerError { get; set; }

    [JsonIgnore]
    public Exception? RuntimeError { get; set; }

    public void SetError(Exception error)
    {
        RuntimeError = error;
        Error = SerializedException.From(error);
    }

    public Exception? GetError() => RuntimeError ??= Error?.ForPropagation();

    public Exception? GetTerminalError() => ErrorHandlerIndex >= 0 && ErrorAction != null && Error != null
        ? new SagaFailureException(Error.Type, Error.Message)
        : GetError();
}

internal sealed class SagaAcceptedWait
{
    public string StepId { get; set; } = "";
    public SerializedValue Event { get; set; } = null!;

    /// <summary>How many saga steps had completed when the wait accepted its event.</summary>
    public int Completed { get; set; }
}

internal sealed class SagaCompletedStep
{
    public string StepId { get; set; } = "";
    public SerializedValue? Output { get; set; }
    public SerializedValue? Previous { get; set; }

    /// <summary>The frame the step ran in and its index there: the step's restart point (D-36).</summary>
    public string FrameId { get; set; } = "";
    public int StepIndex { get; set; }

    /// <summary>For a request, its invocation, whose reply wait and answer carry it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InvocationId { get; set; }

    /// <summary>For a request, the answer it received, taken by its <c>WaitFor</c> or kept for it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SagaReply? Reply { get; set; }

    /// <summary>For a request, whether its answer's wait timed out.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ReplyTimedOut { get; set; }
}

/// <summary>A request's answer, as its compensation and a retried <c>WaitFor</c> read it.</summary>
internal sealed class SagaReply
{
    public SerializedValue Event { get; set; } = new();
    public string EventId { get; set; } = "";
    public string WaitId { get; set; } = "";
}

/// <summary>A saga frame as it was entered: enough to push it again at a restart point.</summary>
internal sealed class SagaFrameRecord
{
    public string ScopeId { get; set; } = "";

    /// <summary>The frame it was pushed from, and that frame's step index then; none for the saga body.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentFrameId { get; set; }
    public int IndexInParent { get; set; }

    public SerializedValue? EntryPrevious { get; set; }
    public bool RestorePreviousOnExit { get; set; }
    public bool ExitParentScopeOnExit { get; set; }
}

/// <summary>A step position in a saga's frames, and the <c>Previous</c> the step saw.</summary>
internal sealed class SagaRestartPoint
{
    public string FrameId { get; set; } = "";
    public int StepIndex { get; set; }
    public SerializedValue? Previous { get; set; }

    /// <summary>For the step that failed, its ID: a wait it accepted before its outputs failed is replayed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StepId { get; set; }
}

internal sealed class ParallelJoinState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string StepId { get; set; } = "";
    public string ParentContinuationId { get; set; } = "";
    public List<string> ChildContinuationIds { get; set; } = [];
    public ParallelJoinMode Mode { get; set; }
    public bool IsCompleting { get; set; }
    public bool ResumeParentWithoutAdvance { get; set; }

    /// <summary>Set when a provisional merge found branches in conflict; the join fails with it (spec E-09).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConflictPath { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Conflict { get; set; }
}

internal sealed class WaitState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ContinuationId { get; set; } = "";
    public string StepId { get; set; } = "";
    public string Key { get; set; } = "";
    public string EventType { get; set; } = "";
    public DateTime RegisteredAtUtc { get; set; }

    /// <summary>
    /// For a wait the store times out, its deadline as the store stamped it, or the run's estimate until
    /// the run reads it (D-35).
    /// </summary>
    public DateTime? DeadlineUtc { get; set; }

    /// <summary>
    /// The wait's timeout or delay, from the commit that records it, or for a reply wait from the commit
    /// that reaches its <c>WaitFor</c>. The store adds it to its own time (D-35).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TimeSpan? Timeout { get; set; }
    /// <summary>For a child's wait, the child: only its completion, addressed to this wait, answers it (D-17).</summary>
    public string? ChildWorkflowId { get; set; }

    /// <summary>
    /// For a child's wait, set when the invocation says <c>CancelWithParent(false)</c>: cancelling the branch, or
    /// failing the parent, closes the wait and leaves the child running (D-09).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Detached { get; set; }

    /// <summary>
    /// True for the answer to a request: opened when the request's activity starts, answered only by
    /// its token, and taken by the request's <c>WaitFor</c> (D-33).
    /// </summary>
    public bool IsReply { get; set; }

    /// <summary>For a reply wait, the request's activity invocation; a retry of the step keeps the wait.</summary>
    public string? InvocationId { get; set; }

    /// <summary>For a reply wait, the scope occurrence of the request and its <c>WaitFor</c>.</summary>
    public string? ScopeActivationId { get; set; }

    /// <summary>For a reply wait, the digest of what the request is about; an answer must carry it.</summary>
    public string? Subject { get; set; }

    /// <summary>How the waits table knows this wait (D-33, D-35).</summary>
    [JsonIgnore]
    public WaitKind TableKind => string.IsNullOrEmpty(EventType) ? WaitKind.Delay
        : IsReply ? WaitKind.Reply
        : ChildWorkflowId != null ? WaitKind.Child
        : WaitKind.Event;

    /// <summary>True for a wait an event can answer.</summary>
    [JsonIgnore]
    public bool Answerable => TableKind is WaitKind.Event or WaitKind.Reply or WaitKind.Child;
}

/// <summary>An answer to a request kept until its <c>WaitFor</c> takes it (D-33).</summary>
internal sealed class BoundReplyState
{
    public string WaitId { get; set; } = "";
    public string EventId { get; set; } = "";

    /// <summary>The <c>WaitFor</c> step that takes it.</summary>
    public string StepId { get; set; } = "";
    public string ScopeActivationId { get; set; } = "";
    public string? InvocationId { get; set; }
    public string? Subject { get; set; }
    public SerializedValue Event { get; set; } = new();
}

internal sealed class AcceptedWaitState
{
    public string StepId { get; set; } = "";
    public SerializedValue Event { get; set; } = new();

    /// <summary>The inbox event this is.</summary>
    public string EventId { get; set; } = "";

    /// <summary>The wait that took it.</summary>
    public string WaitId { get; set; } = "";
}

internal sealed class SerializedValue
{
    public string Type { get; set; } = "";
    public string Json { get; set; } = "";

    public static SerializedValue? From(object? value) => value == null
        ? null
        : new SerializedValue
        {
            Type = value.GetType().AssemblyQualifiedName!,
            Json = JsonSerializer.Serialize(value, value.GetType())
        };

    public object? Read(IServiceProvider services)
    {
        var type = WorkflowTypeIdentity.Resolve(Type)
            ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                $"Saved value type '{Type}' is unavailable");
        if (typeof(IAsyncActivity).IsAssignableFrom(type))
        {
            var activity = ActivatorUtilities.CreateInstance(services, type);
            using var document = JsonDocument.Parse(Json);
            foreach (var value in document.RootElement.EnumerateObject())
            {
                var property = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(candidate =>
                        (candidate.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? candidate.Name) == value.Name);
                if (property?.GetSetMethod(nonPublic: true) == null)
                    throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                        $"Saved activity output '{type.Name}.{value.Name}' cannot be restored");
                var options = new JsonSerializerOptions();
                var converterAttribute = property.GetCustomAttribute<JsonConverterAttribute>();
                if (converterAttribute != null)
                {
                    var converter = converterAttribute.ConverterType is { } converterType
                        ? Activator.CreateInstance(converterType) as JsonConverter
                        : converterAttribute.CreateConverter(property.PropertyType);
                    if (converter == null)
                        throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                            $"Converter for saved activity output '{type.Name}.{property.Name}' is unavailable");
                    options.Converters.Add(converter);
                }
                var numberHandling = property.GetCustomAttribute<JsonNumberHandlingAttribute>();
                if (numberHandling != null)
                    options.NumberHandling = numberHandling.Handling;
                property.SetValue(activity, value.Value.Deserialize(property.PropertyType, options));
            }
            return activity;
        }
        return JsonSerializer.Deserialize(Json, type);
    }
}
