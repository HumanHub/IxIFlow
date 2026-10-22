using System.Runtime.CompilerServices;
using System.Linq.Expressions;
using IxIFlow.Core;

namespace IxIFlow.Builders.Interfaces;

/// <summary>
/// Configures catch blocks in workflows with access to exception context
/// </summary>
public interface ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>
    where TPreviousStepData : class
{
    /// <summary>Assigns one serialized workflow-data location, retaining the previous value.</summary>
    ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> Assign<TValue>(
        Expression<Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, TValue>> target,
        Expression<Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, TValue>> value,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> Named(string name);
    ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> Delay(TimeSpan duration,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);
    ICatchWorkflowBuilder<TWorkflowData, TFault, TActivity> Request<TActivity>(string name,
        Action<ICatchActivitySetupBuilder<TWorkflowData, TActivity, TFault, TPreviousStepData>>? configure = null,
        Expression<Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, string>>? subjectExpression = null,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TActivity : class, IAsyncActivity;
    ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> Switch<TValue>(
        Expression<Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, TValue>> selector,
        Action<SwitchCases<TValue, ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    /// <summary>Identifies the preceding step and its source.</summary>
    ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> WithId(string id, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    /// <summary>
    /// Adds a step to handle the caught exception
    /// </summary>
    /// <typeparam name="TActivity">The activity type to execute for exception handling</typeparam>
    /// <param name="configure">Configuration for the exception handling activity</param>
    ICatchWorkflowBuilder<TWorkflowData, TFault, TActivity> Step<TActivity>(
        Action<ICatchActivitySetupBuilder<TWorkflowData, TActivity, TFault, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TActivity : class, IAsyncActivity;

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    ICatchWorkflowBuilder<TWorkflowData, TFault, TEvent> WaitFor<TEvent>(
        string key,
        Expression<Func<TEvent, FaultContext<TWorkflowData, TFault, TPreviousStepData>, bool>>? matchesExpression = null,
        Action<IFaultSuspendSetupBuilder<TWorkflowData, TFault, TEvent, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TEvent : class;

    ICatchWorkflowBuilder<TWorkflowData, TFault, TEvent> WaitFor<TEvent>(
        string key,
        Func<TEvent, FaultContext<TWorkflowData, TFault, TPreviousStepData>, bool>? matches = null,
        Action<IFaultSuspendSetupBuilder<TWorkflowData, TFault, TEvent, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TEvent : class;

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    ICatchWorkflowBuilder<TWorkflowData, TFault, TEvent> Suspend<TEvent>(
        string reason,
        Expression<Func<TEvent, FaultContext<TWorkflowData, TFault, TPreviousStepData>, bool>>? matchesExpression = null,
        Action<IFaultSuspendSetupBuilder<TWorkflowData, TFault, TEvent, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TEvent : class;

    ICatchWorkflowBuilder<TWorkflowData, TFault, TEvent> Suspend<TEvent>(
        string reason,
        Func<TEvent, FaultContext<TWorkflowData, TFault, TPreviousStepData>, bool>? matches = null,
        Action<IFaultSuspendSetupBuilder<TWorkflowData, TFault, TEvent, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TEvent : class;

    ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> Parallel(
        Action<IFaultParallelBuilder<TWorkflowData, TFault, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    ICatchWorkflowBuilder<TWorkflowData, TFault, TUsedData> Use<TWorkflow, TUsedData>(
        Action<IWorkflowInlineSetupBuilder<TWorkflowData, TUsedData, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TWorkflow : IWorkflow<TUsedData>, new()
        where TUsedData : class;

    ICatchWorkflowBuilder<TWorkflowData, TFault, TUsedData> Use<TUsedData>(
        Func<WorkflowDefinition> callee, WorkflowInlinePin pin,
        Action<IWorkflowInlineSetupBuilder<TWorkflowData, TUsedData, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TUsedData : class;

    ICatchWorkflowBuilder<TWorkflowData, TFault, TInvokedData> Invoke<TWorkflow, TInvokedData>(
        Action<FaultInvocationSetupBuilder<TWorkflowData, TFault, TInvokedData, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TWorkflow : IWorkflow<TInvokedData>
        where TInvokedData : class;

    ICatchWorkflowBuilder<TWorkflowData, TFault, TInvokedData> Invoke<TInvokedData>(
        string workflowName, int version,
        Action<FaultInvocationSetupBuilder<TWorkflowData, TFault, TInvokedData, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TInvokedData : class;

    /// <summary>Invokes a workflow using an explicit pinned input and output contract.</summary>
    ICatchWorkflowBuilder<TWorkflowData, TFault, TInvokedData> Invoke<TInvokedData>(
        string workflowName, int version, WorkflowContract contract,
        Action<FaultInvocationSetupBuilder<TWorkflowData, TFault, TInvokedData, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TInvokedData : class;

    ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> Sequence(
        Action<ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    /// <summary>Runs a flowchart in the catch body, retaining the incoming previous value and fault context.</summary>
    ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> Flowchart(
        Action<FaultFlowchartBuilder<TWorkflowData, TFault, TPreviousStepData>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    /// <summary>Runs a native state machine, retaining the incoming previous value.</summary>
    ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> StateMachine(
        Action<FaultStateMachineBuilder<TWorkflowData, TFault, TPreviousStepData>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> If(
        Expression<Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, bool>> condition,
        Action<ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>> then,
        Action<ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>>? @else = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> DoWhile(
        Action<ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>> configure,
        Expression<Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, bool>> conditionExpression, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> DoWhile(
        Action<ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>> configure,
        Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, bool> condition, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> WhileDo(
        Expression<Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, bool>> conditionExpression,
        Action<ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> WhileDo(
        Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, bool> condition,
        Action<ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> ForEach<TItem>(
        Expression<Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, IEnumerable<TItem>?>> itemsExpression,
        Action<IFaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>, ItemScope<TItem>> body,
        Action<IForEachSetup<TWorkflowData>>? setup = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> ForEach<TItem>(
        Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, IEnumerable<TItem>?> items,
        Action<IFaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>, ItemScope<TItem>> body,
        Action<IForEachSetup<TWorkflowData>>? setup = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    IFaultTryBuilder<TWorkflowData, TFault, TPreviousStepData> Try(
        Action<ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    IFaultSagaContainerBuilder<TWorkflowData, TFault, TPreviousStepData> Saga(
        Action<IFaultSagaActivityBuilder<TWorkflowData, TFault, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

}
