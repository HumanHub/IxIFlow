using IxIFlow.Builders;

namespace IxIFlow.Core;

/// <summary>
///     Represents the current status of a workflow execution
/// </summary>
public enum WorkflowStatus
{
    /// <summary>
    ///     Workflow is ready to be started
    /// </summary>
    Ready,

    /// <summary>
    ///     Workflow is currently running
    /// </summary>
    Running,

    /// <summary>
    ///     Workflow is suspended and waiting for an event
    /// </summary>
    Suspended,

    /// <summary>
    ///     Workflow completed successfully
    /// </summary>
    Completed,

    /// <summary>
    ///     Workflow failed with an error
    /// </summary>
    Failed,

    /// <summary>
    ///     Workflow was cancelled
    /// </summary>
    Cancelled,

    /// <summary>
    ///     Workflow is in compensation mode (saga pattern)
    /// </summary>
    Compensating,

    /// <summary>
    ///     Workflow is terminated
    /// </summary>
    Terminated,
    /// <summary>
    /// At least one activity has a committed Start without a committed End and needs resolution.
    /// </summary>
    NeedsResolution,
    /// <summary>The whole-instance execution deadline expired.</summary>
    TimedOut
}

/// <summary>
///     Reason for workflow cancellation
/// </summary>
public class CancellationReason
{
    /// <summary>The durable command mode; termination supersedes cancellation without unwinding.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public WorkflowControlMode Mode { get; set; }

    /// <summary>The version assigned by the store and acknowledged by the terminal commit.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public long CommandVersion { get; set; }

    /// <summary>
    ///     Reason code for cancellation
    /// </summary>
    public string ReasonCode { get; set; } = "";

    /// <summary>
    ///     Human-readable description
    /// </summary>
    public string Description { get; set; } = "";

    /// <summary>
    ///     When the cancellation was requested
    /// </summary>
    public DateTime RequestedAt { get; set; }

    /// <summary>
    ///     Who requested the cancellation: a plain string the engine records and never checks. Empty takes
    ///     the client's default actor, <c>IxIFlowOptions.Actor</c>.
    /// </summary>
    public string RequestedBy { get; set; } = "";

    /// <summary>
    ///     Additional metadata about the cancellation
    /// </summary>
    public Dictionary<string, System.Text.Json.JsonElement> Metadata { get; set; } = new();
}

/// <summary>
///     Workflow options for execution control
/// </summary>
public class WorkflowOptions
{
    /// <summary>Additional capability constraints, resolved once at creation.</summary>
    public List<WorkflowDemand> Demands { get; set; } = [];
    /// <summary>A temporary pin, stamped with an expiry by the creating store.</summary>
    public Guid? RequiredHostId { get; set; }
    /// <summary>Wait by default, or refuse when no live host matches.</summary>
    public WhenUnschedulable WhenUnschedulable { get; set; } = WhenUnschedulable.Wait;

    /// <summary>Stable instance ID for an idempotent start command.</summary>
    public string? InstanceId { get; set; }

    /// <summary>
    ///     Maximum execution time for the workflow
    /// </summary>
    public TimeSpan? ExecutionTimeout { get; set; }

    /// <summary>
    ///     Whether to enable detailed tracing
    /// </summary>
    public bool EnableTracing { get; set; } = true;

    /// <summary>
    ///     Custom correlation ID
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    ///     The parent instance that invoked this workflow as a child
    /// </summary>
    internal string? ParentInstanceId { get; set; }

    /// <summary>
    ///     Custom properties for the workflow execution
    /// </summary>
    public Dictionary<string, object> Properties { get; set; } = new();
}

/// <summary>
///     Represents a runtime workflow instance
/// </summary>
public class WorkflowInstance
{
    /// <summary>The host that most recently claimed or committed this instance.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Guid? LastHostId { get; internal set; }

    /// <summary>The identity of this creation of the instance, independent of its reusable ID.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Guid Incarnation { get; internal set; }

    /// <summary>The revision assigned by an atomic repository commit.</summary>
    public long Revision { get; set; }

    /// <summary>
    ///     Unique identifier for this workflow instance
    /// </summary>
    public string InstanceId { get; set; } = "";

    /// <summary>
    ///     Workflow definition name
    /// </summary>
    public string WorkflowName { get; set; } = "";

