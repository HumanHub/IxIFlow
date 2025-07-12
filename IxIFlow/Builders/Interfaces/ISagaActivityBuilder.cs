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
        Action<ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>> configure)
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
