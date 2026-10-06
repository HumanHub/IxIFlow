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
    ///     Who requested the cancellation
    /// </summary>
    public string RequestedBy { get; set; } = "";

    /// <summary>
    ///     Additional metadata about the cancellation
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
///     Workflow version information
/// </summary>
public class WorkflowVersionInfo
{
    /// <summary>
    ///     Workflow name
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>
    ///     Version number
    /// </summary>
    public int Version { get; set; }

    /// <summary>
    ///     Version description
    /// </summary>
    public string Description { get; set; } = "";

    /// <summary>
    ///     When this version was created
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    ///     Who created this version
    /// </summary>
    public string CreatedBy { get; set; } = "";

    /// <summary>
    ///     Whether this version is active
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    ///     Whether this is the default version
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    ///     Tags associated with this version
    /// </summary>
    public string[] Tags { get; set; } = Array.Empty<string>();
}

/// <summary>
///     Workflow options for execution control
/// </summary>
public class WorkflowOptions
{
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
    ///     Whether to enable debugging support
    /// </summary>
    public bool EnableDebugging { get; set; } = false;

    /// <summary>
    ///     Custom correlation ID
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    ///     Custom properties for the workflow execution
    /// </summary>
    public Dictionary<string, object> Properties { get; set; } = new();

    /// <summary>
    ///     Retry policy for the entire workflow
    /// </summary>
    public RetryPolicy? WorkflowRetryPolicy { get; set; }

    /// <summary>
    ///     Whether to persist workflow state
    /// </summary>
    public bool PersistState { get; set; } = true;

    /// <summary>
    ///     Priority level for workflow execution
    /// </summary>
    public int Priority { get; set; } = 0;

    // ========== HOST SELECTION CONTROLS ==========

    /// <summary>
    ///     MUST run on this specific host (highest priority)
    /// </summary>
    public string? RequiredHostId { get; set; }

    /// <summary>
    ///     PREFER to run on this specific host (with fallback)
    /// </summary>
    public string? PreferredHostId { get; set; }

    /// <summary>
    ///     MUST have ALL of these tags to be eligible
    /// </summary>
    public string[] RequiredTags { get; set; } = Array.Empty<string>();

    /// <summary>
    ///     PREFER hosts with ANY of these tags
    /// </summary>
    public string[] PreferredTags { get; set; } = Array.Empty<string>();

    /// <summary>
    ///     Allow fallback to non-tagged hosts if no tagged hosts available
    /// </summary>
    public bool AllowTagFallback { get; set; } = true;

    // ========== IMMEDIATE EXECUTION CONTROLS ==========

    /// <summary>
    ///     Execute immediately, bypassing the normal queue
    /// </summary>
    public bool ExecuteImmediately { get; set; } = false;

    /// <summary>
    ///     Force execution even if host is at capacity (emergency execution)
    /// </summary>
    public bool OverrideCapacityLimit { get; set; } = false;
}

/// <summary>
///     Represents a runtime workflow instance
/// </summary>
public class WorkflowInstance
{
    /// <summary>The revision assigned by an atomic repository commit.</summary>
    public long Revision { get; set; }

    /// <summary>Runtime-only token used to fence checkpoint writes by a former owner.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ExecutionLeaseToken { get; set; }

    internal WorkflowInstance CopyForResume() => (WorkflowInstance)MemberwiseClone();

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

    /// <summary>Earliest persisted deadline that can wake an idle instance.</summary>
    public DateTime? NextDueAtUtc { get; set; }

    /// <summary>
    ///     Current step number being executed
    /// </summary>
    public int CurrentStepNumber { get; set; }

    /// <summary>
    ///     Total number of steps in the workflow
    /// </summary>
    public int TotalSteps { get; set; }

    /// <summary>
    ///     Serialized workflow data
    /// </summary>
    public string WorkflowDataJson { get; set; } = "";

    /// <summary>
    ///     Workflow data type for deserialization
    /// </summary>
    public string WorkflowDataType { get; set; } = "";

    /// <summary>
    ///     Serialized workflow definition (for resume operations)
    /// </summary>
    public string WorkflowDefinitionJson { get; set; } = "";

    /// <summary>
    ///     Serialized execution state at suspension point (for resume operations)
    /// </summary>
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
    public Dictionary<string, object> Properties { get; set; } = new();

    /// <summary>Whether optional execution history entries are recorded.</summary>
    public bool TracingEnabled { get; set; } = true;

    /// <summary>
    ///     Execution history entries
    /// </summary>
    public List<ExecutionTraceEntry> ExecutionHistory { get; set; } = new();

    /// <summary>
    ///     Current suspension information (if suspended)
    /// </summary>
    public SuspensionInfo? SuspensionInfo { get; set; }

    /// <summary>
    ///     Structured execution snapshot for resume, replay, and debugging
    /// </summary>
    public WorkflowExecutionSnapshot? ExecutionSnapshot { get; set; }
}

