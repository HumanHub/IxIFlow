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
        Expression<Func<TEvent, WorkflowContext<TWorkflowData>, bool>>? matchesExpression = null,
        Action<ISuspendSetupBuilder<TWorkflowData, TEvent, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TEvent : class;

    ICatchWorkflowBuilder<TWorkflowData, TFault, TEvent> WaitFor<TEvent>(
        string key,
        Func<TEvent, WorkflowContext<TWorkflowData>, bool>? matches = null,
        Action<ISuspendSetupBuilder<TWorkflowData, TEvent, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TEvent : class;

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    ICatchWorkflowBuilder<TWorkflowData, TFault, TEvent> Suspend<TEvent>(
        string reason,
        Expression<Func<TEvent, WorkflowContext<TWorkflowData>, bool>>? matchesExpression = null,
        Action<ISuspendSetupBuilder<TWorkflowData, TEvent, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TEvent : class;

    ICatchWorkflowBuilder<TWorkflowData, TFault, TEvent> Suspend<TEvent>(
        string reason,
        Func<TEvent, WorkflowContext<TWorkflowData>, bool>? matches = null,
        Action<ISuspendSetupBuilder<TWorkflowData, TEvent, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TEvent : class;

    ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> Parallel(
        Action<IParallelBuilder<TWorkflowData, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    ICatchWorkflowBuilder<TWorkflowData, TFault, TInvokedData> Invoke<TWorkflow, TInvokedData>(
        Action<IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TWorkflow : IWorkflow<TInvokedData>
        where TInvokedData : class;

    ICatchWorkflowBuilder<TWorkflowData, TFault, TInvokedData> Invoke<TInvokedData>(
        string workflowName, int version,
        Action<IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TInvokedData : class;

    /// <summary>Invokes a workflow using an explicit pinned input and output contract.</summary>
    ICatchWorkflowBuilder<TWorkflowData, TFault, TInvokedData> Invoke<TInvokedData>(
        string workflowName, int version, WorkflowContract contract,
        Action<IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TInvokedData : class;

    ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> Sequence(
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> If(
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, bool>> condition,
        Action<IWorkflowBuilder<TWorkflowData, TPreviousStepData>> then,
        Action<IWorkflowBuilder<TWorkflowData, TPreviousStepData>>? @else = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> DoWhile(
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure,
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, bool>> conditionExpression, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> DoWhile(
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure,
        Func<WorkflowContext<TWorkflowData, TPreviousStepData>, bool> condition, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> WhileDo(
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, bool>> conditionExpression,
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> WhileDo(
        Func<WorkflowContext<TWorkflowData, TPreviousStepData>, bool> condition,
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> ForEach<TItem>(
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, IEnumerable<TItem>?>> itemsExpression,
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>, ItemScope<TItem>> body,
        Action<IForEachSetup<TWorkflowData>>? setup = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> ForEach<TItem>(
        Func<WorkflowContext<TWorkflowData, TPreviousStepData>, IEnumerable<TItem>?> items,
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>, ItemScope<TItem>> body,
        Action<IForEachSetup<TWorkflowData>>? setup = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    ITryBuilder<TWorkflowData, TPreviousStepData> Try(
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    ISagaContainerBuilder<TWorkflowData, TPreviousStepData> Saga(
        Action<ISagaActivityBuilder<TWorkflowData, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

}