    /// <summary>
    ///     Workflow version
    /// </summary>
    public int WorkflowVersion { get; set; }

    /// <summary>
    ///     Current execution status
    /// </summary>
    public WorkflowStatus Status { get; set; }

    /// <summary>
    ///     Correlation ID for tracking
    /// </summary>
    public string CorrelationId { get; set; } = "";

    /// <summary>
    ///     The parent instance that invoked this workflow as a child, if any
    /// </summary>
    public string? ParentInstanceId { get; set; }

    /// <summary>
    /// The parent's wait for this child, which the child's terminal commit answers with its completion.
    /// </summary>
    public string? ParentWaitId { get; set; }

    /// <summary>
    ///     When the workflow was created
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    ///     When the workflow was started
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    ///     When the workflow completed (successfully or with error)
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// The earliest time a host should claim and run the instance: the commit time while it
    /// runs, its earliest deadline or check while it waits, now when a cancellation request or a
    /// finished child needs it. Null when only an outside event or an operator can move it.
    /// </summary>
    public DateTime? RunnableAtUtc { get; set; }

    /// <summary>
    ///     Serialized workflow data
    /// </summary>
    public string WorkflowDataJson { get; set; } = "";

    /// <summary>
    ///     Workflow data type for deserialization
    /// </summary>
    public string WorkflowDataType { get; set; } = "";

    /// <summary>
    ///     Serialized execution state at suspension point (for resume operations)
    /// </summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(RawJsonText))]
    public string ExecutionStateJson { get; set; } = "";

    /// <summary>
    ///     Last error that occurred (if any)
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    ///     Stack trace of the last error
    /// </summary>
    public string? LastErrorStackTrace { get; set; }

    /// <summary>
    ///     Cancellation reason (if cancelled)
    /// </summary>
    public CancellationReason? CancellationReason { get; set; }

    /// <summary>
    ///     Custom properties for this instance
    /// </summary>
    public Dictionary<string, System.Text.Json.JsonElement> Properties { get; set; } = new();

    /// <summary>Whether trace entries are recorded.</summary>
    public bool TracingEnabled { get; set; } = true;

    /// <summary>
    /// The parts the instance's state is stored in as of its last commit or read, by key, or null when that is not
    /// known; a commit writes only the parts that differ, and deletes those that are gone.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    internal IReadOnlyDictionary<string, string>? SavedParts { get; private set; }

    // The instance and the revision SavedParts are stored at.
    private string? _savedPartsInstanceId;
    private long _savedPartsRevision;

    /// <summary>Records the parts the instance is stored in at <paramref name="revision"/>.</summary>
    internal void Saved(IReadOnlyDictionary<string, string> parts, long revision)
    {
        SavedParts = parts;
        _savedPartsInstanceId = InstanceId;
        _savedPartsRevision = revision;
    }

    /// <summary>
    /// The parts the instance is stored in at <paramref name="revision"/>, or null when they are not known for it: the
    /// object changed revision, or names another instance, since it was read or last committed through the engine.
    /// </summary>
    internal IReadOnlyDictionary<string, string>? PartsAt(long revision) =>
        SavedParts != null && _savedPartsRevision == revision && _savedPartsInstanceId == InstanceId
            ? SavedParts : null;

    /// <summary>The stored form the last preparation made of the instance's state and data.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    internal Runtime.StoredCheckpoint? Stored { get; set; }

    /// <summary>The instance's fields with <paramref name="trunk"/> as its state and no data, to store as a trunk.</summary>
    internal WorkflowInstance AsTrunk(string trunk)
    {
        var copy = (WorkflowInstance)MemberwiseClone();
        copy.ExecutionStateJson = trunk;
        copy.WorkflowDataJson = "";
        return copy;
    }

    /// <summary>
}

/// <summary>
/// Where an instance's branches are and which scopes they are in, computed from its saved checkpoint when
/// asked through <see cref="IWorkflowClient.GetSnapshotAsync"/>; it is not stored.
/// </summary>
public class WorkflowExecutionSnapshot
{
    /// <summary>True when only the instance's removal tombstone remains; all collections are empty.</summary>
    public bool IsRemoved { get; set; }

