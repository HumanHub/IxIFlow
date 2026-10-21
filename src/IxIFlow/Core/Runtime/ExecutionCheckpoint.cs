using JsonSerializer = IxIFlow.Core.Runtime.WorkflowJson;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Core.Runtime;

internal sealed class ExecutionCheckpoint
{
    public List<WorkflowDemand> Demands { get; set; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TimeSpan? HostPinLifetime { get; set; }

    [JsonIgnore]
    public TraceBuffer Trace { get; set; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long TraceEntriesRecorded { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool TraceBudgetExhausted { get; set; }

    /// <summary>Whether this instance's replay origin has been recorded, including for child starts.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool TraceReplayInitialized { get; set; }

    [JsonRequired]
    public string Runtime { get; set; } = "structured-v2";
    /// <summary>
    /// The form the checkpoint is saved in; 3 stores the trunk and separate state parts. A checkpoint in another form is refused
    /// as unsupported rather than read wrongly.
    /// </summary>
    [JsonRequired]
    public int SchemaVersion { get; set; } = WorkflowDefinitionValidator.CheckpointContractVersion;
    [JsonRequired]
    public string StructureHash { get; set; } = "";
    public List<ContinuationState> Continuations { get; set; } = [];
    public List<ParallelJoinState> Joins { get; set; } = [];
    public List<WaitState> Waits { get; set; } = [];

    /// <summary>How many branches the instance has started; the next one's <see cref="ContinuationState.Sequence"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int BranchesStarted { get; set; }

    /// <summary>
    /// Set on the trunk of a saved checkpoint, which holds neither its branches nor its lists: only a read that
    /// assembles them can run it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool StoredTrunk { get; set; }
    /// <summary>Store-owned retry metadata for branches whose parts are payloads.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PayloadRetry>? PayloadRetries { get; set; }

    /// <summary>
    /// Answers to requests that arrived before their <c>WaitFor</c> was reached. Each is taken
    /// by the <c>WaitFor</c> of its request's scope occurrence, or dropped when that scope ends.
    /// </summary>
    public List<BoundReplyState> BoundReplies { get; set; } = [];

    /// <summary>
    /// Children this run starts, carried by its next commit with the waits it opened for them.
    /// Never saved: a run lost before that commit started neither.
    /// </summary>
    [JsonIgnore]
    public List<ChildStart> ChildStarts { get; } = [];

    /// <summary>Cancellation requests for children, carried by the run's next commit.</summary>
    [JsonIgnore]
    public List<ChildCancellation> ChildCancellations { get; } = [];

    /// <summary>
    /// How long the instance may run in all, or null. The store stamps the deadline when it records the
    /// execution deadline's row, <see cref="ExecutionTimerId"/>, in the waits table.
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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<InspectionError>? Diagnostics { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? OperatorDiagnosticIds { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InspectionError? ResultDiagnostic { get; set; }

    /// <summary>New raw records travel once with the commit, never in checkpoint JSON.</summary>
    [JsonIgnore]
    public List<WorkflowDiagnostic> PendingDiagnostics { get; } = [];

    public InspectionError RecordDiagnostic(Exception error)
    {
        var safe = InspectionRedactionPolicy.Default.Error(error);
        Diagnostics ??= [];
        if (Diagnostics.All(item => item.DiagnosticId != safe.DiagnosticId))
            PendingDiagnostics.Add(new(safe.DiagnosticId, safe.ErrorType, error.Message, error.StackTrace));
        Diagnostics.RemoveAll(item => item.DiagnosticId == safe.DiagnosticId);
        Diagnostics.Add(safe);
        return safe;
    }

    public InspectionError RecordDiagnostic(string code, string message)
    {
        var safe = new InspectionError(code, Guid.NewGuid().ToString("N"));
        (Diagnostics ??= []).Add(safe);
        PendingDiagnostics.Add(new(safe.DiagnosticId, safe.ErrorType, message, null));
        return safe;
    }

    /// <summary>Operator-authored records are bounded by operator actions, independently of failures.</summary>
    public InspectionError RecordOperatorDiagnostic(string code, string message)
    {
        var diagnostic = RecordDiagnostic(code, message);
        (OperatorDiagnosticIds ??= []).Add(diagnostic.DiagnosticId);
        return diagnostic;
    }

    /// <summary>Keep operator records, the latest failure, and all pending activity/stop identities.</summary>
    public void RetainDiagnostics()
    {
        var operatorIds = (OperatorDiagnosticIds ?? []).ToHashSet(StringComparer.Ordinal);
        var latest = Diagnostics?.LastOrDefault(item => !operatorIds.Contains(item.DiagnosticId));
        ResultDiagnostic = OperatorStop?.Diagnostic ?? Continuations
            .FirstOrDefault(item => item.Status == ContinuationStatus.WaitingResolution)
            ?.PendingActivity?.ResolutionDiagnostic ?? latest;
        if (Diagnostics is not { Count: > 0 })
            return;
        var retained = Continuations.Where(item => item.Status is not (ContinuationStatus.Completed or ContinuationStatus.Cancelled))
            .Select(item => item.PendingActivity?.ResolutionDiagnostic?.DiagnosticId)
            .Append(OperatorStop?.Diagnostic?.DiagnosticId).Append(latest?.DiagnosticId)
            .Where(id => id != null).ToHashSet(StringComparer.Ordinal);
        retained.UnionWith(operatorIds);
        Diagnostics.RemoveAll(item => !retained.Contains(item.DiagnosticId));
        PendingDiagnostics.RemoveAll(item => !retained.Contains(item.DiagnosticId));
    }
    public bool CancellationRequested { get; set; }

    /// <summary>
    /// Set while the instance is stopped at NeedsResolution for an operator: after a commit exceeded a
    /// resource limit or after too many crashes. Only a release or a cancellation moves it.
    /// </summary>
    public OperatorStopState? OperatorStop { get; set; }

    public DebugCheckpoint Debug { get; set; } = new();

    [JsonIgnore]
    public Exception? RuntimeUnhandledError { get; set; }

    /// <summary>
    /// What the run has done with the inbox and the waits table since its last commit. It is not
    /// saved: the store holds what was committed, and a run that loses it re-reads the inbox.
    /// </summary>
    [JsonIgnore]
    public InboxProgress Inbox { get; private set; } = new();

    /// <summary>The store's clock as this run last read it. It is not saved.</summary>
    [JsonIgnore]
    public StoreClock Clock { get; } = new();

    /// <summary>The delivered signals for this run, never checkpointed.</summary>
    [JsonIgnore]
    public ExecutionSignals? Signals { get; set; }

    /// <summary>
    /// True while the execution deadline can still end the run: it is set, and the run has neither
    /// reached it nor been cancelled, and its root has not finished.
    /// </summary>
    [JsonIgnore]
    public bool ExecutionTimerOpen => ExecutionTimerId != null && !CancellationRequested && !TimedOut &&
        Continuations.Count > 0 && !ContinuationTransitions.RootFinished(this);

    public static ExecutionCheckpoint Read(string json)
    {
        var checkpoint = ReadTrunk(json);
        if (checkpoint.StoredTrunk)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                "The execution state was read without its branches and lists");
        // Each branch names its join; a join lists them in the order they started, which is the saved order.
        var joins = checkpoint.Joins.ToDictionary(join => join.Id, StringComparer.Ordinal);
        foreach (var branch in checkpoint.Continuations)
            if (branch.ParentJoinId != null && joins.TryGetValue(branch.ParentJoinId, out var join))
                join.ChildContinuationIds.Add(branch.Id);
        // Every wait in a saved checkpoint was committed with it.
        checkpoint.Inbox = InboxProgress.Committed(checkpoint);
        return checkpoint;
    }

    /// <summary>
    /// Reads a checkpoint whole, or only the trunk a summary read returns: enough for what belongs to the instance,
    /// such as its definition's structure hash and its operator stop, but not to run it.
    /// </summary>
    public static ExecutionCheckpoint ReadTrunk(string json)
    {
        // Check the envelope before interpreting state that belongs to another schema.
        using var document = JsonDocument.Parse(json);
        ValidateEnvelope(document.RootElement);
        return JsonSerializer.Deserialize<ExecutionCheckpoint>(json)!;
    }

    internal static void ValidateEnvelope(JsonElement root)
    {
        var version = root.ValueKind == JsonValueKind.Object && root.TryGetProperty(nameof(SchemaVersion), out var savedVersion)
            ? savedVersion.ToString() : "missing";
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(nameof(Runtime), out var runtime) || runtime.GetString() != "structured-v2" ||
            !root.TryGetProperty(nameof(SchemaVersion), out savedVersion) ||
            savedVersion.ValueKind != JsonValueKind.Number || !savedVersion.TryGetInt32(out var number) || number != 4)
            throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                $"Unsupported execution checkpoint version '{version}'; expected 4. " +
                "Saved instances from earlier builds cannot be read; no migration is available");
    }

    /// <summary>
    /// Takes the deadlines the store stamped from a page it read. A wait the store has no deadline
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
        Waits.Where(wait => SteppingPolicy.Allows(this, wait.ContinuationId)).Select(wait => wait.DeadlineUtc is { } deadline ? Clock.ToLocal(deadline) : (DateTime?)null)
            .Append(Debug.Stepping == null && ExecutionTimerOpen && ExecutionDeadlineUtc is { } execution ? Clock.ToLocal(execution) : null)
            .Min();

    private object? _rootData;
    [JsonIgnore]
    public bool RootDataChanged { get; set; } = true;
    [JsonIgnore]
    public Func<object, bool>? RootDataUnchanged { get; set; }

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

    // The branches whose stored parts may have changed since a commit last took the marks, or null for all of them:
    // a run starts with every branch marked, so whatever a host changed before it is written (A-08).
    private HashSet<string>? _marked;

    /// <summary>The branches' IDs while a split writes the trunk, so it finds them once.</summary>
    [JsonIgnore]
    internal HashSet<string>? SplitBranches { get; set; }

    /// <summary>Marks the stored part of <paramref name="continuation"/>, and of what it owns, as changed.</summary>
    public void Touch(ContinuationState continuation) => _marked?.Add(continuation.Id);

    /// <summary>Marks the stored part of the continuation with <paramref name="continuationId"/> as changed.</summary>
    public void TouchOwner(string continuationId) => _marked?.Add(continuationId);

    /// <summary>Marks every branch as changed.</summary>
    public void TouchAll() => _marked = null;

    /// <summary>The marks a commit's split writes by; the checkpoint starts marking again from none.</summary>
    public PartMarks TakeMarks()
    {
        var taken = new PartMarks(_marked);
        _marked = new HashSet<string>(StringComparer.Ordinal);
        return taken;
    }

    /// <summary>Gives back marks a commit took but did not save, so the next commit writes those parts.</summary>
    public void RestoreMarks(PartMarks marks)
    {
        if (marks.Ids == null)
            _marked = null;
        else
            _marked?.UnionWith(marks.Ids);
    }

    /// <summary>A join's branches, in the order they started, found by ID rather than by scanning for each.</summary>
    public List<ContinuationState> BranchesOf(ParallelJoinState join) =>
        join.ChildContinuationIds.Select(ContinuationById).OfType<ContinuationState>().ToList();

    // Continuations by ID, built when a lookup misses and dropped when continuations are removed.
    private Dictionary<string, ContinuationState>? _byId;
    private List<ContinuationState>? _indexed;

    /// <summary>The continuation with <paramref name="id"/>, or null when there is none.</summary>
    public ContinuationState? ContinuationById(string id)
    {
        if (_byId != null && ReferenceEquals(_indexed, Continuations) && _byId.TryGetValue(id, out var found))
            return found;
        _indexed = Continuations;
        _byId = Continuations.ToDictionary(item => item.Id, StringComparer.Ordinal);
        return _byId.GetValueOrDefault(id);
    }

    /// <summary>Removes the continuations that match; the index forgets them.</summary>
    public void RemoveContinuations(Predicate<ContinuationState> match)
    {
        Continuations.RemoveAll(match);
        _byId = null;
    }

    /// <summary>The continuation a branch was forked from.</summary>
    public ContinuationState ForkParent(ContinuationState branch)
    {
        var join = Joins.Single(item => item.Id == branch.ParentJoinId);
        return Continuations.Single(item => item.Id == join.ParentContinuationId);
    }

    /// <summary>
    /// Records, for every branch whose copy may have changed, its changes against the data it was forked
    /// from, so a recovery rebuilds the same copy. A branch is never recorded while its own
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
                Touch(branch);
            }
        }
    }

    /// <summary>
    /// Closes the reply waits, and drops the answers kept, of scope occurrences that have ended: their
    /// iteration or branch finished, failed or was cancelled before the request's <c>WaitFor</c> took the
    /// answer.
    /// </summary>
    /// A request a saga retry keeps stays open while the saga compensates, until the retry restores it.
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
        BoundReplies.RemoveAll(reply =>
        {
            if (live.Contains(reply.ScopeActivationId) || kept.Contains(reply.WaitId))
                return false;
            TouchOwner(reply.ContinuationId);
            return true;
        });
    }

    /// <summary>Removes the waits that match and records why each one closed, for the next commit.</summary>
    public void RemoveWaits(Func<WaitState, bool> match, WaitCloseReason reason)
    {
        foreach (var wait in Waits.Where(match).ToArray())
        {
            Waits.Remove(wait);
            TouchOwner(wait.ContinuationId);
            Inbox.Closed(wait.Id, reason);
        }
    }

    /// <summary>Adds a wait, which belongs to its continuation's stored part.</summary>
    public void AddWait(WaitState wait)
    {
        Waits.Add(wait);
        TouchOwner(wait.ContinuationId);
    }

    /// <summary>The operator stop of a saved instance, read without its workflow data.</summary>
    public static OperatorStopState? StopOf(WorkflowInstance instance) =>
        ReadTrunk(instance.ExecutionStateJson).OperatorStop;

    public static bool IsStructured(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return false;
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(nameof(Runtime), out var runtime) &&
            runtime.GetString() == "structured-v2";
    }
}

