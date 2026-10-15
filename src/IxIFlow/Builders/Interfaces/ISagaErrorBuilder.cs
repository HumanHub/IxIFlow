using IxIFlow.Core;

namespace IxIFlow.Builders.Interfaces;

/// <summary>Builds the activities and recovery policy for one saga error handler.</summary>
public interface ISagaErrorBuilder<TWorkflowData, TFault, TPreviousStepData>
    where TPreviousStepData : class
{
    ISagaContinuationBuilder<TWorkflowData, TPreviousStepData> Compensate();
    ISagaContinuationBuilder<TWorkflowData, TPreviousStepData> CompensateNone();
    ISagaContinuationBuilder<TWorkflowData, TPreviousStepData> CompensateUpTo<TActivity>()
        where TActivity : IAsyncActivity;

    ISagaErrorBuilder<TWorkflowData, TFault, TPreviousStepData> Step<TActivity>()
        where TActivity : class, IAsyncActivity;

    ISagaErrorBuilder<TWorkflowData, TFault, TActivity> Step<TActivity>(
        Action<ICatchActivitySetupBuilder<TWorkflowData, TActivity, TFault, TPreviousStepData>> configure)
        where TActivity : class, IAsyncActivity;

    ISagaErrorBuilder<TWorkflowData, TFault, TEvent> WaitFor<TEvent>(
        string key,
        Func<TEvent, WorkflowContext<TWorkflowData>, bool>? matches = null,
        Action<ISuspendSetupBuilder<TWorkflowData, TEvent, TPreviousStepData>>? configure = null)
        where TEvent : class;
}