    /// <summary>
    ///     Workflow instance identifier that owns this snapshot
    /// </summary>
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>
    ///     Workflow definition name
    /// </summary>
    public string WorkflowName { get; set; } = string.Empty;

    /// <summary>
    ///     Workflow definition version
    /// </summary>
    public int WorkflowVersion { get; set; }

    /// <summary>
    ///     Workflow status at snapshot time
    /// </summary>
    public WorkflowStatus Status { get; set; }

    /// <summary>
    ///     The current active execution pointers
    /// </summary>
    public List<ExecutionPointer> Pointers { get; set; } = new();

    /// <summary>
    ///     Execution frames that describe nested control flow state
    /// </summary>
    public List<ExecutionFrame> Frames { get; set; } = new();

    /// <summary>Workflow data projected for inspection, with classified members redacted.</summary>
    public System.Text.Json.JsonElement? Data { get; set; }

    /// <summary>Safe diagnostic identities; raw messages and stacks require the privileged diagnostics read.</summary>
    public IReadOnlyList<InspectionError> Errors { get; set; } = [];

    /// <summary>When the snapshot was computed from the saved checkpoint.</summary>
    public DateTime CapturedAtUtc { get; set; }
}

/// <summary>
///     Execution pointer describing the next step location for a branch of execution
/// </summary>
public class ExecutionPointer
{
    /// <summary>
    ///     Pointer identity
    /// </summary>
    public string PointerId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    ///     The step this pointer currently targets
    /// </summary>
    public string StepId { get; set; } = string.Empty;

    /// <summary>
    ///     Zero-based step index at the current scope
    /// </summary>
    public int StepIndex { get; set; }

    /// <summary>
    ///     Pointer status, such as Active, Waiting, or Completed
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    ///     Associated frame when the pointer belongs to a nested control-flow scope
    /// </summary>
    public string? FrameId { get; set; }
}

/// <summary>
///     Execution frame representing nested control-flow context
/// </summary>
public class ExecutionFrame
{
    /// <summary>
    ///     Frame identity
    /// </summary>
    public string FrameId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    ///     Frame kind such as Try, Catch, Loop, Sequence, or Conditional
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    ///     Owning step identifier
    /// </summary>
    public string StepId { get; set; } = string.Empty;

    /// <summary>
    ///     Parent frame for nested flow scopes
    /// </summary>
    public string? ParentFrameId { get; set; }

    /// <summary>
    ///     Structured frame state captured as strings for persistence safety
    /// </summary>
    public Dictionary<string, string> State { get; set; } = new();

    /// <summary>
    /// Values needed only while a scope is active. They are encoded into State at suspension.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<string, object?> RuntimeValues { get; set; } = new();
}

/// <summary>
///     Execution trace entry for comprehensive workflow debugging
/// </summary>
public class ExecutionTraceEntry
{
    /// <summary>
    ///     Unique identifier for this trace entry
    /// </summary>
    public string TraceId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    ///     When this trace entry was created
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>Sequence orders the trace; host and store clocks can differ.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public TraceClock Clock { get; set; }

    /// <summary>
    ///     Step number in the workflow
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int StepNumber { get; set; }

    /// <summary>
    ///     Name of the activity being executed
    /// </summary>
    public string ActivityName { get; set; } = "";

    /// <summary>
    ///     Type of trace entry
    /// </summary>
    public TraceEntryType EntryType { get; set; }

    /// <summary>
    ///     Execution duration (for completion entries)
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public TimeSpan? Duration { get; set; }

    /// <summary>
    ///     Error information (if applicable)
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorMessage { get; set; }

    /// <summary>The safe type/code and diagnostic ID of this failure.</summary>
    public InspectionError? Error { get; set; }

    /// <summary>
    ///     Stack trace (if error occurred)
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? StackTrace { get; set; }

    /// <summary>
    ///     Activity attempt facts; other kinds carry only their typed payload.
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();

    /// <summary>The typed facts for this entry kind.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public TracePayload? Payload { get; set; }
}

/// <summary>
///     Type of execution trace entry
/// </summary>
public enum TraceEntryType
{
    /// <summary>
    ///     Activity execution started
    /// </summary>
    ActivityStarted,

    /// <summary>
    ///     Activity execution completed successfully
    /// </summary>
    ActivityCompleted,

    /// <summary>
    ///     Activity execution failed
    /// </summary>
    ActivityFailed,

