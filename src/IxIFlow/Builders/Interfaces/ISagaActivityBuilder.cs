using System.Linq.Expressions;
using IxIFlow.Core;

namespace IxIFlow.Builders.Interfaces;

/// <summary>
///     Builder for configuring activities within saga steps with access to previous step data
/// </summary>
public interface ISagaActivityBuilder<TWorkflowData, TPreviousStepData> //: ISagaBuilder<TWorkflowData, TPreviousStepData>
    where TPreviousStepData : class
{
    /// <summary>
    /// Adds a step to the saga execution
    /// </summary>
    /// <typeparam name="TActivity">The activity type for this step</typeparam>
    /// <param name="configure">Configuration for the saga activity</param>
    ISagaActivityBuilder<TWorkflowData, TActivity> Step<TActivity>(
        Action<ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>>? configure = null)
        where TActivity : class, IAsyncActivity;

    /// <summary>
    /// Runs a request activity (<see cref="IRequestActivity{TReply}"/>) as a saga step, whose answer the
    /// saga takes with <c>WaitFor&lt;TReply&gt;(name)</c> later in the saga (D-33). The answer's wait opens
    /// before the activity runs, and the activity sends <see cref="IActivityContext.Reply"/> with the
    /// request. When the saga leaves its forward pass, to compensate, retry, continue or terminate, the
    /// wait closes: an answer that arrives later is refused as cancelled, and one taken before but not yet
    /// reached by the <c>WaitFor</c> is dropped. A saga retry runs the request again with a new wait. A
    /// compensation runs whether or not the request was answered. A <paramref name="subject"/> says what the
    /// request is about; an answer must carry the same one.
    /// </summary>
    ISagaActivityBuilder<TWorkflowData, TActivity> Request<TActivity>(
        string name,
        Action<ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>>? configure = null,
        Func<WorkflowContext<TWorkflowData, TPreviousStepData>, string>? subject = null)
        where TActivity : class, IAsyncActivity;

    /// <summary>
    /// Suspends workflow execution until a resume event is received
    /// </summary>
    /// <typeparam name="TResumeEvent">Type of the resume event</typeparam>
    /// <param name="suspendReason">Reason for suspension</param>
    /// <param name="resumeCondition">Optional condition to resume execution</param>
    /// <param name="configure">Configuration for suspension setup</param>
    /// <returns>A workflow builder for chaining additional steps</returns>
    ISagaActivityBuilder<TWorkflowData, TResumeEvent> Suspend<TResumeEvent>(
        string suspendReason,
        Func<TResumeEvent, WorkflowContext<TWorkflowData, TPreviousStepData>, bool>? resumeCondition = null,
        Action<ISuspendSetupBuilder<TWorkflowData, TResumeEvent, TPreviousStepData>>? configure = null)
        where TResumeEvent : class;

    ISagaActivityBuilder<TWorkflowData, TResumeEvent> WaitFor<TResumeEvent>(
        string key,
        Func<TResumeEvent, WorkflowContext<TWorkflowData, TPreviousStepData>, bool>? matches = null,
        Action<ISuspendSetupBuilder<TWorkflowData, TResumeEvent, TPreviousStepData>>? configure = null)
        where TResumeEvent : class;

    /// <summary>
    /// Creates outcome-based branching based on a property value from the workflow context
    /// </summary>
    /// <typeparam name="TProperty">The type of the property used for branching</typeparam>
    /// <param name="propertySelector">Expression that selects the property to branch on</param>
    /// <param name="configure">Configuration for the outcome branches</param>
    /// <returns>A saga activity builder for continuing the saga flow</returns>
    ISagaActivityBuilder<TWorkflowData, TPreviousStepData> OutcomeOn<TProperty>(
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, TProperty>> propertySelector,
        Action<ISagaOutcomeBuilder<TWorkflowData, TPreviousStepData, TProperty>> configure);
}
