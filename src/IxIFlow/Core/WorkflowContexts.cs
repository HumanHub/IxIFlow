namespace IxIFlow.Core;

/// <summary>
///     Base workflow context providing access to workflow data, loop items and the engine clock
/// </summary>
/// <typeparam name="TWorkflowData">The type of workflow data</typeparam>
public class WorkflowContext<TWorkflowData> : IItemContext
{
    /// <summary>
    ///     The workflow data instance
    /// </summary>
    public TWorkflowData WorkflowData { get; set; } = default!;

    /// <summary>
    /// The time on the engine's clock, the way for an expression to read time: <c>DateTime.Now</c> and the like
    /// fail the build. An expression may be evaluated again after a recovery, and then reads the time of that run.
    /// </summary>
    public DateTimeOffset UtcNow => Runtime.RunClock.Current.GetUtcNow();

    /// <summary>The items of the <c>ForEach</c> bodies this context is made in.</summary>
    internal ItemValues Items { get; set; } = ItemValues.None;

    ItemValues IItemContext.Items
    {
        get => Items;
        set => Items = value;
    }

    /// <summary>The current item of the <c>ForEach</c> body that <paramref name="item"/> belongs to.</summary>
    public TItem Item<TItem>(ItemScope<TItem> item) => (TItem)Items.Of(item).Value!;

    /// <summary>The position in its list of the current item of <paramref name="item"/>'s body, from zero.</summary>
    public int Index<TItem>(ItemScope<TItem> item) => Items.Of(item).Index;
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
}

/// <summary>
/// The context of a saga request's compensation that reads the request's answer.
/// </summary>
/// <typeparam name="TWorkflowData">The type of workflow data</typeparam>
/// <typeparam name="TCurrentStepData">The request activity being compensated</typeparam>
/// <typeparam name="TReply">The answer the request is answered with</typeparam>
public class RequestCompensationContext<TWorkflowData, TCurrentStepData, TReply>
    : CompensationContext<TWorkflowData, TCurrentStepData>
    where TCurrentStepData : class
    where TReply : class
{
    /// <summary>
    /// The answer the request received: the one its <c>WaitFor</c> took, or one that arrived before the
    /// <c>WaitFor</c> was reached. Null when none came.
    /// </summary>
    public TReply? Reply { get; set; }

    /// <summary>Whether the request received an answer.</summary>
    public bool Answered { get; set; }
}