    /// <summary>
    ///     Workflow execution started
    /// </summary>
    WorkflowStarted,

    /// <summary>
    ///     Workflow execution completed
    /// </summary>
    WorkflowCompleted,

    /// <summary>
    ///     Workflow execution failed
    /// </summary>
    WorkflowFailed,

    /// <summary>
    ///     Workflow was suspended
    /// </summary>
    WorkflowSuspended,

    /// <summary>
    ///     Workflow was resumed
    /// </summary>
    WorkflowResumed,

    /// <summary>
    ///     Workflow was cancelled
    /// </summary>
    WorkflowCancelled,

    /// <summary>
    ///     Saga compensation started
    /// </summary>
    CompensationStarted,

    /// <summary>
    ///     Saga compensation completed
    /// </summary>
    CompensationCompleted,

    /// <summary>
    ///     Property mapping occurred
    /// </summary>
    PropertyMapping,

    /// <summary>
    ///     Custom debug event
    /// </summary>
    DebugEvent,

    /// <summary>A cancellation request reached the run, with who made it.</summary>
    CancellationRequested,

    /// <summary>An operator released an instance stopped for them, with who did.</summary>
    Released,

    // Append kinds to preserve the stored numeric values of existing entries (G-01).
    /// <summary>A control-flow step was entered.</summary>
    StepEntered,
    /// <summary>A conditional selected its branch, identified by index and scope, never its value.</summary>
    ConditionTaken,
    /// <summary>One loop iteration started, with its one-based index.</summary>
    LoopIteration,
    /// <summary>A loop guard ended the loop.</summary>
    LoopCompleted,
    /// <summary>A parallel branch started.</summary>
    BranchStarted,
    /// <summary>A join finished, including joins unwound by cancellation or failure.</summary>
    JoinCompleted,
    /// <summary>A catch body was entered.</summary>
    CatchEntered,
    /// <summary>A finally body was entered.</summary>
    FinallyEntered,
    /// <summary>A ForEach item started.</summary>
    ForEachItemStarted,
    /// <summary>A ForEach item ended, normally or through unwind.</summary>
    ForEachItemEnded,
    /// <summary>An event was taken by a wait.</summary>
    EventReceived,
    /// <summary>An event was refused, with its reason.</summary>
    EventRefused,
    /// <summary>An operator resolved an invocation.</summary>
    ActivityResolved,
    /// <summary>A parent asked its child to cancel.</summary>
    ChildCancellationRequested,
    /// <summary>The per-commit trace limit omitted entries.</summary>
    Truncated,
    WaitTimedOut,
    DelayFired,
    SagaRetryStarted,
    StepRetryScheduled,
    CompensationFailed,
    TraceBudgetReached,

    /// <summary>A versioned hard-stop command was acknowledged.</summary>
    WorkflowTerminated
}

/// <summary>
///     Workflow definition metadata and execution structure
/// </summary>
public class WorkflowDefinition
{
    /// <summary>Placement declarations, excluded from executable structure identity.</summary>
    public List<WorkflowDemand> Demands { get; set; } = [];

    // Deployment policy is retained separately so an equivalent caller-built definition cannot bypass it.
    internal IReadOnlyList<WorkflowDemand> RegistrationDemands { get; set; } = [];

    /// <summary>
    ///     Unique identifier for this workflow definition
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    ///     Workflow name
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    ///     Workflow version
    /// </summary>
    public int Version { get; set; }

    /// <summary>
    ///     Description of the workflow
    /// </summary>
    public string Description { get; set; } = "";

    /// <summary>
    ///     Workflow data type
    /// </summary>
    public Type WorkflowDataType { get; set; } = null!;

    /// <summary>
    ///     When this definition was created
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    ///     Who created this definition
    /// </summary>
    public string CreatedBy { get; set; } = "";

    /// <summary>
    ///     Tags associated with this workflow
    /// </summary>
    public string[] Tags { get; set; } = Array.Empty<string>();

    /// <summary>
    ///     Additional metadata
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();

    /// <summary>
    ///     Executable workflow steps (populated by builder)
    /// </summary>
    public List<WorkflowStep> Steps { get; set; } = new();

    /// <summary>
    ///     Gets the first step in the workflow
    /// </summary>
    public WorkflowStep? FirstStep => Steps.FirstOrDefault();

}

