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

    public SagaContainerBuilder(List<WorkflowStep> steps, string workflowName, int workflowVersion)
    {
        _workflowBuilder = new WorkflowBuilder<TWorkflowData, TPreviousStepData>(steps, workflowName, workflowVersion);
    }

    // ISagaContainerBuilder specific methods
    public ISagaContainerBuilder<TWorkflowData, TPreviousStepData> OnError<TException>(
        Action<ISagaErrorBuilder<TWorkflowData, EmptyFault, TPreviousStepData>> configure)
        where TException : Exception
    {
        return _workflowBuilder.OnError<TException>(configure);
    }

    public ISagaContainerBuilder<TWorkflowData, TPreviousStepData> OnError<TException, TFault>(
        Action<ISagaErrorBuilder<TWorkflowData, TFault, TPreviousStepData>> configure)
        where TException : Exception => _workflowBuilder.OnError<TException, TFault>(configure);

    public ISagaContainerBuilder<TWorkflowData, TPreviousStepData> OnError(
        Action<ISagaErrorBuilder<TWorkflowData, EmptyFault, TPreviousStepData>> configure)
    {
        return _workflowBuilder.OnError(configure);
    }

    // Delegate all IWorkflowBuilder methods to the real workflow builder
    public IWorkflowBuilder<TWorkflowData, TActivity> Step<TActivity>(
        Action<IActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>> configure)
        where TActivity : class, IAsyncActivity
    {
        return _workflowBuilder.Step(configure);
    }

    public IWorkflowBuilder<TWorkflowData, TInvokedData> Invoke<TWorkflow, TInvokedData>(
        Action<IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>> configure)
        where TWorkflow : IWorkflow<TInvokedData> where TInvokedData : class
    {
        return _workflowBuilder.Invoke<TWorkflow, TInvokedData>(configure);
    }

    public IWorkflowBuilder<TWorkflowData, TInvokedData> Invoke<TInvokedData>(string workflowName, int version,
        Action<IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>> configure)
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

    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> DoWhile(
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure,
        Func<WorkflowContext<TWorkflowData, TPreviousStepData>, bool> condition)
    {
        return _workflowBuilder.DoWhile(configure, condition);
    }

    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> WhileDo(
        Func<WorkflowContext<TWorkflowData, TPreviousStepData>, bool> condition,
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure)
    {
        return _workflowBuilder.WhileDo(condition, configure);
    }

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

    public IWorkflowBuilder<TWorkflowData, TResumeEvent> Suspend<TResumeEvent>(string suspendReason,
        Func<TResumeEvent, WorkflowContext<TWorkflowData>, bool>? resumeCondition = null,
        Action<ISuspendSetupBuilder<TWorkflowData, TResumeEvent, TPreviousStepData>>? configure = null)
        where TResumeEvent : class
    {
        return _workflowBuilder.Suspend(suspendReason, resumeCondition, configure);
    }

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
