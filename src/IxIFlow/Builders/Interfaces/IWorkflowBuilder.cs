using System.Runtime.CompilerServices;
using System.Linq.Expressions;
using IxIFlow.Core;

namespace IxIFlow.Builders.Interfaces;

/// <summary>
/// Root workflow builder for defining workflow steps
/// </summary>
/// <typeparam name="TWorkflowData">The workflow data type</typeparam>
public interface IWorkflowBuilder<TWorkflowData>
{
    /// <summary>Requires a capability; explicitly inherited constraints also apply to children.</summary>
    IWorkflowBuilder<TWorkflowData> Demand(string key, string? value = null, bool inherit = false);
    /// <summary>Resolves a capability value once from start data using a pure expression.</summary>
    IWorkflowBuilder<TWorkflowData> DemandFrom(string key,
        Expression<Func<WorkflowContext<TWorkflowData>, string?>> expression, bool inherit = false);

    /// <summary>Sets the last step's stable ID and source location.</summary>
    IWorkflowBuilder<TWorkflowData> WithId(string id, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    /// <summary>Names the preceding step.</summary>
    IWorkflowBuilder<TWorkflowData> Named(string name);

    /// <summary>Wait for a duration without requiring a resume event.</summary>
    IWorkflowBuilder<TWorkflowData> Delay(TimeSpan duration, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    /// <summary>
    /// Adds a step to the workflow that executes the specified activity
    /// </summary>
    /// <typeparam name="TActivity">The activity type to execute</typeparam>
    /// <param name="configure">Optional configuration for the activity</param>
    /// <returns>A workflow builder for chaining additional steps</returns>
    IWorkflowBuilder<TWorkflowData, TActivity> Step<TActivity>(
        Action<IActivitySetupBuilder<TWorkflowData, TActivity>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TActivity : class, IAsyncActivity;

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    IWorkflowBuilder<TWorkflowData, TActivity> Request<TActivity>(
        string name,
        Action<IActivitySetupBuilder<TWorkflowData, TActivity>>? configure = null,
        Expression<Func<WorkflowContext<TWorkflowData>, string>>? subjectExpression = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TActivity : class, IAsyncActivity;

    /// <summary>
    /// Runs a request activity (<see cref="IRequestActivity{TReply}"/>) whose answer the workflow takes
    /// with <c>WaitFor&lt;TReply&gt;(name)</c> later in the same body. The answer's wait opens
    /// before the activity runs, and the activity sends <see cref="IActivityContext.Reply"/> with the
    /// request. An answer that arrives before the <c>WaitFor</c> is kept for it. A
    /// <paramref name="subject"/> says what the request is about; an answer must carry the same one.
    /// </summary>
    IWorkflowBuilder<TWorkflowData, TActivity> Request<TActivity>(
        string name,
        Action<IActivitySetupBuilder<TWorkflowData, TActivity>>? configure = null,
        Func<WorkflowContext<TWorkflowData>, string>? subject = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
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
    /// <summary>Requires a capability; explicitly inherited constraints also apply to children.</summary>
    IWorkflowBuilder<TWorkflowData, TPreviousStepData> Demand(string key, string? value = null, bool inherit = false);
    /// <summary>Resolves a capability value once from start data using a pure expression.</summary>
    IWorkflowBuilder<TWorkflowData, TPreviousStepData> DemandFrom(string key,
        Expression<Func<WorkflowContext<TWorkflowData>, string?>> expression, bool inherit = false);

    /// <summary>Sets the last step's stable ID and source location.</summary>
    IWorkflowBuilder<TWorkflowData, TPreviousStepData> WithId(string id, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    /// <summary>Names the preceding step.</summary>
    IWorkflowBuilder<TWorkflowData, TPreviousStepData> Named(string name);

    /// <summary>Wait for a duration and retain the previous step value.</summary>
    IWorkflowBuilder<TWorkflowData, TPreviousStepData> Delay(TimeSpan duration, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    /// <summary>
    /// Adds a step to the workflow with access to previous step data
    /// </summary>
    /// <typeparam name="TActivity">The activity type to execute</typeparam>
    /// <param name="configure">Configuration for the activity</param>
    /// <returns>A workflow builder for chaining additional steps</returns>
    IWorkflowBuilder<TWorkflowData, TActivity> Step<TActivity>(
        Action<IActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TActivity : class, IAsyncActivity;

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    IWorkflowBuilder<TWorkflowData, TActivity> Request<TActivity>(
        string name,
        Action<IActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>>? configure = null,
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, string>>? subjectExpression = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TActivity : class, IAsyncActivity;

    /// <summary>
    /// Runs a request activity (<see cref="IRequestActivity{TReply}"/>) whose answer the workflow takes
    /// with <c>WaitFor&lt;TReply&gt;(name)</c> later in the same body. The answer's wait opens
    /// before the activity runs, and the activity sends <see cref="IActivityContext.Reply"/> with the
    /// request. An answer that arrives before the <c>WaitFor</c> is kept for it. A
    /// <paramref name="subject"/> says what the request is about; an answer must carry the same one.
    /// </summary>
    IWorkflowBuilder<TWorkflowData, TActivity> Request<TActivity>(
        string name,
        Action<IActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>>? configure = null,
        Func<WorkflowContext<TWorkflowData, TPreviousStepData>, string>? subject = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TActivity : class, IAsyncActivity;

    /// <summary>
    /// Invokes a registered workflow class using its declared WorkflowAttribute identity
    /// </summary>
    /// <typeparam name="TWorkflow">The workflow type to invoke</typeparam>
    /// <typeparam name="TInvokedData">The data type for the invoked workflow</typeparam>
    /// <param name="configure">Configuration for the workflow invocation</param>
    /// <returns>A workflow builder for chaining additional steps</returns>
    IWorkflowBuilder<TWorkflowData, TInvokedData> Invoke<TWorkflow, TInvokedData>(
        Action<IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
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
        Action<IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TInvokedData : class;

    /// <summary>
    /// Defines a sequence of workflow steps
    /// </summary>
    /// <param name="configure">Configuration for the sequence</param>
    /// <returns>A workflow builder for chaining additional steps</returns>
    IWorkflowBuilder<TWorkflowData, TPreviousStepData> Sequence(
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

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
        Action<IWorkflowBuilder<TWorkflowData, TPreviousStepData>>? @else = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    /// <summary>
    /// Executes workflow branches in parallel
    /// </summary>
    /// <param name="configure">Configuration for parallel execution</param>
    /// <returns>A workflow builder for chaining additional steps</returns>
    IWorkflowBuilder<TWorkflowData, TPreviousStepData> Parallel(
        Action<IParallelBuilder<TWorkflowData, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    IWorkflowBuilder<TWorkflowData, TPreviousStepData> DoWhile(
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure,
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, bool>> conditionExpression, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    /// <summary>
    /// Executes a loop with condition check after each iteration
    /// </summary>
    /// <param name="configure">Configuration for the loop body</param>
    /// <param name="condition">Condition to check after each iteration</param>
    /// <returns>A workflow builder for chaining additional steps</returns>
    IWorkflowBuilder<TWorkflowData, TPreviousStepData> DoWhile(
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure,
        Func<WorkflowContext<TWorkflowData, TPreviousStepData>, bool> condition, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    IWorkflowBuilder<TWorkflowData, TPreviousStepData> WhileDo(
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, bool>> conditionExpression,
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    /// <summary>
    /// Executes a loop with condition check before each iteration
    /// </summary>
    /// <param name="condition">Condition to check before each iteration</param>
    /// <param name="configure">Configuration for the loop body</param>
    /// <returns>A workflow builder for chaining additional steps</returns>
    IWorkflowBuilder<TWorkflowData, TPreviousStepData> WhileDo(
        Func<WorkflowContext<TWorkflowData, TPreviousStepData>, bool> condition,
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    /// <summary>
    /// Runs <paramref name="body"/> once per item of the list <paramref name="items"/> gives when the step
    /// starts, one item after another. The list is saved then, so later changes to the data add or
    /// remove no item. Each item sees the data changes of the items before it. The body reads the item
    /// with <c>ctx.Item(item)</c> and its position with <c>ctx.Index(item)</c>, and may end with
    /// <see cref="Result{TResult}"/>; <paramref name="setup"/> names the handle and says where the results go.
    /// Items are read-only: outputs in the body write workflow data, through targets that are chains of
    /// members, indexes, element selectors such as <c>First(...)</c> and built-in casts, such as
    /// <c>ctx.WorkflowData.Rows[ctx.Index(item)].Value</c>; any other target is a build error.
    /// </summary>
    /// <summary>Captures the item selector as an expression.</summary>
    [OverloadResolutionPriority(-1)]
    IWorkflowBuilder<TWorkflowData, TPreviousStepData> ForEach<TItem>(
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, IEnumerable<TItem>?>> itemsExpression,
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>, ItemScope<TItem>> body,
        Action<IForEachSetup<TWorkflowData>>? setup = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    /// <summary>Captures the result selector as an expression.</summary>
    [OverloadResolutionPriority(-1)]
    IWorkflowBuilder<TWorkflowData, TPreviousStepData> Result<TResult>(
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, TResult>> valueExpression, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    IWorkflowBuilder<TWorkflowData, TPreviousStepData> ForEach<TItem>(
        Func<WorkflowContext<TWorkflowData, TPreviousStepData>, IEnumerable<TItem>?> items,
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>, ItemScope<TItem>> body,
        Action<IForEachSetup<TWorkflowData>>? setup = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    /// <summary>
    /// Ends a <c>ForEach</c> body with the value it gives for its item, which the step's
    /// <see cref="IForEachSetup{TWorkflowData}.Into{TResult}"/> stores. A result can only be the last step
    /// of a <c>ForEach</c> body, or of a timeout branch that leaves the body: the branch of a wait directly
    /// in the body, or directly in such a branch.
    /// </summary>
    IWorkflowBuilder<TWorkflowData, TPreviousStepData> Result<TResult>(
        Func<WorkflowContext<TWorkflowData, TPreviousStepData>, TResult> value, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    /// <summary>
    /// Defines a try-catch block for error handling
    /// </summary>
    /// <param name="configure">Configuration for the try block</param>
    /// <returns>A workflow builder for chaining additional steps</returns>
    ITryBuilder<TWorkflowData, TPreviousStepData> Try(
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    /// <summary>
    /// Defines a saga for distributed transactions
    /// </summary>
    /// <param name="configure">Configuration for the saga</param>
    /// <returns>A workflow builder for chaining additional steps</returns>
    ISagaContainerBuilder<TWorkflowData, TPreviousStepData> Saga(
        Action<ISagaActivityBuilder<TWorkflowData, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    IWorkflowBuilder<TWorkflowData, TResumeEvent> Suspend<TResumeEvent>(
        string suspendReason,
        Expression<Func<TResumeEvent, WorkflowContext<TWorkflowData>, bool>>? resumeExpression = null,
        Action<ISuspendSetupBuilder<TWorkflowData, TResumeEvent, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TResumeEvent : class;

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
        Action<ISuspendSetupBuilder<TWorkflowData, TResumeEvent, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TResumeEvent : class;

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    IWorkflowBuilder<TWorkflowData, TEvent> WaitFor<TEvent>(
        string key,
        Expression<Func<TEvent, WorkflowContext<TWorkflowData>, bool>>? matchesExpression = null,
        Action<ISuspendSetupBuilder<TWorkflowData, TEvent, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TEvent : class;

    /// <summary>
    /// Registers a wait for a correlated event and continues with that event as the previous step.
    /// </summary>
    IWorkflowBuilder<TWorkflowData, TEvent> WaitFor<TEvent>(
        string key,
        Func<TEvent, WorkflowContext<TWorkflowData>, bool>? matches = null,
        Action<ISuspendSetupBuilder<TWorkflowData, TEvent, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TEvent : class;

    /// <summary>
    /// Builds the workflow definition
    /// </summary>
    /// <returns>The constructed workflow definition</returns>
    WorkflowDefinition Build();

}