/// <summary>
///     Represents an individual step in a workflow definition
/// </summary>
public class WorkflowStep
{
    /// <summary>The authoring source location, when available.</summary>
    public string? SourceLocation { get; set; }

    internal Runtime.WorkflowStepDelegates Runtime { get; set; } = null!;

    internal System.Linq.Expressions.LambdaExpression? OutcomeExpression { get; set; }

    /// <summary>
    ///     Unique identifier for this step
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    ///     Step name for debugging
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    ///     Type of step
    /// </summary>
    public WorkflowStepType StepType { get; set; }

    /// <summary>
    ///     Activity type (for activity steps)
    /// </summary>
    public Type? ActivityType { get; set; }

    /// <summary>
    ///     Workflow type (for workflow invocation steps)
    /// </summary>
    public Type? WorkflowType { get; set; }

    /// <summary>For a workflow invocation step, the child's data type its mappings name, checked when built.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Type? InvokedDataType { get; set; }

    /// <summary>
    ///     Workflow name (for name-based workflow invocation)
    /// </summary>
    public string? WorkflowName { get; set; }

    /// <summary>
    ///     Workflow version (for name-based workflow invocation)
    /// </summary>
    public int? WorkflowVersion { get; set; }

    /// <summary>
    ///     Property mappings for inputs
    /// </summary>
    public List<PropertyMapping> InputMappings { get; set; } = new();

    /// <summary>
    ///     Property mappings for outputs
    /// </summary>
    public List<PropertyMapping> OutputMappings { get; set; } = new();

    /// <summary>
    ///     Compiled condition function for maximum performance (used by both conditional and loop steps)
    /// </summary>
    public Func<object, bool>? CompiledCondition { get; set; }

    /// <summary>For an expression-based condition, the expression it was compiled from; its shape and referenced members contribute to the structure hash.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public System.Linq.Expressions.LambdaExpression? ConditionExpression { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public System.Linq.Expressions.LambdaExpression? ParallelCompletionExpression { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public System.Linq.Expressions.LambdaExpression? RequestSubjectExpression { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public System.Linq.Expressions.LambdaExpression? ForEachItemsExpression { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public System.Linq.Expressions.LambdaExpression? ResultExpression { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public System.Linq.Expressions.LambdaExpression? ForEachCompletionExpression { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    internal Type? ForEachStoredResultType { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    internal Type? ForEachConditionResultType { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public Func<object, object?>? OutcomeSelector { get; set; }

    public List<WorkflowOutcomeBranch> OutcomeBranches { get; set; } = new();

    /// <summary>
    ///     Steps to execute if condition is true
    /// </summary>
    public List<WorkflowStep> ThenSteps { get; set; } = new();

    /// <summary>
    ///     Steps to execute if condition is false
    /// </summary>
    public List<WorkflowStep> ElseSteps { get; set; } = new();

    /// <summary>
    ///     Steps within a sequence container
    /// </summary>
    public List<WorkflowStep> SequenceSteps { get; set; } = new();

    /// <summary>
    ///     Parallel execution branches
    /// </summary>
    public List<List<WorkflowStep>> ParallelBranches { get; set; } = new();

    /// <summary>
    /// Determines when parallel branches join. The default waits for every branch.
    /// </summary>
    public ParallelJoinMode ParallelJoinMode { get; set; } = ParallelJoinMode.WaitAll;

    /// <summary>
    /// Evaluated after each branch completes when the join is conditional.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<object, bool>? ParallelCompletionCondition { get; set; }