internal sealed class PayloadRetry
{
    public string Id { get; set; } = "";
    public DateTime? RetryAfterUtc { get; set; }
}

internal sealed class OperatorStopState
{
    public InspectionError? Diagnostic { get; set; }
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
        OperatorStopKind.Verification =>
            $"The workflow instance is stopped because the engine could not verify a commit ({Reason}); its last " +
            "checkpoint is kept. Release it to run it again, or cancel it",
        OperatorStopKind.PayloadUnavailable =>
            $"The workflow instance is stopped because a payload is unavailable ({Reason}); its checkpoint is kept. Restore the payload and release it",
        _ => $"The workflow instance is stopped at a resource limit ({Reason}); " +
            "raise the limit and release it, or cancel it"
    };
}

internal enum OperatorStopKind
{
    ResourceLimit,
    Quarantine,

    /// <summary>A commit failed the checkpoint parts' verification, which points at an engine fault.</summary>
    Verification,
    PayloadUnavailable
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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ItemEndCause? ItemUnwindCause { get; set; }

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? ParentJoinId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ForkActivationId { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ScopePosition? PendingJoinFrame { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SerializedException? DeferredFailure { get; set; }

    /// <summary>For a branch, its place in the order the instance's branches started; the root's is zero.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Sequence { get; set; }

    public ContinuationStatus Status { get; set; } = ContinuationStatus.Active;
    public bool CancellationUnwind { get; set; }
    public List<ScopePosition> Stack { get; set; } = [];
    public SerializedValue? Previous { get; set; }
    public ActivityInvocationState? PendingActivity { get; set; }

    /// <summary>
    /// The inputs a failed attempt saved, kept while its step waits to be retried: a step retry is another attempt of
    /// the same invocation and starts from them.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public KeptActivityInputs? RetryInputs { get; set; }

    public AcceptedWaitState? AcceptedWait { get; set; }

    /// <summary>For a branch, the <c>ForEach</c> items in force where it was forked.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<InheritedItem>? InheritedItems { get; set; }

    /// <summary>When the continuation's retry is due, on the store's clock as the run last read it.</summary>
    public DateTime? RetryAfterUtc { get; set; }

    [JsonIgnore]
    public object? RuntimePrevious { get; set; }

    /// <summary>
    /// For a branch, its own copy of the workflow data; null until it is needed, when it is
    /// rebuilt from the data it was forked from and <see cref="DataChanges"/>. The root reads and writes
    /// the instance's data, <see cref="ExecutionCheckpoint.RootData"/>.
    /// </summary>
    [JsonIgnore]
    public object? Data { get; set; }

    /// <summary>For a branch, its changes to the data it was forked from, as of the last commit.</summary>
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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChildDataType { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ArtifactDigest? ChildBundleDigest { get; set; }
    public List<ActivityAttemptState> Attempts { get; set; } = [];
    public string? ResolutionReason { get; set; }
    public InspectionError? ResolutionDiagnostic { get; set; }
    public ActivityResolutionState? Resolution { get; set; }
}

/// <summary>The inputs an invocation's failed attempt saved, for the step retry that continues the invocation.</summary>
internal sealed class KeptActivityInputs
{
    public string InvocationId { get; set; } = "";
    public ActivityInputSnapshot Inputs { get; set; } = new();

    /// <summary>When the invocation began, and its attempts so far, which the retry's record carries on.</summary>
    public DateTime StartedAtUtc { get; set; }
    public List<ActivityAttemptState> Attempts { get; set; } = [];
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

    /// <summary>The host the attempt ran on, as it names itself.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Host { get; set; }

    /// <summary>The exception a thrown attempt ended with, for the attempts after it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorType { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorMessage { get; set; }

    /// <summary>The note a failed attempt left for the next one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SerializedValue? Note { get; set; }

    /// <summary>The attempt as a later attempt of the same invocation sees it.</summary>
    public PreviousActivityAttempt Seen(IServiceProvider services) =>
        new(Id, Host, StartedAtUtc, EndedAtUtc, ErrorType, ErrorMessage, ReadNote(services));

    // A note this host cannot read is an artifact problem, like a missing type: the run stops with its checkpoint
    // kept, and a host that can read it, or one running fixed code, continues.
    private object? ReadNote(IServiceProvider services)
    {
        try
        {
            return Note?.Read(services);
        }
        catch (Exception problem) when (problem is not WorkflowEngineException)
        {
            throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                $"The note attempt '{Id}' left cannot be read: {problem.Message}", problem);
        }
    }
}

internal sealed class ActivityInputSnapshot
{
    public Dictionary<string, ActivityInputValue> Properties { get; set; } = [];

    public IReadOnlyCollection<string> CodeInputs => Properties
        .Where(item => item.Value.RebindFromDefinition)
        .Select(item => item.Key).ToArray();

    /// <summary>
    /// Sets the saved inputs on a recovered attempt's activity, as copies read back from what was saved, so it runs on
    /// exactly what its first attempt ran on.
    /// </summary>
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
            property.SetValue(activity, saved.Read(property, services));
        }
    }
}

