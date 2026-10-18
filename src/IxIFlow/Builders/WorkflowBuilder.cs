using IxIFlow.Core.Runtime;
using System.Runtime.CompilerServices;
using System.Linq.Expressions;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;

namespace IxIFlow.Builders;

/// <summary>
///     Concrete implementation of workflow builder for root-level workflow construction
/// </summary>
public class WorkflowBuilder<TWorkflowData>(string workflowName = "", int workflowVersion = 1)
    : IWorkflowBuilder<TWorkflowData>
    where TWorkflowData : class
{
    private readonly List<WorkflowStep> _steps = new();

    public IWorkflowBuilder<TWorkflowData> WithId(string id, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        Core.Runtime.WorkflowStepIdentity.Assign(_steps, id, sourcePath, sourceLine);
        return this;
    }

    public IWorkflowBuilder<TWorkflowData> Named(string name)
    {
        Core.Runtime.WorkflowStepIdentity.Name(_steps, name);
        return this;
    }

    public IWorkflowBuilder<TWorkflowData> Delay(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        _steps.Add(new WorkflowStep
        {
            Runtime = Core.Runtime.WorkflowStepDelegates.Create<TWorkflowData>(),
            Name = "Delay",
            StepType = WorkflowStepType.Delay,
            WorkflowDataType = typeof(TWorkflowData),
            DelayDuration = duration,
            Order = _steps.Count
        });
        return this;
    }

    /// <summary>
    ///     Builds the final workflow definition
    /// </summary>
    public WorkflowDefinition Build()
    {
        var definition = new WorkflowDefinition
        {
            Id = Guid.NewGuid().ToString(),
            Name = workflowName,
            Version = workflowVersion,
            WorkflowDataType = typeof(TWorkflowData),
            CreatedAt = DateTime.UtcNow,
            EstimatedStepCount = _steps.Count,
            Steps = _steps
        };
        WorkflowDefinitionValidator.Apply(definition);
        return definition;
    }

    public IWorkflowBuilder<TWorkflowData, TActivity> Step<TActivity>(
        Action<IActivitySetupBuilder<TWorkflowData, TActivity>>? configure = null)
        where TActivity : class, IAsyncActivity
    {
        var step = new WorkflowStep
        {
            Runtime = Core.Runtime.WorkflowStepDelegates.Create<TWorkflowData>(),
            Id = Guid.NewGuid().ToString(),
            Name = typeof(TActivity).Name,
            StepType = WorkflowStepType.Activity,
            ActivityType = typeof(TActivity),
            WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = null, // First step has no previous step
            Order = _steps.Count
        };

        // Create setup builder and configure it
        var setupBuilder = new ActivitySetupBuilder<TWorkflowData, TActivity>(step);
        configure?.Invoke(setupBuilder);

        _steps.Add(step);

        // Return continuation builder with TActivity as previous step type
        return new WorkflowBuilder<TWorkflowData, TActivity>(_steps, workflowName, workflowVersion);
    }

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    public IWorkflowBuilder<TWorkflowData, TActivity> Request<TActivity>(
        string name,
        Action<IActivitySetupBuilder<TWorkflowData, TActivity>>? configure = null,
        Expression<Func<WorkflowContext<TWorkflowData>, string>>? subjectExpression = null)
        where TActivity : class, IAsyncActivity
    {
        var expression = subjectExpression == null ? null : WorkflowExpressionSignature.Checked(subjectExpression);
        var result = Request<TActivity>(name, configure, subjectExpression?.Compile());
        _steps[^1].RequestSubjectExpression = expression;
        return result;
    }

    public IWorkflowBuilder<TWorkflowData, TActivity> Request<TActivity>(string name,
        Action<IActivitySetupBuilder<TWorkflowData, TActivity>>? configure = null,
        Func<WorkflowContext<TWorkflowData>, string>? subject = null)
        where TActivity : class, IAsyncActivity
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var replyType = RequestPairing.ReplyTypeOf(typeof(TActivity));
        var next = Step(configure);
        var step = _steps[^1];
        step.RequestName = name;
        step.ReplyType = replyType;
        step.RequestSubjectBehavior = subject;
        if (subject != null)
            step.RequestSubject = context => subject((WorkflowContext<TWorkflowData>)context);
        return next;
    }

}