    /// <summary>
    /// For a parallel step, combiners it chooses for properties of the workflow data, by JSON property
    /// path; they take precedence over those declared on the data type.
    /// </summary>
    public Dictionary<string, Type> ParallelCombiners { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    ///     Catch blocks for try/catch error handling
    /// </summary>
    public List<WorkflowStep> CatchBlocks { get; set; } = new();

    /// <summary>
    ///     Finally steps for try/catch error handling
    /// </summary>
    public List<WorkflowStep> FinallySteps { get; set; } = new();

    /// <summary>Maximum time to wait for the matching event.</summary>
    public TimeSpan? WaitTimeout { get; set; }

    /// <summary>Duration of a timer step that advances without an event.</summary>
    public TimeSpan? DelayDuration { get; set; }

    /// <summary>Alternative steps run when a wait deadline expires.</summary>
    public List<WorkflowStep> TimeoutSteps { get; set; } = new();

    /// <summary>
    ///     Steps within a loop body (for loop steps)
    /// </summary>
    public List<WorkflowStep> LoopBodySteps { get; set; } = new();

    /// <summary>
    ///     Type of loop construct (for loop steps)
    /// </summary>
    public LoopType LoopType { get; set; }

    /// <summary>For a <c>ForEach</c> step, the body run once per item.</summary>
    public List<WorkflowStep> ForEachBodySteps { get; set; } = new();

    /// <summary>For a <c>ForEach</c> step, its body's handle on the current item.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ItemScope? ForEachItem { get; set; }

    /// <summary>For a <c>ForEach</c> step, the list it runs over, read from the context when the step starts.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<object, System.Collections.IEnumerable?>? ForEachItems { get; set; }

    /// <summary>
    /// For a <c>ForEach</c> step, where its results go: given the context and each item's result in order,
    /// stores a <c>List&lt;ItemResult&lt;TResult&gt;&gt;</c>.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Action<object, IReadOnlyList<(ItemStatus Status, object? Value)>>? ForEachInto { get; set; }

    /// <summary>
    /// For a <c>ForEach</c> step, true when its items run as parallel branches; its join is
    /// <see cref="ParallelJoinMode"/>.
    /// </summary>
    public bool ForEachParallel { get; set; }

    /// <summary>For a parallel <c>ForEach</c> step, how many items run at once, or null for all of them.</summary>
    public int? ForEachMaxConcurrency { get; set; }

    /// <summary>
    /// For a parallel <c>ForEach</c> step joined conditionally, decides on the provisional merge of the
    /// completed items' data and their results so far, by index.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<object, IReadOnlyList<(int Index, object? Value)>, bool>? ForEachCompletionCondition { get; set; }

    /// <summary>For a <c>ForEach</c> step, the member path its results are stored to, such as <c>WorkflowData.Results</c>.</summary>
    public string? ForEachIntoPath { get; set; }

    /// <summary>For a <c>ForEach</c> step that stores results, its target expression; its shape and referenced members contribute to the structure hash.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public System.Linq.Expressions.LambdaExpression? ForEachIntoExpression { get; set; }

    /// <summary>For a <c>.Result(...)</c> step, or a <c>ForEach</c> step that stores results, the result type.</summary>
    public Type? ResultType { get; set; }

    /// <summary>For a <c>.Result(...)</c> step, the value it gives for its item.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<object, object?>? ResultSelector { get; set; }

    /// <summary>
    ///     Context type information for compilation
    /// </summary>
    public Type WorkflowDataType { get; set; } = typeof(object);

    /// <summary>
    ///     Previous step data type (if applicable)
    /// </summary>
    public Type? PreviousStepDataType { get; set; }

    /// <summary>
    ///     Exception type (for catch blocks)
    /// </summary>
    public Type? ExceptionType { get; set; }

    /// <summary>The durable data type exposed to catch activities.</summary>
    public Type? FaultType { get; set; }

    /// <summary>
    ///     Resume event type (for resume operations)
    /// </summary>
    public Type? ResumeEventType { get; set; }

    /// <summary>A suggested event payload for discovery UIs only; excluded from the structure hash.</summary>
    public object? DefaultPayload { get; set; }

    /// <summary>For a request step, the name its <c>WaitFor</c> uses.</summary>
    public string? RequestName { get; set; }

    /// <summary>For a request step, the answer type its activity declares with <c>IRequestActivity&lt;TReply&gt;</c>.</summary>
    public Type? ReplyType { get; set; }

    /// <summary>
    /// For a request step, what the request is about, computed from the workflow context when its
    /// inputs are captured. A reply must carry the same subject.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<object, string>? RequestSubject { get; set; }

    /// <summary>For a <c>WaitFor</c> that takes a request's answer, the request's name.</summary>
    public string? ReplyToRequest { get; set; }

    /// <summary>
    /// For a child workflow invocation: whether cancelling the invoking branch, or failing the parent, cancels the
    /// child and waits for it to end (the default), or leaves it running on its own.
    /// </summary>
    public bool CancelWithParent { get; set; } = true;

    /// <summary>
    ///     Step execution order
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    ///     Step-specific metadata
    /// </summary>
    public Dictionary<string, object> StepMetadata { get; set; } = new();

    /// <summary>
    ///     Creates a copy of this step for execution tracking
    /// </summary>
    public WorkflowStep Clone()
    {
        return new WorkflowStep
        {
            Id = Id,
            Name = Name,
            StepType = StepType,
            ActivityType = ActivityType,
            WorkflowType = WorkflowType,
            InvokedDataType = InvokedDataType,
            WorkflowName = WorkflowName,
            WorkflowVersion = WorkflowVersion,
            CancelWithParent = CancelWithParent,
            InputMappings = InputMappings.Select(m => m.Clone()).ToList(),
            OutputMappings = OutputMappings.Select(m => m.Clone()).ToList(),
            SourceLocation = SourceLocation,
            Runtime = Runtime,
            OutcomeExpression = OutcomeExpression,
            CompiledCondition = CompiledCondition,
            ConditionExpression = ConditionExpression,
            ParallelCompletionExpression = ParallelCompletionExpression,
            RequestSubjectExpression = RequestSubjectExpression,
            OutcomeSelector = OutcomeSelector,
            OutcomeBranches = OutcomeBranches.Select(branch => new WorkflowOutcomeBranch
            {
                Value = branch.Value,
                IsDefault = branch.IsDefault,
                Steps = branch.Steps.Select(step => step.Clone()).ToList(),
                SagaSteps = new List<IxIFlow.Builders.SagaStepInfo>(branch.SagaSteps)
            }).ToList(),
            ThenSteps = ThenSteps.Select(s => s.Clone()).ToList(),
            ElseSteps = ElseSteps.Select(s => s.Clone()).ToList(),
            SequenceSteps = SequenceSteps.Select(s => s.Clone()).ToList(),
            ParallelBranches = ParallelBranches.Select(branch => branch.Select(s => s.Clone()).ToList()).ToList(),
            ParallelJoinMode = ParallelJoinMode,
            ParallelCompletionCondition = ParallelCompletionCondition,
            ParallelCombiners = new Dictionary<string, Type>(ParallelCombiners, StringComparer.Ordinal),
            CatchBlocks = CatchBlocks.Select(s => s.Clone()).ToList(),
            FinallySteps = FinallySteps.Select(s => s.Clone()).ToList(),
            WaitTimeout = WaitTimeout,
            DelayDuration = DelayDuration,
            TimeoutSteps = TimeoutSteps.Select(s => s.Clone()).ToList(),
            LoopBodySteps = LoopBodySteps.Select(s => s.Clone()).ToList(),
            LoopType = LoopType,
            ForEachBodySteps = ForEachBodySteps.Select(s => s.Clone()).ToList(),
            ForEachItem = ForEachItem,
            ForEachItems = ForEachItems,
            ForEachItemsExpression = ForEachItemsExpression,
            ResultExpression = ResultExpression,
            ForEachCompletionExpression = ForEachCompletionExpression,
            ForEachStoredResultType = ForEachStoredResultType,
            ForEachConditionResultType = ForEachConditionResultType,
            ForEachInto = ForEachInto,
            ForEachIntoPath = ForEachIntoPath,
            ForEachParallel = ForEachParallel,
            ForEachMaxConcurrency = ForEachMaxConcurrency,
            ForEachCompletionCondition = ForEachCompletionCondition,
            ForEachIntoExpression = ForEachIntoExpression,
            ResultType = ResultType,
            ResultSelector = ResultSelector,
            WorkflowDataType = WorkflowDataType,
            PreviousStepDataType = PreviousStepDataType,
            ExceptionType = ExceptionType,
            FaultType = FaultType,
            ResumeEventType = ResumeEventType,
            DefaultPayload = DefaultPayload,
            RequestName = RequestName,
            ReplyType = ReplyType,
            RequestSubject = RequestSubject,
            ReplyToRequest = ReplyToRequest,
            Order = Order,
            StepMetadata = new Dictionary<string, object>(StepMetadata)
        };
    }
}

public class WorkflowOutcomeBranch
{
    public object? Value { get; set; }
    public bool IsDefault { get; set; }
    public List<WorkflowStep> Steps { get; set; } = new();
    public List<IxIFlow.Builders.SagaStepInfo> SagaSteps { get; set; } = new();
}

/// <summary>
///     Types of workflow steps
/// </summary>
public enum WorkflowStepType
{
    /// <summary>
    ///     Execute an activity
    /// </summary>
    Activity,