/// <summary>
/// One activity input as its attempt saves it. The activity runs on a copy read back from this, on its first
/// attempt as after a recovery, so a change it makes to an input reaches nothing outside it: the workflow data changes
/// only through output mappings, whether or not the host crashed. A delegate input is bound again from the definition.
/// </summary>
internal sealed class ActivityInputValue
{
    public SerializedValue? Value { get; set; }
    public SerializedException? Exception { get; set; }
    public bool RebindFromDefinition { get; set; }

    /// <summary>
    /// Saves the value mapped to <paramref name="input"/> and returns it with the copy the activity is given. A value
    /// that cannot be saved and read back is an invalid definition, refused before the activity runs.
    /// </summary>
    public static (ActivityInputValue Saved, object? Copy) Of(PropertyInfo input, object? value, IServiceProvider services)
    {
        if (value is Delegate)
            return (new ActivityInputValue { RebindFromDefinition = true }, value);
        // The value and the definition are all a first attempt reads, so any failure here is the definition's.
        try
        {
            if (value is Exception error && !HasConverter(input))
            {
                var exception = SerializedException.From(error);
                return (new ActivityInputValue { Exception = exception }, exception.Restore());
            }
            if (value == null && !HasConverter(input))
                return (new ActivityInputValue(), null);
            var type = SavedType(input, value);
            var json = Serialize(input, value);
            var saved = new ActivityInputValue { Value = new SerializedValue { Type = WorkflowTypeIdentity.SavedName(type), Json = StoredJson.Inline(json) } };
            return (saved, saved.ReadAs(type, input, services));
        }
        catch (Exception problem)
        {
            throw Unreadable(input, problem);
        }
    }

