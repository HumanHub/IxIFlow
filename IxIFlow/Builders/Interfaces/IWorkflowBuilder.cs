using System.Linq.Expressions;
using IxIFlow.Core;

namespace IxIFlow.Builders.Interfaces;

/// <summary>
/// Root workflow builder for defining workflow steps
/// </summary>
/// <typeparam name="TWorkflowData">The workflow data type</typeparam>
public interface IWorkflowBuilder<TWorkflowData>
{
    /// <summary>
    /// Adds a step to the workflow that executes the specified activity
    /// </summary>
    /// <typeparam name="TActivity">The activity type to execute</typeparam>
    /// <param name="configure">Optional configuration for the activity</param>
    /// <returns>A workflow builder for chaining additional steps</returns>
    IWorkflowBuilder<TWorkflowData, TActivity> Step<TActivity>(
        Action<IActivitySetupBuilder<TWorkflowData, TActivity>>? configure = null)
        where TActivity : class, IAsyncActivity;

    /// <summary>
    /// Builds and returns the workflow definition
    /// </summary>
    /// <returns>The constructed workflow definition</returns>
    WorkflowDefinition Build();
}

/// <summary>
/// Builder for workflow steps that have a previous step, providing access to the previous step's data
/// </summary>
/// <typeparam name="TWorkflowData">The workflow data type</typeparam>
/// <typeparam name="TPreviousStepData">The previous step's data type</typeparam>
public interface IWorkflowBuilder<TWorkflowData, TPreviousStepData> 
    where TPreviousStepData : class
{
    /// <summary>
    /// Adds a step to the workflow with access to previous step data
    /// </summary>
    /// <typeparam name="TActivity">The activity type to execute</typeparam>
    /// <param name="configure">Configuration for the activity</param>
    /// <returns>A workflow builder for chaining additional steps</returns>
    IWorkflowBuilder<TWorkflowData, TActivity> Step<TActivity>(
        Action<IActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>> configure)
        where TActivity : class, IAsyncActivity;

    /// <summary>
    /// Invokes a workflow by its type
    /// </summary>
    /// <typeparam name="TWorkflow">The workflow type to invoke</typeparam>
    /// <typeparam name="TInvokedData">The data type for the invoked workflow</typeparam>
    /// <param name="configure">Configuration for the workflow invocation</param>
    /// <returns>A workflow builder for chaining additional steps</returns>
    IWorkflowBuilder<TWorkflowData, TInvokedData> Invoke<TWorkflow, TInvokedData>(
        Action<IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>> configure)
        where TWorkflow : IWorkflow<TInvokedData> 
        where TInvokedData : class;

    /// <summary>
    /// Invokes a workflow by name and version
    /// </summary>
    /// <typeparam name="TInvokedData">The data type for the invoked workflow</typeparam>
    /// <param name="workflowName">Name of the workflow to invoke</param>
    /// <param name="version">Version of the workflow to invoke</param>
    /// <param name="configure">Configuration for the workflow invocation</param>
    /// <returns>A workflow builder for chaining additional steps</returns>
    IWorkflowBuilder<TWorkflowData, TInvokedData> Invoke<TInvokedData>(string workflowName, int version,
        Action<IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>> configure)
        where TInvokedData : class;

    /// <summary>
    /// Defines a sequence of workflow steps
    /// </summary>
    /// <param name="configure">Configuration for the sequence</param>
    /// <returns>A workflow builder for chaining additional steps</returns>
    IWorkflowBuilder<TWorkflowData, TPreviousStepData> Sequence(
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure);

    /// <summary>
    /// Creates a conditional branch in the workflow
    /// </summary>
    /// <param name="condition">Condition expression</param>
    /// <param name="then">Action to execute if condition is true</param>
    /// <param name="else">Optional action to execute if condition is false</param>
    /// <returns>A workflow builder for chaining additional steps</returns>
    IWorkflowBuilder<TWorkflowData, TPreviousStepData> If(
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, bool>> condition,
        Action<IWorkflowBuilder<TWorkflowData, TPreviousStepData>> then,
        Action<IWorkflowBuilder<TWorkflowData, TPreviousStepData>>? @else = null);

    /// <summary>
    /// Executes workflow branches in parallel
    /// </summary>
    /// <param name="configure">Configuration for parallel execution</param>
    /// <returns>A workflow builder for chaining additional steps</returns>
    IWorkflowBuilder<TWorkflowData, TPreviousStepData> Parallel(
        Action<IParallelBuilder<TWorkflowData, TPreviousStepData>> configure);

    /// <summary>
    /// Executes a loop with condition check after each iteration
    /// </summary>
    /// <param name="configure">Configuration for the loop body</param>
    /// <param name="condition">Condition to check after each iteration</param>
    /// <returns>A workflow builder for chaining additional steps</returns>
    IWorkflowBuilder<TWorkflowData, TPreviousStepData> DoWhile(
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure,
        Func<WorkflowContext<TWorkflowData, TPreviousStepData>, bool> condition);

    /// <summary>
    /// Executes a loop with condition check before each iteration
    /// </summary>
    /// <param name="condition">Condition to check before each iteration</param>
    /// <param name="configure">Configuration for the loop body</param>
    /// <returns>A workflow builder for chaining additional steps</returns>
    IWorkflowBuilder<TWorkflowData, TPreviousStepData> WhileDo(
        Func<WorkflowContext<TWorkflowData, TPreviousStepData>, bool> condition,
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure);

    /// <summary>
    /// Defines a try-catch block for error handling
    /// </summary>
    /// <param name="configure">Configuration for the try block</param>
    /// <returns>A workflow builder for chaining additional steps</returns>
    ITryBuilder<TWorkflowData, TPreviousStepData> Try(
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure);

    /// <summary>
    /// Defines a saga for distributed transactions
    /// </summary>
    /// <param name="configure">Configuration for the saga</param>
    /// <returns>A workflow builder for chaining additional steps</returns>
    ISagaContainerBuilder<TWorkflowData, TPreviousStepData> Saga(
        Action<ISagaActivityBuilder<TWorkflowData, TPreviousStepData>> configure);

    /// <summary>
    /// Suspends workflow execution until a resume event is received
    /// </summary>
    /// <typeparam name="TResumeEvent">Type of the resume event</typeparam>
    /// <param name="suspendReason">Reason for suspension</param>
    /// <param name="resumeCondition">Optional condition to resume execution</param>
    /// <param name="configure">Configuration for suspension setup</param>
    /// <returns>A workflow builder for chaining additional steps</returns>
    IWorkflowBuilder<TWorkflowData, TResumeEvent> Suspend<TResumeEvent>(
        string suspendReason,
        Func<TResumeEvent, WorkflowContext<TWorkflowData>, bool>? resumeCondition = null,
        Action<ISuspendSetupBuilder<TWorkflowData, TResumeEvent, TPreviousStepData>>? configure = null)
        where TResumeEvent : class;

    /// <summary>
    /// Builds the workflow definition
    /// </summary>
    /// <returns>The constructed workflow definition</returns>
    WorkflowDefinition Build();
}