    /// <summary>
    ///     Invoke another workflow
    /// </summary>
    WorkflowInvocation,

    /// <summary>
    ///     Conditional execution (if/then/else)
    /// </summary>
    Conditional,

    /// <summary>
    ///     Sequential container
    /// </summary>
    Sequence,

    /// <summary>
    ///     Parallel execution
    /// </summary>
    Parallel,

    /// <summary>
    ///     Try/catch error handling
    /// </summary>
    TryCatch,

    /// <summary>
    ///     Catch block within try/catch
    /// </summary>
    CatchBlock,

    /// <summary>
    ///     Saga transaction pattern
    /// </summary>
    Saga,

    /// <summary>
    ///     Suspend and resume pattern
    /// </summary>
    SuspendResume,

    /// <summary>
    ///     Loop constructs (while/do-while)
    /// </summary>
    Loop,

    /// <summary>Park until a durable timer becomes due.</summary>
    Delay,

    /// <summary>Run a body once per item of a list.</summary>
    ForEach,

    /// <summary>The last step of a <c>ForEach</c> body: the value it gives for its item.</summary>
    ForEachResult
}

/// <summary>
///     Types of loop constructs
/// </summary>
public enum LoopType
{
    /// <summary>
    ///     Do-while loop: execute body first, then check condition
    /// </summary>
    DoWhile,

