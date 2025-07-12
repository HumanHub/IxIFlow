namespace IxIFlow.Core;

// =====================================================
// WORKFLOW CONTEXTS WITH COMPREHENSIVE TRACKING
// =====================================================

/// <summary>
///     Base workflow context providing access to workflow data and execution metadata
/// </summary>
/// <typeparam name="TWorkflowData">The type of workflow data</typeparam>
public class WorkflowContext<TWorkflowData>
{
    /// <summary>
    ///     The workflow data instance
    /// </summary>
    public TWorkflowData WorkflowData { get; set; } = default!;

    /// <summary>
    ///     Unique identifier for this workflow instance
    /// </summary>
    public string WorkflowInstanceId { get; set; } = "";

    /// <summary>
    ///     Workflow definition name
    /// </summary>
    public string WorkflowName { get; set; } = "";

    /// <summary>
    ///     Workflow version
    /// </summary>
    public int WorkflowVersion { get; set; }

    /// <summary>
    ///     Current execution step number
    /// </summary>
    public int CurrentStepNumber { get; set; }

    /// <summary>
    ///     Workflow execution start time
    /// </summary>
    public DateTime StartedAt { get; set; }

    /// <summary>
    ///     Correlation ID for tracking across systems
    /// </summary>
    public string CorrelationId { get; set; } = "";

    /// <summary>
    ///     Custom properties for workflow execution
    /// </summary>
    public Dictionary<string, object> Properties { get; set; } = new();
}

/// <summary>
///     Workflow context with access to previous step data
/// </summary>
/// <typeparam name="TWorkflowData">The type of workflow data</typeparam>
/// <typeparam name="TPreviousStepData">The type of data from the previous step</typeparam>
public class WorkflowContext<TWorkflowData, TPreviousStepData> : WorkflowContext<TWorkflowData>
    where TPreviousStepData : class
{
    /// <summary>
    ///     Data from the previous step execution
    /// </summary>
    public TPreviousStepData PreviousStep { get; set; } = null!;
}

/// <summary>
///     Exception context for error handling scenarios
/// </summary>
/// <typeparam name="TWorkflowData">The type of workflow data</typeparam>
/// <typeparam name="TException">The type of exception being handled</typeparam>
public class CatchContext<TWorkflowData, TException> : WorkflowContext<TWorkflowData>
    where TException : Exception
{
    /// <summary>
    ///     The exception that was caught
    /// </summary>
    public TException Exception { get; set; } = default!;

    /// <summary>
    ///     Name of the step where the exception occurred
    /// </summary>
    public string FailedStepName { get; set; } = "";

    /// <summary>
    ///     Stack trace of the execution path
    /// </summary>
    public string[] ExecutionStackTrace { get; set; } = [];

    /// <summary>
    ///     Number of retry attempts made
    /// </summary>
    public int RetryAttempt { get; set; }
}

/// <summary>
///     Exception context with access to previous step data
/// </summary>
/// <typeparam name="TWorkflowData">The type of workflow data</typeparam>
/// <typeparam name="TException">The type of exception being handled</typeparam>
/// <typeparam name="TPreviousStepData">The type of data from the previous step</typeparam>
public class CatchContext<TWorkflowData, TException, TPreviousStepData> : WorkflowContext<TWorkflowData, TPreviousStepData>
    where TException : Exception
    where TPreviousStepData : class
{
    /// <summary>
    ///     The exception that was caught
    /// </summary>
    public TException Exception { get; set; } = default!;

    /// <summary>
    ///     Name of the step where the exception occurred
    /// </summary>
    public string FailedStepName { get; set; } = "";

    /// <summary>
    ///     Stack trace of the execution path
    /// </summary>
    public string[] ExecutionStackTrace { get; set; } = Array.Empty<string>();

    /// <summary>
    ///     Number of retry attempts made
    /// </summary>
    public int RetryAttempt { get; set; }
}

/// <summary>
///     Resume event context for workflows resuming from suspension
/// </summary>
/// <typeparam name="TWorkflowData">The type of workflow data</typeparam>
/// <typeparam name="TResumeEvent">The type of resume event</typeparam>
public class ResumeEventContext<TWorkflowData, TResumeEvent> : WorkflowContext<TWorkflowData>
{
    /// <summary>
    ///     The event that triggered workflow resumption
    /// </summary>
    public TResumeEvent ResumeEvent { get; set; } = default!;

    /// <summary>
    ///     When the workflow was suspended
    /// </summary>
    public DateTime SuspendedAt { get; set; }

    /// <summary>
    ///     When the workflow was resumed
    /// </summary>
    public DateTime ResumedAt { get; set; }

    /// <summary>
    ///     Reason for suspension
    /// </summary>
    public string SuspendReason { get; set; } = "";

    /// <summary>
    ///     Duration of suspension
    /// </summary>
    public TimeSpan SuspensionDuration { get; set; }
}

/// <summary>
///     Resume event context with access to previous step data
/// </summary>
/// <typeparam name="TWorkflowData">The type of workflow data</typeparam>
/// <typeparam name="TResumeEvent">The type of resume event</typeparam>
/// <typeparam name="TPreviousStepData">The type of data from the previous step</typeparam>
public class ResumeEventContext<TWorkflowData, TResumeEvent, TPreviousStepData> : WorkflowContext<TWorkflowData,
    TPreviousStepData>
    where TPreviousStepData : class
{
    /// <summary>
    ///     The event that triggered workflow resumption
    /// </summary>
    public TResumeEvent ResumeEvent { get; set; } = default!;

    /// <summary>
    ///     When the workflow was suspended
    /// </summary>
    public DateTime SuspendedAt { get; set; }

    /// <summary>
    ///     When the workflow was resumed
    /// </summary>
    public DateTime ResumedAt { get; set; }

    /// <summary>
    ///     Reason for suspension
    /// </summary>
    public string SuspendReason { get; set; } = "";

    /// <summary>
    ///     Duration of suspension
    /// </summary>
    public TimeSpan SuspensionDuration { get; set; }
}

/// <summary>
///     Compensation context for saga pattern compensation activities
/// </summary>
/// <typeparam name="TWorkflowData">The type of workflow data</typeparam>
/// <typeparam name="TCurrentStepData">The type of data from the step being compensated</typeparam>
public class CompensationContext<TWorkflowData, TCurrentStepData> : WorkflowContext<TWorkflowData>
    where TCurrentStepData : class
{
    public TCurrentStepData CurrentStep { get; set; } = null!;

    /// <summary>
    ///     Name of the step being compensated
    /// </summary>
    public string CompensatingStepName { get; set; } = "";

    /// <summary>
    ///     When the original step was executed
    /// </summary>
    public DateTime OriginalStepExecutedAt { get; set; }

    /// <summary>
    ///     Reason for compensation
    /// </summary>
    public string CompensationReason { get; set; } = "";

    /// <summary>
    ///     Exception that triggered compensation (if any)
    /// </summary>
    public Exception? TriggeringException { get; set; }

    /// <summary>
    ///     Order in the compensation chain (0 = last successful step)
    /// </summary>
    public int CompensationOrder { get; set; }
}