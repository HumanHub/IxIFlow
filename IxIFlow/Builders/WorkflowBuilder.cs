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

    public IWorkflowBuilder<TWorkflowData> Delay(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        _steps.Add(new WorkflowStep
        {
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
        return new WorkflowDefinition
        {
            Id = Guid.NewGuid().ToString(),
            Name = workflowName,
            Version = workflowVersion,
            WorkflowDataType = typeof(TWorkflowData),
            CreatedAt = DateTime.UtcNow,
            EstimatedStepCount = _steps.Count,
            Steps = [.._steps]
        };
    }

    public IWorkflowBuilder<TWorkflowData, TActivity> Step<TActivity>(
        Action<IActivitySetupBuilder<TWorkflowData, TActivity>>? configure = null)
        where TActivity : class, IAsyncActivity
    {
        var step = new WorkflowStep
        {
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

    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> Delay(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        _steps.Add(new WorkflowStep
        {
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
        Action<IActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>> configure)
        where TActivity : class, IAsyncActivity
    {
        var step = new WorkflowStep
        {
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
        configure(setupBuilder);

        _steps.Add(step);

        // Return continuation builder with TActivity as new previous step type
        return new WorkflowBuilder<TWorkflowData, TActivity>(_steps, name, workflowVersion);
    }

    /// <summary>
    ///     Invokes a workflow by type reference
    /// </summary>
    public IWorkflowBuilder<TWorkflowData, TInvokedData> Invoke<TWorkflow, TInvokedData>(
        Action<IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>> configure)
        where TWorkflow : IWorkflow<TInvokedData> where TInvokedData : class
    {
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid().ToString(),
            Name = typeof(TWorkflow).Name,
            StepType = WorkflowStepType.WorkflowInvocation,
            WorkflowType = typeof(TWorkflow),
            WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = typeof(TPreviousStepData),
            Order = _steps.Count
        };

        // Create invocation builder and configure it
        var invocationBuilder = new WorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>(step);
        configure(invocationBuilder);

        _steps.Add(step);

        return new WorkflowBuilder<TWorkflowData, TInvokedData>(_steps, name, workflowVersion);
    }

    /// <summary>
    ///     Invokes a workflow by name and version
    /// </summary>
    public IWorkflowBuilder<TWorkflowData, TInvokedData> Invoke<TInvokedData>(string workflowName, int version,
        Action<IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>> configure)
        where TInvokedData : class
    {
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid().ToString(),
            Name = workflowName,
            StepType = WorkflowStepType.WorkflowInvocation,
            WorkflowName = workflowName,
            WorkflowVersion = version,
            WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = typeof(TPreviousStepData),
            Order = _steps.Count
        };

        // Create invocation builder and configure it
        var invocationBuilder = new WorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>(step);
        configure(invocationBuilder);

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
            Id = Guid.NewGuid().ToString(),
            Name = "Conditional",
            StepType = WorkflowStepType.Conditional,
            WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = typeof(TPreviousStepData),
            CompiledCondition = conditionWrapper,
            ConditionExpressionSignature = Core.Runtime.WorkflowExpressionSignature.Of(condition),
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

    public IWorkflowBuilder<TWorkflowData, TResumeEvent> Suspend<TResumeEvent>(
        string suspendReason,
        Func<TResumeEvent, WorkflowContext<TWorkflowData>, bool>? resumeCondition = null,
        Action<ISuspendSetupBuilder<TWorkflowData, TResumeEvent, TPreviousStepData>>? configure = null)
        where TResumeEvent : class
    {
        var suspendStep = new WorkflowStep
        {
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
                        WorkflowData = resumeContext.WorkflowData
                    };
                    return resumeCondition(resumeContext.ResumeEvent, workflowContext);
                }
                return false;
            };
            
            suspendStep.CompiledCondition = conditionWrapper;
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
            Id = Guid.NewGuid().ToString(),
            Name = "DoWhile",
            StepType = WorkflowStepType.Loop,
            LoopType = LoopType.DoWhile,
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
            Id = Guid.NewGuid().ToString(),
            Name = "WhileDo",
            StepType = WorkflowStepType.Loop,
            LoopType = LoopType.WhileDo,
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

    /// <summary>
    ///     Builds the final workflow definition
    /// </summary>
    public WorkflowDefinition Build()
    {
        return new WorkflowDefinition
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            Version = workflowVersion,
            WorkflowDataType = typeof(TWorkflowData),
            CreatedAt = DateTime.UtcNow,
            EstimatedStepCount = _steps.Count,
            Steps = [.._steps]
        };
    }

    /// <summary>Adds a saga error handler without exception properties.</summary>
    public ISagaContainerBuilder<TWorkflowData, TPreviousStepData> OnError<TException>(
        Action<ISagaErrorBuilder<TWorkflowData, EmptyFault, TPreviousStepData>> configure)
        where TException : Exception => OnError<TException, EmptyFault>(configure);

    /// <summary>Adds a saga error handler with a checkpointable fault value.</summary>
    public ISagaContainerBuilder<TWorkflowData, TPreviousStepData> OnError<TException, TFault>(
        Action<ISagaErrorBuilder<TWorkflowData, TFault, TPreviousStepData>> configure)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(configure);
        Core.Runtime.FaultProjection.For(typeof(TException), typeof(TFault));
        var sagaStep = _steps.LastOrDefault(step => step.StepType == WorkflowStepType.Saga)
            ?? throw new InvalidOperationException("OnError can only be called after a Saga block");
        var handlerBlock = new WorkflowStep
        {
            Name = $"OnError<{typeof(TException).Name}>",
            StepType = WorkflowStepType.CatchBlock,
            WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = typeof(TPreviousStepData),
            ExceptionType = typeof(TException),
            FaultType = typeof(TFault),
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
            _ => null
        };
        if (policy != null)
            handlerBlock.StepMetadata["SagaErrorConfig"] = policy;
        else if (handlerBlock.SequenceSteps.Any(step =>
                     step.StepMetadata.ContainsKey("WaitKey")))
            throw new InvalidOperationException(
                "A saga error handler with WaitFor must end with ThenContinue, ThenRetry, or ThenTerminate");
        sagaStep.CatchBlocks.Add(handlerBlock);
        return new SagaContainerBuilder<TWorkflowData, TPreviousStepData>(_steps, name, workflowVersion);
    }
    /// <summary>
    ///     Adds saga error handling for any exception
    /// </summary>
    public ISagaContainerBuilder<TWorkflowData, TPreviousStepData> OnError(
        Action<ISagaErrorBuilder<TWorkflowData, EmptyFault, TPreviousStepData>> configure)
    {
        return OnError<Exception>(configure);
    }
}