    /// <summary>
    ///     While-do loop: check condition first, then execute body
    /// </summary>
    WhileDo
}

/// <summary>
///     Represents a property mapping between contexts and activity properties
/// </summary>
public class PropertyMapping
{
    internal Func<Runtime.MappingContextValues, object>? ContextFactory { get; set; }

    /// <summary>
    ///     Unique identifier for this mapping
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    ///     Target property name on the activity
    /// </summary>
    public string TargetProperty { get; set; } = string.Empty;

    /// <summary>The source or destination expression the mapping was built from; its shape and referenced members contribute to the structure hash.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public System.Linq.Expressions.LambdaExpression? Expression { get; set; }

    /// <summary>
    /// For an output, the member path it writes, such as <c>WorkflowData.Total</c>, or null when the
    /// destination is not a plain member chain.
    /// </summary>
    public string? TargetPath { get; set; }

    /// <summary>
    ///     Compiled source function from context (fast execution)
    /// </summary>
    public Func<object, object?> SourceFunction { get; set; } = default!;

    /// <summary>
    ///     Compiled target assignment function (for outputs)
    /// </summary>
    public Action<object, object?>? TargetAssignmentFunction { get; set; }

    /// <summary>
    ///     Source type from expression
    /// </summary>
    public Type SourceType { get; set; } = typeof(object);

    public CompensationInputSource CompensationSource { get; set; }

    /// <summary>
    ///     Target property type on activity
    /// </summary>
    public Type TargetType { get; set; } = typeof(object);

    /// <summary>
    ///     Direction of the mapping
    /// </summary>
    public PropertyMappingDirection Direction { get; set; }

    /// <summary>
    ///     Creates a copy of this property mapping
    /// </summary>
    public PropertyMapping Clone()
    {
        return new PropertyMapping
        {
            Id = Id,
            ContextFactory = ContextFactory,
            TargetProperty = TargetProperty,
            Expression = Expression,
            TargetPath = TargetPath,
            SourceFunction = SourceFunction,
            TargetAssignmentFunction = TargetAssignmentFunction,
            SourceType = SourceType,
            CompensationSource = CompensationSource,
            TargetType = TargetType,
            Direction = Direction
        };
    }
}

public enum CompensationInputSource
{
    CurrentStepContext,
    PreviousStep,
    PreviousCompensation
}

/// <summary>
///     Direction of property mapping
/// </summary>
public enum PropertyMappingDirection
{
    /// <summary>
    ///     From context to activity (input)
    /// </summary>
    Input,

    /// <summary>
    ///     From activity to context (output)
    /// </summary>
    Output
}
