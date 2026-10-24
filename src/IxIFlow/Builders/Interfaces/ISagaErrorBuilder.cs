using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using IxIFlow.Core;

namespace IxIFlow.Builders.Interfaces;

/// <summary>Builds the activities and recovery policy for one saga error handler.</summary>
public interface ISagaErrorBuilder<TWorkflowData, TFault, TPreviousStepData>
    where TPreviousStepData : class
{
    /// <summary>Assigns one serialized workflow-data location, retaining the previous value.</summary>
    ISagaErrorBuilder<TWorkflowData, TFault, TPreviousStepData> Assign<TValue>(
        Expression<Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, TValue>> target,
        Expression<Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, TValue>> value,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);
    /// <summary>Raises a business exception constructed from the current context.</summary>
    ISagaErrorBuilder<TWorkflowData, TFault, TPreviousStepData> Throw<TException>(Expression<Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, TException>> value,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TException : Exception;
    /// <summary>Propagates the original error of the lexically enclosing Catch.</summary>
    ISagaErrorBuilder<TWorkflowData, TFault, TPreviousStepData> Rethrow([CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    ISagaErrorBuilder<TWorkflowData, TFault, TPreviousStepData> CancellationScope(
        Action<ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>> body,
        Action<ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>> onCancelled,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    ISagaErrorBuilder<TWorkflowData, TFault, TPreviousStepData> Switch<TValue>(
        Expression<Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, TValue>> selector,
        Action<SwitchCases<TValue, ISagaErrorBodyBuilder<TWorkflowData, TFault, TPreviousStepData>>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

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

    ISagaErrorBuilder<TWorkflowData, TFault, TPreviousStepData> Step<TActivity>([CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TActivity : class, IAsyncActivity;

    ISagaErrorBuilder<TWorkflowData, TFault, TActivity> Step<TActivity>(
        Action<ICatchActivitySetupBuilder<TWorkflowData, TActivity, TFault, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TActivity : class, IAsyncActivity;

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    ISagaErrorBuilder<TWorkflowData, TFault, TEvent> WaitFor<TEvent>(
        string key,
        Expression<Func<TEvent, FaultContext<TWorkflowData, TFault, TPreviousStepData>, bool>>? matchesExpression = null,
        Action<IFaultSuspendSetupBuilder<TWorkflowData, TFault, TEvent, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TEvent : class;

    ISagaErrorBuilder<TWorkflowData, TFault, TEvent> WaitFor<TEvent>(
        string key,
        Func<TEvent, FaultContext<TWorkflowData, TFault, TPreviousStepData>, bool>? matches = null,
        Action<IFaultSuspendSetupBuilder<TWorkflowData, TFault, TEvent, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TEvent : class;

}