    /// <summary>The copy of the saved value the activity is given.</summary>
    public object? Read(PropertyInfo input, IServiceProvider services)
    {
        if (Exception != null)
            return Exception.Restore();
        if (Value == null)
            return null;
        var type = WorkflowTypeIdentity.Resolve(Value.Type)
            ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                $"Saved value type '{Value.Type}' is unavailable");
        try
        {
            return ReadAs(type, input, services);
        }
        catch (Exception problem) when (problem is not WorkflowEngineException)
        {
            throw Unreadable(input, problem);
        }
    }

    private object? ReadAs(Type type, PropertyInfo input, IServiceProvider services) =>
        typeof(IAsyncActivity).IsAssignableFrom(type) && !HasConverter(input)
            ? SerializedValue.ReadActivity(type, Value!.Json.InlineText, services)
            : JsonSerializer.Deserialize(Value!.Json.InlineText, type, OptionsFor(input));

    // A value is saved under its own type, read back by name, which keeps a derived value whole. A non-public
    // collection, such as a LINQ iterator, is saved under the input's declared type: what it holds is its elements, and
    // its own type cannot be made from them. An input property with a converter is saved under its declared type,
    // which the converter is written for, and read back through it, whatever the value: an object, an exception or an
    // activity. Number handling applies to the numbers inside whatever type is saved.
    private static Type SavedType(PropertyInfo input, object? value)
    {
        if (value == null || HasConverter(input))
            return input.PropertyType;
        var runtime = value.GetType();
        return input.PropertyType == typeof(object) || runtime.IsVisible || value is not System.Collections.IEnumerable
            ? runtime
            : input.PropertyType;
    }

    /// <summary>Serializes a value with the input's saved type and property-level JSON settings.</summary>
    internal static string Serialize(PropertyInfo input, object? value) =>
        JsonSerializer.Serialize(value, SavedType(input, value), OptionsFor(input));

    private static bool HasConverter(PropertyInfo input) => input.IsDefined(typeof(JsonConverterAttribute));

    private static bool HasJsonAttributes(PropertyInfo input) =>
        input.IsDefined(typeof(JsonConverterAttribute)) || input.IsDefined(typeof(JsonNumberHandlingAttribute));

    // Every property is written, as for branch data (D-14), so a null or default inside an input reads back as it was;
    // a converter or number handling on the input property applies as it would to the property. A converter for the
    // declared type also applies to a value of that type nested inside the input.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PropertyInfo, JsonSerializerOptions> Options = new();

    private static readonly JsonSerializerOptions EveryProperty = EveryPropertyOptions();

    private static JsonSerializerOptions EveryPropertyOptions() => new()
    {
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver
        {
            Modifiers = { info => { foreach (var each in info.Properties) each.ShouldSerialize = null; } }
        }
    };

    private static JsonSerializerOptions OptionsFor(PropertyInfo input) => !HasJsonAttributes(input)
        ? EveryProperty
        : Options.GetValue(input, property =>
        {
            var options = EveryPropertyOptions();
            if (PropertyConverter(property) is { } converter)
                options.Converters.Add(converter);
            if (property.GetCustomAttribute<JsonNumberHandlingAttribute>() is { } numbers)
                options.NumberHandling = numbers.Handling;
            return options;
        });

    /// <summary>The converter a property's <see cref="JsonConverterAttribute"/> names, if it has one.</summary>
    public static JsonConverter? PropertyConverter(PropertyInfo property) =>
        property.GetCustomAttribute<JsonConverterAttribute>() is { } attribute
            ? attribute.ConverterType is { } converterType
                ? Activator.CreateInstance(converterType) as JsonConverter
                : attribute.CreateConverter(property.PropertyType)
            : null;

    private static WorkflowEngineException Unreadable(PropertyInfo input, Exception problem) => new(
        WorkflowErrorKind.InvalidDefinition,
        $"Activity input '{input.DeclaringType?.Name}.{input.Name}' cannot be saved and read back: " +
        problem.GetBaseException().Message, problem);
}

