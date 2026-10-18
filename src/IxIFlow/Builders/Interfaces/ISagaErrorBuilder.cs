using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using IxIFlow.Core;

namespace IxIFlow.Builders.Interfaces;

/// <summary>Builds the activities and recovery policy for one saga error handler.</summary>
public interface ISagaErrorBuilder<TWorkflowData, TFault, TPreviousStepData>
    where TPreviousStepData : class
{
    /// <summary>Identifies the preceding step and its source.</summary>
    ISagaErrorBuilder<TWorkflowData, TFault, TPreviousStepData> WithId(string id, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    /// <summary>Undoes every completed step of the saga, newest first.</summary>
    ISagaContinuationBuilder<TWorkflowData, TPreviousStepData> Compensate();

    /// <summary>
    /// Undoes nothing: every completed step is kept. A retry starts again at the step that failed.
    /// </summary>
    ISagaContinuationBuilder<TWorkflowData, TPreviousStepData> CompensateNone();

    /// <summary>
    /// Undoes completed steps from the newest back up to <typeparamref name="TActivity"/>, which is undone too,
    /// and keeps the steps before it; a retry starts again at <typeparamref name="TActivity"/>. Only completed steps are
    /// undone: when <typeparamref name="TActivity"/> had not completed, everything is undone. The activity
    /// must be a step of the saga exactly once.
    /// </summary>
    ISagaContinuationBuilder<TWorkflowData, TPreviousStepData> CompensateUpTo<TActivity>()
        where TActivity : IAsyncActivity;

    ISagaErrorBuilder<TWorkflowData, TFault, TPreviousStepData> Step<TActivity>()
        where TActivity : class, IAsyncActivity;

    ISagaErrorBuilder<TWorkflowData, TFault, TActivity> Step<TActivity>(
        Action<ICatchActivitySetupBuilder<TWorkflowData, TActivity, TFault, TPreviousStepData>> configure)
        where TActivity : class, IAsyncActivity;

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    ISagaErrorBuilder<TWorkflowData, TFault, TEvent> WaitFor<TEvent>(
        string key,
        Expression<Func<TEvent, WorkflowContext<TWorkflowData>, bool>>? matchesExpression = null,
        Action<ISuspendSetupBuilder<TWorkflowData, TEvent, TPreviousStepData>>? configure = null)
        where TEvent : class;

    ISagaErrorBuilder<TWorkflowData, TFault, TEvent> WaitFor<TEvent>(
        string key,
        Func<TEvent, WorkflowContext<TWorkflowData>, bool>? matches = null,
        Action<ISuspendSetupBuilder<TWorkflowData, TEvent, TPreviousStepData>>? configure = null)
        where TEvent : class;

}
