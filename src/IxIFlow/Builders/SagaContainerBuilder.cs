using IxIFlow.Core.Runtime;
using System.Runtime.CompilerServices;
using System.Linq.Expressions;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;

namespace IxIFlow.Builders;

/// <summary>
///     Saga container builder implementation that properly delegates to workflow builder
/// </summary>
public class SagaContainerBuilder<TWorkflowData, TPreviousStepData> : ISagaContainerBuilder<TWorkflowData, TPreviousStepData>
    where TWorkflowData : class
    where TPreviousStepData : class
{
    private readonly WorkflowBuilder<TWorkflowData, TPreviousStepData> _workflowBuilder;

    public SagaContainerBuilder(List<WorkflowStep> steps, string workflowName, int workflowVersion, List<WorkflowDemand>? demands = null)
    {
        _workflowBuilder = new WorkflowBuilder<TWorkflowData, TPreviousStepData>(steps, workflowName, workflowVersion, demands);
    }

    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> Demand(string key, string? value = null, bool inherit = false) =>
        _workflowBuilder.Demand(key, value, inherit);
    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> DemandFrom(string key,
        Expression<Func<WorkflowContext<TWorkflowData>, string?>> expression, bool inherit = false) =>
        _workflowBuilder.DemandFrom(key, expression, inherit);

    // ISagaContainerBuilder specific methods
    public ISagaContainerBuilder<TWorkflowData, TPreviousStepData> OnError<TException>(
        Action<ISagaErrorBuilder<TWorkflowData, EmptyFault, TPreviousStepData>> configure,
        string? id = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TException : Exception
    {
        return _workflowBuilder.OnError<TException>(configure, id, sourcePath, sourceLine);
    }

    public ISagaContainerBuilder<TWorkflowData, TPreviousStepData> OnError<TException, TFault>(
        Action<ISagaErrorBuilder<TWorkflowData, TFault, TPreviousStepData>> configure,
        string? id = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TException : Exception => _workflowBuilder.OnError<TException, TFault>(configure, id, sourcePath, sourceLine);

    public ISagaContainerBuilder<TWorkflowData, TPreviousStepData> OnError(
        Action<ISagaErrorBuilder<TWorkflowData, EmptyFault, TPreviousStepData>> configure,
        string? id = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        return _workflowBuilder.OnError(configure, id, sourcePath, sourceLine);
    }

    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> WithId(string id, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) =>
        _workflowBuilder.WithId(id, sourcePath, sourceLine);

    // Delegate all IWorkflowBuilder methods to the real workflow builder
    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> Named(string name) => _workflowBuilder.Named(name);

    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> Delay(TimeSpan duration) =>
        _workflowBuilder.Delay(duration);

    public IWorkflowBuilder<TWorkflowData, TActivity> Step<TActivity>(
        Action<IActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>>? configure = null)
        where TActivity : class, IAsyncActivity
    {
        return _workflowBuilder.Step(configure);
    }

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    public IWorkflowBuilder<TWorkflowData, TActivity> Request<TActivity>(
        string name,
        Action<IActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>>? configure = null,
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, string>>? subjectExpression = null)
        where TActivity : class, IAsyncActivity =>
        _workflowBuilder.Request<TActivity>(name, configure, subjectExpression: subjectExpression);

    public IWorkflowBuilder<TWorkflowData, TActivity> Request<TActivity>(string name,
        Action<IActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>>? configure = null,
        Func<WorkflowContext<TWorkflowData, TPreviousStepData>, string>? subject = null)
        where TActivity : class, IAsyncActivity =>
        _workflowBuilder.Request(name, configure, subject);

    public IWorkflowBuilder<TWorkflowData, TInvokedData> Invoke<TWorkflow, TInvokedData>(
        Action<IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>>? configure = null)
        where TWorkflow : IWorkflow<TInvokedData> where TInvokedData : class
    {
        return _workflowBuilder.Invoke<TWorkflow, TInvokedData>(configure);
    }

    public IWorkflowBuilder<TWorkflowData, TInvokedData> Invoke<TInvokedData>(string workflowName, int version,
        Action<IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>>? configure = null)
        where TInvokedData : class
    {
        return _workflowBuilder.Invoke(workflowName, version, configure);
    }

    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> Sequence(
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure)
    {
        return _workflowBuilder.Sequence(configure);
    }

    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> If(
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, bool>> condition,
        Action<IWorkflowBuilder<TWorkflowData, TPreviousStepData>> then,
        Action<IWorkflowBuilder<TWorkflowData, TPreviousStepData>>? @else = null)
    {
        return _workflowBuilder.If(condition, then, @else);
    }

    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> Parallel(
        Action<IParallelBuilder<TWorkflowData, TPreviousStepData>> configure)
    {
        return _workflowBuilder.Parallel(configure);
    }

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> DoWhile(
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure,
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, bool>> conditionExpression) =>
        _workflowBuilder.DoWhile(configure, conditionExpression: conditionExpression);

    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> DoWhile(
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure,
        Func<WorkflowContext<TWorkflowData, TPreviousStepData>, bool> condition)
    {
        return _workflowBuilder.DoWhile(configure, condition);
    }

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> WhileDo(
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, bool>> conditionExpression,
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure) =>
        _workflowBuilder.WhileDo(conditionExpression: conditionExpression, configure: configure);

    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> WhileDo(
        Func<WorkflowContext<TWorkflowData, TPreviousStepData>, bool> condition,
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure)
    {
        return _workflowBuilder.WhileDo(condition, configure);
    }

    [OverloadResolutionPriority(-1)]
    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> ForEach<TItem>(
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, IEnumerable<TItem>?>> itemsExpression,
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>, ItemScope<TItem>> body,
        Action<IForEachSetup<TWorkflowData>>? setup = null) => _workflowBuilder.ForEach(itemsExpression, body, setup);

    [OverloadResolutionPriority(-1)]
    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> Result<TResult>(
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, TResult>> valueExpression) => _workflowBuilder.Result(valueExpression);

    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> ForEach<TItem>(
        Func<WorkflowContext<TWorkflowData, TPreviousStepData>, IEnumerable<TItem>?> items,
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>, ItemScope<TItem>> body,
        Action<IForEachSetup<TWorkflowData>>? setup = null) =>
        _workflowBuilder.ForEach(items, body, setup);

    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> Result<TResult>(
        Func<WorkflowContext<TWorkflowData, TPreviousStepData>, TResult> value) =>
        _workflowBuilder.Result(value);

    public ITryBuilder<TWorkflowData, TPreviousStepData> Try(
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure)
    {
        return _workflowBuilder.Try(configure);
    }

    public ISagaContainerBuilder<TWorkflowData, TPreviousStepData> Saga(
        Action<ISagaActivityBuilder<TWorkflowData, TPreviousStepData>> configure)
    {
        return _workflowBuilder.Saga(configure);
    }

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    public IWorkflowBuilder<TWorkflowData, TResumeEvent> Suspend<TResumeEvent>(
        string suspendReason,
        Expression<Func<TResumeEvent, WorkflowContext<TWorkflowData>, bool>>? resumeExpression = null,
        Action<ISuspendSetupBuilder<TWorkflowData, TResumeEvent, TPreviousStepData>>? configure = null)
        where TResumeEvent : class =>
        _workflowBuilder.Suspend<TResumeEvent>(suspendReason, resumeExpression: resumeExpression, configure: configure);

    public IWorkflowBuilder<TWorkflowData, TResumeEvent> Suspend<TResumeEvent>(string suspendReason,
        Func<TResumeEvent, WorkflowContext<TWorkflowData>, bool>? resumeCondition = null,
        Action<ISuspendSetupBuilder<TWorkflowData, TResumeEvent, TPreviousStepData>>? configure = null)
        where TResumeEvent : class
    {
        return _workflowBuilder.Suspend(suspendReason, resumeCondition, configure);
    }

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    public IWorkflowBuilder<TWorkflowData, TEvent> WaitFor<TEvent>(
        string key,
        Expression<Func<TEvent, WorkflowContext<TWorkflowData>, bool>>? matchesExpression = null,
        Action<ISuspendSetupBuilder<TWorkflowData, TEvent, TPreviousStepData>>? configure = null)
        where TEvent : class =>
        _workflowBuilder.WaitFor<TEvent>(key, matchesExpression: matchesExpression, configure: configure);

    public IWorkflowBuilder<TWorkflowData, TEvent> WaitFor<TEvent>(string key,
        Func<TEvent, WorkflowContext<TWorkflowData>, bool>? matches = null,
        Action<ISuspendSetupBuilder<TWorkflowData, TEvent, TPreviousStepData>>? configure = null)
        where TEvent : class
    {
        return _workflowBuilder.WaitFor(key, matches, configure);
    }

    public WorkflowDefinition Build()
    {
        return _workflowBuilder.Build();
    }

}