internal sealed class ScopePosition
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool CompensationFailed { get; set; }

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

    /// <summary>While the step at <see cref="NextStepIndex"/> is a <c>ForEach</c>: its saved list, next item and results.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ForEachState? ForEach { get; set; }

    /// <summary>For an item's frame of a <c>ForEach</c> body, the item's position in the list.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ForEachIndex { get; set; }

    /// <summary>For an item's frame, the value its result step gave, as JSON of the result type.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StoredJson? ForEachResult { get; set; }

    [JsonIgnore]
    public object? RuntimeEntryPrevious { get; set; }
}

/// <summary>The branches a commit writes: those marked as changed, or all of them.</summary>
internal sealed record PartMarks(HashSet<string>? Ids)
{
    public static PartMarks All { get; } = new((HashSet<string>?)null);

    public bool Has(string continuationId) => Ids == null || Ids.Contains(continuationId);
}

/// <summary>
/// A <c>ForEach</c>'s list, saved once in item order as JSON of the item type, the next item to run, and,
/// when the step stores results, the results of the items that ran. The step's definition gives
/// the types.
/// </summary>
internal sealed class ForEachState
{
    /// <summary>This run of the step, which names its list and results where they are stored.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public List<StoredJson> Items { get; set; } = [];
    public int Next { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<StoredJson?>? Results { get; set; }

    /// <summary>In parallel, the items whose branches completed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ItemSet? Finished { get; set; }

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

    /// <summary>The saga's frames, each recorded once by activation, so a retry can restore a restart point.</summary>
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

    /// <summary>The frame the step ran in and its index there: the step's restart point.</summary>
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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TimedOutWaitId { get; set; }
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

    /// <summary>
    /// The join's branches, in the order they started. Not saved: each branch names its join, and reading a
    /// checkpoint lists them again.
    /// </summary>
    [JsonIgnore]
    public List<string> ChildContinuationIds { get; set; } = [];
    public ParallelJoinMode Mode { get; set; }
    public bool IsCompleting { get; set; }
    public bool ResumeParentWithoutAdvance { get; set; }

    /// <summary>Set when a provisional merge found branches in conflict; the join fails with it.</summary>
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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StoredJson? DefaultPayloadJson { get; set; }
    public DateTime RegisteredAtUtc { get; set; }

    /// <summary>
    /// For a wait the store times out, its deadline as the store stamped it, or the run's estimate until
    /// the run reads it.
    /// </summary>
    public DateTime? DeadlineUtc { get; set; }

    /// <summary>
    /// The wait's timeout or delay, from the commit that records it, or for a reply wait from the commit
    /// that reaches its <c>WaitFor</c>. The store adds it to its own time.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TimeSpan? Timeout { get; set; }
    /// <summary>For a child's wait, the child: only its completion, addressed to this wait, answers it.</summary>
    public string? ChildWorkflowId { get; set; }

    /// <summary>
    /// For a child's wait, set when the invocation says <c>CancelWithParent(false)</c>: cancelling the branch, or
    /// failing the parent, closes the wait and leaves the child running.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Detached { get; set; }

    /// <summary>
    /// True for the answer to a request: opened when the request's activity starts, answered only by
    /// its token, and taken by the request's <c>WaitFor</c>.
    /// </summary>
    public bool IsReply { get; set; }

    /// <summary>For a reply wait, the request's activity invocation; a retry of the step keeps the wait.</summary>
    public string? InvocationId { get; set; }

    /// <summary>For a reply wait, the scope occurrence of the request and its <c>WaitFor</c>.</summary>
    public string? ScopeActivationId { get; set; }

    /// <summary>For a reply wait, the digest of what the request is about; an answer must carry it.</summary>
    public string? Subject { get; set; }

    /// <summary>How the waits table knows this wait.</summary>
    [JsonIgnore]
    public WaitKind TableKind => string.IsNullOrEmpty(EventType) ? WaitKind.Delay
        : IsReply ? WaitKind.Reply
        : ChildWorkflowId != null ? WaitKind.Child
        : WaitKind.Event;

    /// <summary>True for a wait an event can answer.</summary>
    [JsonIgnore]
    public bool Answerable => TableKind is WaitKind.Event or WaitKind.Reply or WaitKind.Child;
}

/// <summary>An answer to a request kept until its <c>WaitFor</c> takes it.</summary>
internal sealed class BoundReplyState
{
    public string WaitId { get; set; } = "";
    public string EventId { get; set; } = "";