/// <summary>
///     Structured execution snapshot for durable resume and future debugger support
/// </summary>
public class WorkflowExecutionSnapshot
{
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

    /// <summary>
    ///     Serialized values used by the runtime during resume
    /// </summary>
    public Dictionary<string, string> Values { get; set; } = new();

    /// <summary>
    ///     When the snapshot was captured
    /// </summary>
    public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;
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
    ///     Optional parent pointer for nested execution
    /// </summary>
    public string? ParentPointerId { get; set; }

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
///     Information about workflow suspension
/// </summary>
public class SuspensionInfo
{
    /// <summary>
    ///     Identifies this particular wait, including repeated waits at the same step.
    /// </summary>
    public string SuspensionId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    ///     When the workflow was suspended
    /// </summary>
    public DateTime SuspendedAt { get; set; }

    /// <summary>
    ///     Reason for suspension
    /// </summary>
    public string SuspendReason { get; set; } = "";

    /// <summary>
    ///     Type of event expected for resumption
    /// </summary>
    public string ResumeEventType { get; set; } = "";

    /// <summary>
    ///     Serialized resume condition
    /// </summary>
    public string ResumeConditionJson { get; set; } = "";

    /// <summary>
    ///     When the suspension expires (if applicable)
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    ///     Additional suspension metadata
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
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
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    ///     Step number in the workflow
    /// </summary>
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
    public TimeSpan? Duration { get; set; }

    /// <summary>
    ///     Input data for the activity (serialized)
    /// </summary>
    public string InputDataJson { get; set; } = "";

    /// <summary>
    ///     Output data from the activity (serialized)
    /// </summary>
    public string OutputDataJson { get; set; } = "";

    /// <summary>
    ///     Error information (if applicable)
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    ///     Stack trace (if error occurred)
    /// </summary>
    public string? StackTrace { get; set; }

    /// <summary>
    ///     Additional metadata for this trace entry
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
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
    DebugEvent
}

/// <summary>
///     Workflow definition metadata and execution structure
/// </summary>
public class WorkflowDefinition
{
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
    ///     Estimated number of steps
    /// </summary>
    public int EstimatedStepCount { get; set; }

    /// <summary>
    ///     Whether this workflow supports suspension
    /// </summary>
    public bool SupportsSuspension { get; set; }

    /// <summary>
    ///     Whether this workflow uses saga pattern
    /// </summary>
    public bool UsesSagaPattern { get; set; }

    /// <summary>
    ///     Whether this workflow supports parallel execution
    /// </summary>
    public bool SupportsParallelExecution { get; set; }

    /// <summary>
    ///     Workflow execution factory
    /// </summary>
    public Func<object> WorkflowFactory { get; set; } = null!;

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

    /// <summary>
    ///     Gets a step by its ID
    /// </summary>
    public WorkflowStep? GetStep(string stepId)
    {
        return Steps.FirstOrDefault(s => s.Id == stepId);
    }

    /// <summary>
    ///     Gets the next step after the given step
    /// </summary>
    public WorkflowStep? GetNextStep(WorkflowStep currentStep)
    {
        var currentIndex = Steps.IndexOf(currentStep);
        return currentIndex >= 0 && currentIndex < Steps.Count - 1
            ? Steps[currentIndex + 1]
            : null;
    }
}

/// <summary>
///     Represents an individual step in a workflow definition
/// </summary>
public class WorkflowStep
{
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
    ///     Catch blocks for try/catch error handling
    /// </summary>
    public List<WorkflowStep> CatchBlocks { get; set; } = new();

    /// <summary>
    ///     Finally steps for try/catch error handling
    /// </summary>
    public List<WorkflowStep> FinallySteps { get; set; } = new();

    /// <summary>Maximum time to wait for the matching event.</summary>
    public TimeSpan? WaitTimeout { get; set; }

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
            WorkflowName = WorkflowName,
            WorkflowVersion = WorkflowVersion,
            InputMappings = InputMappings.Select(m => m.Clone()).ToList(),
            OutputMappings = OutputMappings.Select(m => m.Clone()).ToList(),
            CompiledCondition = CompiledCondition,
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
            CatchBlocks = CatchBlocks.Select(s => s.Clone()).ToList(),
            FinallySteps = FinallySteps.Select(s => s.Clone()).ToList(),
            WaitTimeout = WaitTimeout,
            TimeoutSteps = TimeoutSteps.Select(s => s.Clone()).ToList(),
            LoopBodySteps = LoopBodySteps.Select(s => s.Clone()).ToList(),
            LoopType = LoopType,
            WorkflowDataType = WorkflowDataType,
            PreviousStepDataType = PreviousStepDataType,
            ExceptionType = ExceptionType,
            FaultType = FaultType,
            ResumeEventType = ResumeEventType,
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
    Loop
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
    /// <summary>
    ///     Unique identifier for this mapping
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    ///     Target property name on the activity
    /// </summary>
    public string TargetProperty { get; set; } = string.Empty;

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
            TargetProperty = TargetProperty,
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
