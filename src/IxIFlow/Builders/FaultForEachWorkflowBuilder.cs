using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using IxIFlow.Core;
using IxIFlow.Builders.Interfaces;

namespace IxIFlow.Builders;

internal sealed class FaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>(List<WorkflowStep> steps, Type exceptionType)
    : CatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>(steps, exceptionType), IFaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>
    where TWorkflowData : class where TPreviousStepData : class
{
    public new IFaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> Named(string name)
    {
        base.Named(name);
        return new FaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>(Steps, ExceptionType);
    }
    public new IFaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> Delay(TimeSpan duration,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        base.Delay(duration, sourcePath, sourceLine);
        return new FaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>(Steps, ExceptionType);
    }
    public new IFaultForEachWorkflowBuilder<TWorkflowData, TFault, TActivity> Request<TActivity>(string name,
        Action<ICatchActivitySetupBuilder<TWorkflowData, TActivity, TFault, TPreviousStepData>>? configure = null,
        Expression<Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, string>>? subjectExpression = null,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TActivity : class, IAsyncActivity
    {
        base.Request<TActivity>(name, configure, subjectExpression, sourcePath, sourceLine);
        return new FaultForEachWorkflowBuilder<TWorkflowData, TFault, TActivity>(Steps, ExceptionType);
    }
    /// <summary>Assigns one serialized workflow-data location, retaining the previous value.</summary>
    public new IFaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> Assign<TValue>(
        Expression<Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, TValue>> target,
        Expression<Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, TValue>> value,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        base.Assign(target, value, sourcePath, sourceLine);
        return this;
    }

    public new IFaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> Switch<TValue>(
        Expression<Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, TValue>> selector,
        Action<SwitchCases<TValue, ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        base.Switch<TValue>(selector, configure, sourcePath, sourceLine);
        return new FaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>(Steps, ExceptionType);
    }
    public new IFaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> WithId(string id, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        base.WithId(id, sourcePath, sourceLine);
        return new FaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>(Steps, ExceptionType);
    }
    public new IFaultForEachWorkflowBuilder<TWorkflowData, TFault, TActivity> Step<TActivity>(
        Action<ICatchActivitySetupBuilder<TWorkflowData, TActivity, TFault, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TActivity : class, IAsyncActivity
    {
        base.Step<TActivity>(configure, sourcePath, sourceLine);
        return new FaultForEachWorkflowBuilder<TWorkflowData, TFault, TActivity>(Steps, ExceptionType);
    }
    [OverloadResolutionPriority(-1)]
    public new IFaultForEachWorkflowBuilder<TWorkflowData, TFault, TEvent> WaitFor<TEvent>(
        string key,
        Expression<Func<TEvent, FaultContext<TWorkflowData, TFault, TPreviousStepData>, bool>>? matchesExpression = null,
        Action<IFaultSuspendSetupBuilder<TWorkflowData, TFault, TEvent, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TEvent : class
    {
        base.WaitFor<TEvent>(key, matchesExpression, configure, sourcePath, sourceLine);
        return new FaultForEachWorkflowBuilder<TWorkflowData, TFault, TEvent>(Steps, ExceptionType);
    }
    public new IFaultForEachWorkflowBuilder<TWorkflowData, TFault, TEvent> WaitFor<TEvent>(
        string key,
        Func<TEvent, FaultContext<TWorkflowData, TFault, TPreviousStepData>, bool>? matches = null,
        Action<IFaultSuspendSetupBuilder<TWorkflowData, TFault, TEvent, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TEvent : class
    {
        base.WaitFor<TEvent>(key, matches, configure, sourcePath, sourceLine);
        return new FaultForEachWorkflowBuilder<TWorkflowData, TFault, TEvent>(Steps, ExceptionType);
    }
    [OverloadResolutionPriority(-1)]
    public new IFaultForEachWorkflowBuilder<TWorkflowData, TFault, TEvent> Suspend<TEvent>(
        string reason,
        Expression<Func<TEvent, FaultContext<TWorkflowData, TFault, TPreviousStepData>, bool>>? matchesExpression = null,
        Action<IFaultSuspendSetupBuilder<TWorkflowData, TFault, TEvent, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TEvent : class
    {
        base.Suspend<TEvent>(reason, matchesExpression, configure, sourcePath, sourceLine);
        return new FaultForEachWorkflowBuilder<TWorkflowData, TFault, TEvent>(Steps, ExceptionType);
    }
    public new IFaultForEachWorkflowBuilder<TWorkflowData, TFault, TEvent> Suspend<TEvent>(
        string reason,
        Func<TEvent, FaultContext<TWorkflowData, TFault, TPreviousStepData>, bool>? matches = null,
        Action<IFaultSuspendSetupBuilder<TWorkflowData, TFault, TEvent, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TEvent : class
    {
        base.Suspend<TEvent>(reason, matches, configure, sourcePath, sourceLine);
        return new FaultForEachWorkflowBuilder<TWorkflowData, TFault, TEvent>(Steps, ExceptionType);
    }
    public new IFaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> Parallel(
        Action<IFaultParallelBuilder<TWorkflowData, TFault, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        base.Parallel(configure, sourcePath, sourceLine);
        return new FaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>(Steps, ExceptionType);
    }
    public new IFaultForEachWorkflowBuilder<TWorkflowData, TFault, TUsedData> Use<TWorkflow, TUsedData>(
        Action<IWorkflowInlineSetupBuilder<TWorkflowData, TUsedData, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TWorkflow : IWorkflow<TUsedData>, new()
        where TUsedData : class
    {
        base.Use<TWorkflow, TUsedData>(configure, sourcePath, sourceLine);
        return new FaultForEachWorkflowBuilder<TWorkflowData, TFault, TUsedData>(Steps, ExceptionType);
    }
    public new IFaultForEachWorkflowBuilder<TWorkflowData, TFault, TUsedData> Use<TUsedData>(
        Func<WorkflowDefinition> callee, WorkflowInlinePin pin,
        Action<IWorkflowInlineSetupBuilder<TWorkflowData, TUsedData, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TUsedData : class
    {
        base.Use<TUsedData>(callee, pin, configure, sourcePath, sourceLine);
        return new FaultForEachWorkflowBuilder<TWorkflowData, TFault, TUsedData>(Steps, ExceptionType);
    }
    public new IFaultForEachWorkflowBuilder<TWorkflowData, TFault, TInvokedData> Invoke<TInvokedData>(
        string workflowName, int version, WorkflowContract contract,
        Action<FaultInvocationSetupBuilder<TWorkflowData, TFault, TInvokedData, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TInvokedData : class
    {
        base.Invoke<TInvokedData>(workflowName, version, contract, configure, sourcePath, sourceLine);
        return new FaultForEachWorkflowBuilder<TWorkflowData, TFault, TInvokedData>(Steps, ExceptionType);
    }
    public new IFaultForEachWorkflowBuilder<TWorkflowData, TFault, TInvokedData> Invoke<TWorkflow, TInvokedData>(
        Action<FaultInvocationSetupBuilder<TWorkflowData, TFault, TInvokedData, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TWorkflow : IWorkflow<TInvokedData>
        where TInvokedData : class
    {
        base.Invoke<TWorkflow, TInvokedData>(configure, sourcePath, sourceLine);
        return new FaultForEachWorkflowBuilder<TWorkflowData, TFault, TInvokedData>(Steps, ExceptionType);
    }
    public new IFaultForEachWorkflowBuilder<TWorkflowData, TFault, TInvokedData> Invoke<TInvokedData>(
        string workflowName, int version,
        Action<FaultInvocationSetupBuilder<TWorkflowData, TFault, TInvokedData, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TInvokedData : class
    {
        base.Invoke<TInvokedData>(workflowName, version, configure, sourcePath, sourceLine);
        return new FaultForEachWorkflowBuilder<TWorkflowData, TFault, TInvokedData>(Steps, ExceptionType);
    }
    public new IFaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> Sequence(
        Action<ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        base.Sequence(configure, sourcePath, sourceLine);
        return new FaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>(Steps, ExceptionType);
    }
    public new IFaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> Flowchart(
        Action<FaultFlowchartBuilder<TWorkflowData, TFault, TPreviousStepData>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        base.Flowchart(configure, sourcePath, sourceLine);
        return new FaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>(Steps, ExceptionType);
    }
    public new IFaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> StateMachine(
        Action<FaultStateMachineBuilder<TWorkflowData, TFault, TPreviousStepData>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        base.StateMachine(configure, sourcePath, sourceLine);
        return new FaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>(Steps, ExceptionType);
    }
    public new IFaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> If(
        Expression<Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, bool>> condition,
        Action<ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>> then,
        Action<ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>>? @else = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        base.If(condition, then, @else, sourcePath, sourceLine);
        return new FaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>(Steps, ExceptionType);
    }
    [OverloadResolutionPriority(-1)]
    public new IFaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> DoWhile(
        Action<ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>> configure,
        Expression<Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, bool>> conditionExpression, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        base.DoWhile(configure, conditionExpression, sourcePath, sourceLine);
        return new FaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>(Steps, ExceptionType);
    }
    public new IFaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> DoWhile(
        Action<ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>> configure,
        Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, bool> condition, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        base.DoWhile(configure, condition, sourcePath, sourceLine);
        return new FaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>(Steps, ExceptionType);
    }
    [OverloadResolutionPriority(-1)]
    public new IFaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> WhileDo(
        Expression<Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, bool>> conditionExpression,
        Action<ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        base.WhileDo(conditionExpression, configure, sourcePath, sourceLine);
        return new FaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>(Steps, ExceptionType);
    }
    public new IFaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> WhileDo(
        Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, bool> condition,
        Action<ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        base.WhileDo(condition, configure, sourcePath, sourceLine);
        return new FaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>(Steps, ExceptionType);
    }
    [OverloadResolutionPriority(-1)]
    public new IFaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> ForEach<TItem>(
        Expression<Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, IEnumerable<TItem>?>> itemsExpression,
        Action<IFaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>, ItemScope<TItem>> body,
        Action<IForEachSetup<TWorkflowData>>? setup = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        base.ForEach<TItem>(itemsExpression, body, setup, sourcePath, sourceLine);
        return new FaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>(Steps, ExceptionType);
    }
    public new IFaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> ForEach<TItem>(
        Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, IEnumerable<TItem>?> items,
        Action<IFaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>, ItemScope<TItem>> body,
        Action<IForEachSetup<TWorkflowData>>? setup = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        base.ForEach<TItem>(items, body, setup, sourcePath, sourceLine);
        return new FaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>(Steps, ExceptionType);
    }
    public IFaultForEachWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData> Result<TResult>(Expression<Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, TResult>> valueExpression,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        AppendResult(valueExpression, sourcePath, sourceLine);
        return this;
    }
}
