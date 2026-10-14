using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;

namespace IxIFlow.Builders;

/// <summary>Appends saga error activities to their own checkpointed handler scope.</summary>
public sealed class SagaErrorBuilder<TWorkflowData, TFault, TPreviousStepData>(
    List<WorkflowStep> steps,
    List<SagaStepInfo> sagaSteps,
    List<ErrorHandler> errorHandlers)
    : ISagaErrorBuilder<TWorkflowData, TFault, TPreviousStepData>
    where TWorkflowData : class
    where TPreviousStepData : class
{
    public ISagaContinuationBuilder<TWorkflowData, TPreviousStepData> Compensate() =>
        ContinueWith(CompensationStrategy.CompensateAll);

    public ISagaContinuationBuilder<TWorkflowData, TPreviousStepData> CompensateNone() =>
        ContinueWith(CompensationStrategy.None);

    public ISagaContinuationBuilder<TWorkflowData, TPreviousStepData> CompensateUpTo<TActivity>()
        where TActivity : IAsyncActivity =>
        ContinueWith(CompensationStrategy.CompensateUpTo, typeof(TActivity));

    private ISagaContinuationBuilder<TWorkflowData, TPreviousStepData> ContinueWith(
        CompensationStrategy strategy, Type? target = null) =>
        new SagaContinuationBuilder<TWorkflowData, TPreviousStepData>(steps, sagaSteps,
            errorHandlers, new CompensationErrorHandler
            {
                Strategy = strategy,
                CompensationTargetType = target,
                SagaSteps = sagaSteps
            });

    public ISagaErrorBuilder<TWorkflowData, TFault, TPreviousStepData> Step<TActivity>()
        where TActivity : class, IAsyncActivity
    {
        steps.Add(new WorkflowStep
        {
            Name = typeof(TActivity).Name,
            StepType = WorkflowStepType.Activity,
            ActivityType = typeof(TActivity),
            WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = typeof(TPreviousStepData),
            Order = steps.Count
        });
        return this;
    }

    public ISagaErrorBuilder<TWorkflowData, TFault, TActivity> Step<TActivity>(
        Action<ICatchActivitySetupBuilder<TWorkflowData, TActivity, TFault, TPreviousStepData>> configure)
        where TActivity : class, IAsyncActivity
    {
        var step = new WorkflowStep
        {
            Name = typeof(TActivity).Name,
            StepType = WorkflowStepType.Activity,
            ActivityType = typeof(TActivity),
            WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = typeof(TPreviousStepData),
            FaultType = typeof(TFault),
            Order = steps.Count
        };
        configure(new CatchActivitySetupBuilder<TWorkflowData, TActivity, TFault, TPreviousStepData>(step));
        steps.Add(step);
        return new SagaErrorBuilder<TWorkflowData, TFault, TActivity>(steps, sagaSteps, errorHandlers);
    }

    public ISagaErrorBuilder<TWorkflowData, TFault, TEvent> WaitFor<TEvent>(
        string key,
        Func<TEvent, WorkflowContext<TWorkflowData>, bool>? matches = null,
        Action<ISuspendSetupBuilder<TWorkflowData, TEvent, TPreviousStepData>>? configure = null)
        where TEvent : class
    {
        new WorkflowBuilder<TWorkflowData, TPreviousStepData>(steps).WaitFor(key, matches, configure);
        return new SagaErrorBuilder<TWorkflowData, TFault, TEvent>(steps, sagaSteps, errorHandlers);
    }
}
