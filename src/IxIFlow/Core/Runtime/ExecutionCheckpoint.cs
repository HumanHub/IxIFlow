using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Core.Runtime;

internal sealed class ExecutionCheckpoint
{
    public string Runtime { get; set; } = "structured-v2";
    /// <summary>
    /// The form the checkpoint is saved in; 3 is the trunk and parts of A-08. A checkpoint in another form is refused
    /// as unsupported rather than read wrongly.
    /// </summary>
    public int SchemaVersion { get; set; } = 3;
    public string DefinitionFingerprint { get; set; } = "";
    public List<ContinuationState> Continuations { get; set; } = [];
    public List<ParallelJoinState> Joins { get; set; } = [];
    public List<WaitState> Waits { get; set; } = [];

    /// <summary>How many branches the instance has started; the next one's <see cref="ContinuationState.Sequence"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int BranchesStarted { get; set; }

    /// <summary>
    /// Set on the trunk of a saved checkpoint, which holds neither its branches nor its lists (A-08): only a read that
    /// assembles them can run it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool StoredTrunk { get; set; }

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
    /// such as its definition's fingerprint and its operator stop, but not to run it.
    /// </summary>
    public static ExecutionCheckpoint ReadTrunk(string json)
    {
        var checkpoint = JsonSerializer.Deserialize<ExecutionCheckpoint>(json);
        if (checkpoint?.Runtime != "structured-v2" || checkpoint.SchemaVersion != 3)
            throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                "Unsupported execution checkpoint version");
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
                Touch(branch);
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
        BoundReplies.RemoveAll(reply =>
        {
            if (live.Contains(reply.ScopeActivationId) || kept.Contains(reply.WaitId))
                return false;
            TouchOwner(reply.ContinuationId);
            return true;
        });
    }

    /// <summary>Removes the waits that match and records why each one closed, for the next commit (D-33).</summary>
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
        IsStructured(instance.ExecutionStateJson) ? ReadTrunk(instance.ExecutionStateJson).OperatorStop : null;

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
        OperatorStopKind.Verification =>
            $"The workflow instance is stopped because the engine could not verify a commit ({Reason}); its last " +
            "checkpoint is kept. Release it to run it again, or cancel it",
        _ => $"The workflow instance is stopped at a resource limit ({Reason}); " +
            "raise the limit and release it, or cancel it"
    };
}

internal enum OperatorStopKind
{
    ResourceLimit,
    Quarantine,

    /// <summary>A commit failed the checkpoint parts' verification (A-08), which points at an engine fault.</summary>
    Verification
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

    /// <summary>For a branch, its place in the order the instance's branches started; the root's is zero.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Sequence { get; set; }

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
/// One activity input as its attempt saves it (A-09). The activity runs on a copy read back from this, on its first
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
        try
        {
            if (value is Exception error)
            {
                var exception = SerializedException.From(error);
                var saved = new ActivityInputValue { Exception = exception };
                return (saved, saved.Read(input, services));
            }
            if (value == null)
                return (new ActivityInputValue(), null);
            var type = SavedType(input, value);
            var json = JsonSerializer.Serialize(value, type, OptionsFor(input));
            var copy = new ActivityInputValue { Value = new SerializedValue { Type = type.AssemblyQualifiedName!, Json = json } };
            return (copy, copy.Read(input, services));
        }
        catch (Exception problem) when (problem is not WorkflowEngineException)
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
        if (typeof(IAsyncActivity).IsAssignableFrom(type))
            return Value.Read(services);
        try
        {
            return JsonSerializer.Deserialize(Value.Json, type, OptionsFor(input));
        }
        catch (Exception problem) when (problem is not WorkflowEngineException)
        {
            throw Unreadable(input, problem);
        }
    }

    // A value is saved under its own type when that type can be read back by name, which keeps a derived value whole;
    // a compiler-made or non-public type, such as a LINQ iterator, is saved under the input's declared type.
    private static Type SavedType(PropertyInfo input, object value)
    {
        var runtime = value.GetType();
        return runtime.IsVisible && !runtime.IsAbstract || input.PropertyType == typeof(object) ? runtime : input.PropertyType;
    }

    // Every property is written, as for branch data (D-14), so a null or default inside an input reads back as it was;
    // a converter or number handling on the input property applies as it would to the property.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<PropertyInfo, JsonSerializerOptions> Options = new();

    private static JsonSerializerOptions OptionsFor(PropertyInfo input) => Options.GetOrAdd(input, property =>
    {
        var options = new JsonSerializerOptions
        {
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver
            {
                Modifiers = { info => { foreach (var each in info.Properties) each.ShouldSerialize = null; } }
            }
        };
        if (property.GetCustomAttribute<JsonConverterAttribute>() is { } attribute)
        {
            var converter = attribute.ConverterType is { } converterType
                ? Activator.CreateInstance(converterType) as JsonConverter
                : attribute.CreateConverter(property.PropertyType);
            if (converter != null)
                options.Converters.Add(converter);
        }
        if (property.GetCustomAttribute<JsonNumberHandlingAttribute>() is { } numbers)
            options.NumberHandling = numbers.Handling;
        return options;
    });

    private static WorkflowEngineException Unreadable(PropertyInfo input, Exception problem) => new(
        WorkflowErrorKind.InvalidDefinition,
        $"Activity input '{input.DeclaringType?.Name}.{input.Name}' cannot be saved and read back: " +
        problem.GetBaseException().Message, problem);
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

/// <summary>The branches a commit writes: those marked as changed, or all of them.</summary>
internal sealed record PartMarks(HashSet<string>? Ids)
{
    public static PartMarks All { get; } = new((HashSet<string>?)null);

    public bool Has(string continuationId) => Ids == null || Ids.Contains(continuationId);
}

/// <summary>
/// A <c>ForEach</c>'s list, saved once in item order as JSON of the item type, the next item to run, and,
/// when the step stores results, the results of the items that ran (D-34). The step's definition gives
/// the types.
/// </summary>
internal sealed class ForEachState
{
    /// <summary>This run of the step, which names its list and results where they are stored (A-08).</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public List<string> Items { get; set; } = [];
    public int Next { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string?>? Results { get; set; }

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

    /// <summary>
    /// The join's branches, in the order they started. Not saved: each branch names its join, and reading a
    /// checkpoint lists them again (A-08).
    /// </summary>
    [JsonIgnore]
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

    /// <summary>The continuation whose request it answers, which keeps it (A-08).</summary>
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