/// <summary>
///     Continuation workflow builder for subsequent steps (has previous step)
/// </summary>
public class WorkflowBuilder<TWorkflowData, TPreviousStepData>(
    List<WorkflowStep> steps,
    string name = "",
    int workflowVersion = 1)
    : IWorkflowBuilder<TWorkflowData, TPreviousStepData>
    where TWorkflowData : class
    where TPreviousStepData : class
{
    protected readonly List<WorkflowStep> _steps = steps;

    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> WithId(string id, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        Core.Runtime.WorkflowStepIdentity.Assign(_steps, id, sourcePath, sourceLine);
        return this;
    }

    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> Named(string name)
    {
        Core.Runtime.WorkflowStepIdentity.Name(_steps, name);
        return this;
    }

    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> Delay(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        _steps.Add(new WorkflowStep
        {
            Runtime = Core.Runtime.WorkflowStepDelegates.Create<TWorkflowData, TPreviousStepData>(),
            Name = "Delay",
            StepType = WorkflowStepType.Delay,
            WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = typeof(TPreviousStepData),
            DelayDuration = duration,
            Order = _steps.Count
        });
        return this;
    }

    /// <summary>
    ///     Adds an activity step to the workflow
    /// </summary>
    public IWorkflowBuilder<TWorkflowData, TActivity> Step<TActivity>(
        Action<IActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>>? configure = null)
        where TActivity : class, IAsyncActivity
    {
        var step = new WorkflowStep
        {
            Runtime = Core.Runtime.WorkflowStepDelegates.Create<TWorkflowData, TPreviousStepData>(),
            Id = Guid.NewGuid().ToString(),
            Name = typeof(TActivity).Name,
            StepType = WorkflowStepType.Activity,
            ActivityType = typeof(TActivity),
            WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = typeof(TPreviousStepData),
            Order = _steps.Count
        };

        // Create setup builder and configure it
        var setupBuilder = new ActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>(step);
        configure?.Invoke(setupBuilder);

        _steps.Add(step);

        // Return continuation builder with TActivity as new previous step type
        return new WorkflowBuilder<TWorkflowData, TActivity>(_steps, name, workflowVersion);
    }

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    public IWorkflowBuilder<TWorkflowData, TActivity> Request<TActivity>(
        string name,
        Action<IActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>>? configure = null,
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, string>>? subjectExpression = null)
        where TActivity : class, IAsyncActivity
    {
        var expression = subjectExpression == null ? null : WorkflowExpressionSignature.Checked(subjectExpression);
        var result = Request<TActivity>(name, configure, subjectExpression?.Compile());
        _steps[^1].RequestSubjectExpression = expression;
        return result;
    }

    public IWorkflowBuilder<TWorkflowData, TActivity> Request<TActivity>(string requestName,
        Action<IActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>>? configure = null,
        Func<WorkflowContext<TWorkflowData, TPreviousStepData>, string>? subject = null)
        where TActivity : class, IAsyncActivity
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestName);
        var replyType = RequestPairing.ReplyTypeOf(typeof(TActivity));
        var next = Step(configure);
        var step = _steps[^1];
        step.RequestName = requestName;
        step.ReplyType = replyType;
        step.RequestSubjectBehavior = subject;
        if (subject != null)
            step.RequestSubject = context => subject((WorkflowContext<TWorkflowData, TPreviousStepData>)context);
        return next;
    }

    /// <summary>
    ///     Invokes a workflow by type reference
    /// </summary>
    public IWorkflowBuilder<TWorkflowData, TInvokedData> Invoke<TWorkflow, TInvokedData>(
        Action<IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>>? configure = null)
        where TWorkflow : IWorkflow<TInvokedData> where TInvokedData : class
    {
        var step = new WorkflowStep
        {
            Runtime = Core.Runtime.WorkflowStepDelegates.Create<TWorkflowData, TPreviousStepData>(Core.Runtime.ChildWorkflowBinding.Resolve<TWorkflow, TInvokedData>),
            Id = Guid.NewGuid().ToString(),
            Name = typeof(TWorkflow).Name,
            StepType = WorkflowStepType.WorkflowInvocation,
            WorkflowType = typeof(TWorkflow),
            InvokedDataType = typeof(TInvokedData),
            WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = typeof(TPreviousStepData),
            Order = _steps.Count
        };

        // Create invocation builder and configure it
        var invocationBuilder = new WorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>(step);
        configure?.Invoke(invocationBuilder);

        _steps.Add(step);

        return new WorkflowBuilder<TWorkflowData, TInvokedData>(_steps, name, workflowVersion);
    }

    /// <summary>
    ///     Invokes a workflow by name and version
    /// </summary>
    public IWorkflowBuilder<TWorkflowData, TInvokedData> Invoke<TInvokedData>(string workflowName, int version,
        Action<IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>>? configure = null)
        where TInvokedData : class
    {
        var step = new WorkflowStep
        {
            Runtime = Core.Runtime.WorkflowStepDelegates.Create<TWorkflowData, TPreviousStepData>(),
            Id = Guid.NewGuid().ToString(),
            Name = workflowName,
            StepType = WorkflowStepType.WorkflowInvocation,
            WorkflowName = workflowName,
            InvokedDataType = typeof(TInvokedData),
            WorkflowVersion = version,
            WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = typeof(TPreviousStepData),
            Order = _steps.Count
        };

        // Create invocation builder and configure it
        var invocationBuilder = new WorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>(step);
        configure?.Invoke(invocationBuilder);

        _steps.Add(step);

        return new WorkflowBuilder<TWorkflowData, TInvokedData>(_steps, name, workflowVersion);
    }

    /// <summary>
    ///     Creates a sequence container
    /// </summary>
    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> Sequence(
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure)
    {
        var sequenceStep = new WorkflowStep
        {
            Runtime = Core.Runtime.WorkflowStepDelegates.Create<TWorkflowData, TPreviousStepData>(),
            Id = Guid.NewGuid().ToString(),
            Name = "Sequence",
            StepType = WorkflowStepType.Sequence,
            WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = typeof(TPreviousStepData),
            Order = _steps.Count
        };

        // Create sequence builder (inherits from workflow builder)
        var sequenceBuilder = new SequenceBuilder<TWorkflowData, TPreviousStepData>(sequenceStep.SequenceSteps);
        configure(sequenceBuilder);

        _steps.Add(sequenceStep);

        // Return this builder (sequence is transparent pass-through)
        return this;
    }

    /// <summary>
    ///     Creates a conditional block
    /// </summary>
    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> If(
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, bool>> condition,
        Action<IWorkflowBuilder<TWorkflowData, TPreviousStepData>> then,
        Action<IWorkflowBuilder<TWorkflowData, TPreviousStepData>>? @else = null)
    {
        // Compile the condition expression at build time for maximum performance
        var compiledCondition = condition.Compile();

        // Create wrapper function that takes object and returns bool
        Func<object, bool> conditionWrapper = context =>
            compiledCondition((WorkflowContext<TWorkflowData, TPreviousStepData>)context);

        var conditionalStep = new WorkflowStep
        {
            Runtime = Core.Runtime.WorkflowStepDelegates.Create<TWorkflowData, TPreviousStepData>(),
            Id = Guid.NewGuid().ToString(),
            Name = "Conditional",
            StepType = WorkflowStepType.Conditional,
            WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = typeof(TPreviousStepData),
            CompiledCondition = conditionWrapper,
            ConditionExpression = Core.Runtime.WorkflowExpressionSignature.Checked(condition),
            Order = _steps.Count
        };

        // Configure then branch
        var thenBuilder = new WorkflowBuilder<TWorkflowData, TPreviousStepData>(conditionalStep.ThenSteps);
        then(thenBuilder);

        // Configure else branch if provided
        if (@else != null)
        {
            var elseBuilder = new WorkflowBuilder<TWorkflowData, TPreviousStepData>(conditionalStep.ElseSteps);
            @else(elseBuilder);
        }

        _steps.Add(conditionalStep);

        return this;
    }

    /// <summary>
    ///     Creates a parallel execution block
    /// </summary>
    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> Parallel(
        Action<IParallelBuilder<TWorkflowData, TPreviousStepData>> configure)
    {
        var parallelStep = new WorkflowStep
        {
            Runtime = Core.Runtime.WorkflowStepDelegates.Create<TWorkflowData, TPreviousStepData>(),
            Id = Guid.NewGuid().ToString(),
            Name = "Parallel",
            StepType = WorkflowStepType.Parallel,
            WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = typeof(TPreviousStepData),
            Order = _steps.Count
        };

        // Create parallel builder
        var parallelBuilder = new ParallelBuilder<TWorkflowData, TPreviousStepData>(parallelStep);
        configure(parallelBuilder);

        _steps.Add(parallelStep);

        return this;
    }

    /// <summary>
    ///     Creates a try/catch block
    /// </summary>
    public ITryBuilder<TWorkflowData, TPreviousStepData> Try(
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure)
    {
        var tryStep = new WorkflowStep
        {
            Runtime = Core.Runtime.WorkflowStepDelegates.Create<TWorkflowData, TPreviousStepData>(),
            Id = Guid.NewGuid().ToString(),
            Name = "Try",
            StepType = WorkflowStepType.TryCatch,
            WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = typeof(TPreviousStepData),
            Order = _steps.Count
        };

        // Create sequence builder for try block
        var sequenceBuilder = new SequenceBuilder<TWorkflowData, TPreviousStepData>(tryStep.SequenceSteps);
        configure(sequenceBuilder);

        _steps.Add(tryStep);

        return new TryBuilder<TWorkflowData, TPreviousStepData>(
            _steps, tryStep, name, workflowVersion);
    }

    /// <summary>
    ///     Creates a saga container
    /// </summary>
    public ISagaContainerBuilder<TWorkflowData, TPreviousStepData> Saga(
        Action<ISagaActivityBuilder<TWorkflowData, TPreviousStepData>> configure)
    {
        var sagaStep = new WorkflowStep
        {
            Runtime = Core.Runtime.WorkflowStepDelegates.Create<TWorkflowData, TPreviousStepData>(),
            Id = Guid.NewGuid().ToString(),
            Name = "Saga",
            StepType = WorkflowStepType.Saga,
            WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = typeof(TPreviousStepData),
            Order = _steps.Count,
            SequenceSteps = []
        };

        // Initialize saga steps list and error handlers
        var sagaSteps = new List<SagaStepInfo>();

        // Create saga builder
        var sagaBuilder = new SagaActivityBuilder<TWorkflowData, TPreviousStepData>(sagaStep.SequenceSteps, sagaSteps);
        configure(sagaBuilder);

        // Store saga configuration in step metadata
        sagaStep.StepMetadata["SagaSteps"] = sagaSteps;

        _steps.Add(sagaStep);

        // Return a real saga container builder that delegates to this workflow builder
        return new SagaContainerBuilder<TWorkflowData, TPreviousStepData>(_steps, name, workflowVersion);
    }

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    public IWorkflowBuilder<TWorkflowData, TEvent> WaitFor<TEvent>(
        string key,
        Expression<Func<TEvent, WorkflowContext<TWorkflowData>, bool>>? matchesExpression = null,
        Action<ISuspendSetupBuilder<TWorkflowData, TEvent, TPreviousStepData>>? configure = null)
        where TEvent : class
    {
        var expression = matchesExpression == null ? null : WorkflowExpressionSignature.Checked(matchesExpression);
        var result = WaitFor<TEvent>(key, matchesExpression?.Compile(), configure);
        _steps[^1].ConditionExpression = expression;
        return result;
    }

    /// <summary>
    ///     Creates a suspend/resume block
    /// </summary>
    public IWorkflowBuilder<TWorkflowData, TEvent> WaitFor<TEvent>(
        string key,
        Func<TEvent, WorkflowContext<TWorkflowData>, bool>? matches = null,
        Action<ISuspendSetupBuilder<TWorkflowData, TEvent, TPreviousStepData>>? configure = null)
        where TEvent : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var next = Suspend(key, matches, configure);
        var waitStep = _steps[^1];
        waitStep.Name = "WaitFor";
        waitStep.StepMetadata["WaitKey"] = key;
        return next;
    }

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    public IWorkflowBuilder<TWorkflowData, TResumeEvent> Suspend<TResumeEvent>(
        string suspendReason,
        Expression<Func<TResumeEvent, WorkflowContext<TWorkflowData>, bool>>? resumeExpression = null,
        Action<ISuspendSetupBuilder<TWorkflowData, TResumeEvent, TPreviousStepData>>? configure = null)
        where TResumeEvent : class
    {
        var expression = resumeExpression == null ? null : WorkflowExpressionSignature.Checked(resumeExpression);
        var result = Suspend<TResumeEvent>(suspendReason, resumeExpression?.Compile(), configure);
        _steps[^1].ConditionExpression = expression;
        return result;
    }

    public IWorkflowBuilder<TWorkflowData, TResumeEvent> Suspend<TResumeEvent>(
        string suspendReason,
        Func<TResumeEvent, WorkflowContext<TWorkflowData>, bool>? resumeCondition = null,
        Action<ISuspendSetupBuilder<TWorkflowData, TResumeEvent, TPreviousStepData>>? configure = null)
        where TResumeEvent : class
    {
        var suspendStep = new WorkflowStep
        {
            Runtime = Core.Runtime.WorkflowStepDelegates.Wait<TWorkflowData, TResumeEvent, TPreviousStepData>(),
            Id = Guid.NewGuid().ToString(),
            Name = "Suspend",
            StepType = WorkflowStepType.SuspendResume,
            WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = typeof(TPreviousStepData),
            ResumeEventType = typeof(TResumeEvent),
            Order = _steps.Count,
            StepMetadata =
            {
                // Store suspend reason in metadata
                ["SuspendReason"] = suspendReason,
                ["WaitKey"] = suspendReason
            }
        };

        // Store resume condition in CompiledCondition for proper evaluation
        if (resumeCondition != null)
        {
            // Create wrapper function that takes object and returns bool
            Func<object, bool> conditionWrapper = context =>
            {
                // The context should be a ResumeEventContext<TWorkflowData, TResumeEvent>
                if (context is ResumeEventContext<TWorkflowData, TResumeEvent> resumeContext)
                {
                    // Create WorkflowContext<TWorkflowData> for the resume condition
                    var workflowContext = new WorkflowContext<TWorkflowData>
                    {
                        WorkflowData = resumeContext.WorkflowData,
                        Items = resumeContext.Items
                    };
                    return resumeCondition(resumeContext.ResumeEvent, workflowContext);
                }
                return false;
            };
            
            suspendStep.CompiledCondition = conditionWrapper;
            suspendStep.ConditionBehavior = resumeCondition;
        }

        // Configure the suspend step if a setup action is provided
        if (configure != null)
        {
            var setupBuilder = new SuspendSetupBuilder<TWorkflowData, TResumeEvent, TPreviousStepData>(suspendStep);
            configure(setupBuilder);
        }

        _steps.Add(suspendStep);

        // Return a workflow builder with TResumeEvent as the previous step type
        return new WorkflowBuilder<TWorkflowData, TResumeEvent>(_steps, name, workflowVersion);
    }

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> DoWhile(
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure,
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, bool>> conditionExpression)
    {
        var expression = WorkflowExpressionSignature.Checked(conditionExpression);
        var result = DoWhile(configure, conditionExpression.Compile());
        _steps[^1].ConditionExpression = expression;
        return result;
    }

    /// <summary>
    ///     Creates a do-while loop
    /// </summary>
    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> DoWhile(
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure,
        Func<WorkflowContext<TWorkflowData, TPreviousStepData>, bool> condition)
    {
        // Create wrapper function that takes object and returns bool for maximum performance
        Func<object, bool> conditionWrapper =
            context => condition((WorkflowContext<TWorkflowData, TPreviousStepData>)context);

        var loopStep = new WorkflowStep
        {
            Runtime = Core.Runtime.WorkflowStepDelegates.Create<TWorkflowData, TPreviousStepData>(),
            Id = Guid.NewGuid().ToString(),
            Name = "DoWhile",
            StepType = WorkflowStepType.Loop,
            LoopType = LoopType.DoWhile,
            ConditionBehavior = condition,
            WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = typeof(TPreviousStepData),
            CompiledCondition = conditionWrapper,
            Order = _steps.Count
        };

        // Configure loop body
        var sequenceBuilder = new SequenceBuilder<TWorkflowData, TPreviousStepData>(loopStep.LoopBodySteps);
        configure(sequenceBuilder);

        loopStep.StepMetadata["LoopType"] = LoopType.DoWhile;

        _steps.Add(loopStep);

        return this;
    }

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> WhileDo(
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, bool>> conditionExpression,
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure)
    {
        var expression = WorkflowExpressionSignature.Checked(conditionExpression);
        var result = WhileDo(conditionExpression.Compile(), configure);
        _steps[^1].ConditionExpression = expression;
        return result;
    }

    /// <summary>
    ///     Creates a while-do loop
    /// </summary>
    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> WhileDo(
        Func<WorkflowContext<TWorkflowData, TPreviousStepData>, bool> condition,
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure)
    {
        // Create wrapper function that takes object and returns bool for maximum performance
        Func<object, bool> conditionWrapper =
            context => condition((WorkflowContext<TWorkflowData, TPreviousStepData>)context);

        var loopStep = new WorkflowStep
        {
            Runtime = Core.Runtime.WorkflowStepDelegates.Create<TWorkflowData, TPreviousStepData>(),
            Id = Guid.NewGuid().ToString(),
            Name = "WhileDo",
            StepType = WorkflowStepType.Loop,
            LoopType = LoopType.WhileDo,
            ConditionBehavior = condition,
            WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = typeof(TPreviousStepData),
            CompiledCondition = conditionWrapper,
            Order = _steps.Count
        };

        // Configure loop body
        var sequenceBuilder = new SequenceBuilder<TWorkflowData, TPreviousStepData>(loopStep.LoopBodySteps);
        configure(sequenceBuilder);

        loopStep.StepMetadata["LoopType"] = LoopType.WhileDo;

        _steps.Add(loopStep);

        return this;
    }

    [OverloadResolutionPriority(-1)]
    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> ForEach<TItem>(
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, IEnumerable<TItem>?>> itemsExpression,
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>, ItemScope<TItem>> body,
        Action<IForEachSetup<TWorkflowData>>? setup = null)
    {
        ArgumentNullException.ThrowIfNull(itemsExpression);
        var expression = Core.Runtime.WorkflowExpressionSignature.Checked(itemsExpression);
        var result = ForEach(itemsExpression.Compile(), body, setup);
        _steps[^1].ForEachItemsExpression = expression;
        return result;
    }

    [OverloadResolutionPriority(-1)]
    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> Result<TResult>(
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, TResult>> valueExpression)
    {
        ArgumentNullException.ThrowIfNull(valueExpression);
        var expression = Core.Runtime.WorkflowExpressionSignature.Checked(valueExpression);
        var result = Result(valueExpression.Compile());
        _steps[^1].ResultExpression = expression;
        return result;
    }

    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> ForEach<TItem>(
        Func<WorkflowContext<TWorkflowData, TPreviousStepData>, IEnumerable<TItem>?> items,
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>, ItemScope<TItem>> body,
        Action<IForEachSetup<TWorkflowData>>? setup = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(body);
        var step = new WorkflowStep
        {
            Runtime = Core.Runtime.WorkflowStepDelegates.Create<TWorkflowData, TPreviousStepData>(),
            Id = Guid.NewGuid().ToString(),
            Name = "ForEach",
            StepType = WorkflowStepType.ForEach,
            WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = typeof(TPreviousStepData),
            ForEachItemsBehavior = items,
            ForEachItems = context => items((WorkflowContext<TWorkflowData, TPreviousStepData>)context),
            Order = _steps.Count
        };
        // The setup runs first, so the handle the body is given carries its label.
        var options = new ForEachSetup<TWorkflowData>(step);
        setup?.Invoke(options);
        var item = new ItemScope<TItem>(options.Label);
        step.ForEachItem = item;
        body(new SequenceBuilder<TWorkflowData, TPreviousStepData>(step.ForEachBodySteps), item);
        _steps.Add(step);
        return this;
    }

    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> Result<TResult>(
        Func<WorkflowContext<TWorkflowData, TPreviousStepData>, TResult> value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _steps.Add(new WorkflowStep
        {
            Runtime = Core.Runtime.WorkflowStepDelegates.Create<TWorkflowData, TPreviousStepData>(),
            Id = Guid.NewGuid().ToString(),
            Name = "Result",
            StepType = WorkflowStepType.ForEachResult,
            WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = typeof(TPreviousStepData),
            ResultType = typeof(TResult),
            ResultBehavior = value,
            ResultSelector = context => value((WorkflowContext<TWorkflowData, TPreviousStepData>)context),
            Order = _steps.Count
        });
        return this;
    }

    /// <summary>
    ///     Builds the final workflow definition
    /// </summary>
    public WorkflowDefinition Build()
    {
        var definition = new WorkflowDefinition
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            Version = workflowVersion,
            WorkflowDataType = typeof(TWorkflowData),
            CreatedAt = DateTime.UtcNow,
            EstimatedStepCount = _steps.Count,
            Steps = _steps
        };
        WorkflowDefinitionValidator.Apply(definition);
        return definition;
    }

    /// <summary>Adds a saga error handler without exception properties.</summary>
    public ISagaContainerBuilder<TWorkflowData, TPreviousStepData> OnError<TException>(
        Action<ISagaErrorBuilder<TWorkflowData, EmptyFault, TPreviousStepData>> configure,
        string? id = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TException : Exception => OnError<TException, EmptyFault>(configure, id, sourcePath, sourceLine);

    /// <summary>Adds a saga error handler with a checkpointable fault value.</summary>
    public ISagaContainerBuilder<TWorkflowData, TPreviousStepData> OnError<TException, TFault>(
        Action<ISagaErrorBuilder<TWorkflowData, TFault, TPreviousStepData>> configure,
        string? id = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(configure);
        Core.Runtime.FaultProjection.For(typeof(TException), typeof(TFault));
        var sagaStep = _steps.LastOrDefault(step => step.StepType == WorkflowStepType.Saga)
            ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                "OnError can only be called after a Saga block");
        var handlerBlock = new WorkflowStep
        {
            Runtime = Core.Runtime.WorkflowStepDelegates.Create<TWorkflowData, TPreviousStepData>(),
            Name = $"OnError<{typeof(TException).Name}>",
            StepType = WorkflowStepType.CatchBlock,
            WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = typeof(TPreviousStepData),
            ExceptionType = typeof(TException),
            FaultType = typeof(TFault),
            Id = id ?? Guid.NewGuid().ToString(),
            SourceLocation = sourcePath + ":" + sourceLine,
            Order = sagaStep.CatchBlocks.Count
        };
        if (!sagaStep.StepMetadata.TryGetValue("SagaSteps", out var sagaStepsValue))
        {
            sagaStepsValue = new List<SagaStepInfo>();
            sagaStep.StepMetadata["SagaSteps"] = sagaStepsValue;
        }
        var handlers = new List<ErrorHandler>();
        configure(new SagaErrorBuilder<TWorkflowData, TFault, TPreviousStepData>(
            handlerBlock.SequenceSteps, (List<SagaStepInfo>)sagaStepsValue, handlers));
        var configured = handlers.LastOrDefault()?.Handler;
        var policy = configured switch
        {
            TerminationErrorHandler termination => termination.SagaErrorConfig,
            ContinuationErrorHandler continuation => continuation.SagaErrorConfig,
            RetryErrorHandler retry => new SagaErrorConfiguration
            {
                CompensationStrategy = retry.PreExecute?.Strategy ?? CompensationStrategy.CompensateAll,
                CompensationTargetType = retry.PreExecute?.CompensationTargetType,
                ContinuationAction = SagaContinuationAction.Retry,
                RetryPolicy = retry.RetryPolicy
            },
            // An undo with no Then... keeps its choice; the original error is raised again after it (D-36).
            CompensationErrorHandler undo => new SagaErrorConfiguration
            {
                CompensationStrategy = undo.Strategy,
                CompensationTargetType = undo.CompensationTargetType,
                RetryPolicy = null
            },
            _ => null
        };
        if (policy != null)
            handlerBlock.StepMetadata["SagaErrorConfig"] = policy;
        sagaStep.CatchBlocks.Add(handlerBlock);
        return new SagaContainerBuilder<TWorkflowData, TPreviousStepData>(_steps, name, workflowVersion);
    }
    /// <summary>
    ///     Adds saga error handling for any exception
    /// </summary>
    public ISagaContainerBuilder<TWorkflowData, TPreviousStepData> OnError(
        Action<ISagaErrorBuilder<TWorkflowData, EmptyFault, TPreviousStepData>> configure,
        string? id = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        return OnError<Exception>(configure, id, sourcePath, sourceLine);
    }

}
