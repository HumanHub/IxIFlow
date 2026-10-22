using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using IxIFlow.Core;

namespace IxIFlow.Builders.Interfaces;

/// <summary>Builds a conditional saga handler body; policy belongs to the enclosing handler.</summary>
public interface ISagaErrorBodyBuilder<TWorkflowData, TFault, TPreviousStepData>
    where TPreviousStepData : class
{
    /// <summary>Assigns one serialized workflow-data location, retaining the previous value.</summary>
    ISagaErrorBodyBuilder<TWorkflowData, TFault, TPreviousStepData> Assign<TValue>(
        Expression<Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, TValue>> target,
        Expression<Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, TValue>> value,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    ISagaErrorBodyBuilder<TWorkflowData, TFault, TPreviousStepData> Switch<TValue>(
        Expression<Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, TValue>> selector,
        Action<SwitchCases<TValue, ISagaErrorBodyBuilder<TWorkflowData, TFault, TPreviousStepData>>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    /// <summary>Identifies the preceding step and its source.</summary>
    ISagaErrorBodyBuilder<TWorkflowData, TFault, TPreviousStepData> WithId(string id, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    ISagaErrorBodyBuilder<TWorkflowData, TFault, TPreviousStepData> Step<TActivity>([CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TActivity : class, IAsyncActivity;

    ISagaErrorBodyBuilder<TWorkflowData, TFault, TActivity> Step<TActivity>(
        Action<ICatchActivitySetupBuilder<TWorkflowData, TActivity, TFault, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TActivity : class, IAsyncActivity;

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    ISagaErrorBodyBuilder<TWorkflowData, TFault, TEvent> WaitFor<TEvent>(
        string key,
        Expression<Func<TEvent, FaultContext<TWorkflowData, TFault, TPreviousStepData>, bool>>? matchesExpression = null,
        Action<IFaultSuspendSetupBuilder<TWorkflowData, TFault, TEvent, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TEvent : class;

    ISagaErrorBodyBuilder<TWorkflowData, TFault, TEvent> WaitFor<TEvent>(
        string key,
        Func<TEvent, FaultContext<TWorkflowData, TFault, TPreviousStepData>, bool>? matches = null,
        Action<IFaultSuspendSetupBuilder<TWorkflowData, TFault, TEvent, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TEvent : class;

}