    /// <summary>The continuation whose request it answers, which keeps it.</summary>
    public string ContinuationId { get; set; } = "";

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
    public StoredJson Json { get; set; } = StoredJson.Inline("");

    public static SerializedValue? From(object? value) => value == null
        ? null
        : new SerializedValue
        {
            Type = WorkflowTypeIdentity.SavedName(value.GetType()),
            Json = StoredJson.Inline(JsonSerializer.Serialize(value, value.GetType()))
        };

    public object? Read(IServiceProvider services)
    {
        var type = WorkflowTypeIdentity.Resolve(Type)
            ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                $"Saved value type '{Type}' is unavailable");
        return typeof(IAsyncActivity).IsAssignableFrom(type)
            ? ReadActivity(type, Json.InlineText, services)
            : JsonSerializer.Deserialize(Json.InlineText, type);
    }

    /// <summary>An activity created through the container, with each saved property set as its own attributes read it.</summary>
    public static object ReadActivity(Type type, string json, IServiceProvider services)
    {
        var activity = ActivityActivation.Create(type, services, services.GetRequiredService<IActivityRegistry>());
        var properties = ActivityProperties.GetValue(type, SettableProperties);
        using var document = JsonDocument.Parse(json);
        foreach (var value in document.RootElement.EnumerateObject())
        {
            if (!properties.TryGetValue(value.Name, out var saved) || saved.Property.GetSetMethod(nonPublic: true) == null)
                throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                    $"Saved activity output '{type.Name}.{value.Name}' cannot be restored");
            var options = saved.Options.Value
                ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                    $"Converter for saved activity output '{type.Name}.{saved.Property.Name}' is unavailable");
            saved.Property.SetValue(activity, JsonSerializer.Deserialize(value.Value.GetRawText(), saved.Property.PropertyType, options));
        }
        return activity;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Type,
        IReadOnlyDictionary<string, (PropertyInfo Property, Lazy<JsonSerializerOptions?> Options)>> ActivityProperties =
            new();

    // An activity's public properties by their JSON names, the first of a name winning, each with the options its
    // converter and number handling need, made when a saved value first needs them; no options when its converter
    // cannot be created. A property the saved form leaves out, such as an ignored one, never makes its converter.
    private static IReadOnlyDictionary<string, (PropertyInfo Property, Lazy<JsonSerializerOptions?> Options)>
        SettableProperties(Type type) =>
        type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .GroupBy(property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name)
            .ToDictionary(group => group.Key, group =>
            {
                var property = group.First();
                return (property, new Lazy<JsonSerializerOptions?>(() =>
                {
                    var options = new JsonSerializerOptions();
                    if (property.IsDefined(typeof(JsonConverterAttribute)))
                    {
                        if (ActivityInputValue.PropertyConverter(property) is not { } converter)
                            return null;
                        options.Converters.Add(converter);
                    }
                    if (property.GetCustomAttribute<JsonNumberHandlingAttribute>() is { } numbers)
                        options.NumberHandling = numbers.Handling;
                    return options;
                }));
            });
}
