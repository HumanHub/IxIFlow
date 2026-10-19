using IxIFlow.Core.Runtime;
using System.Runtime.CompilerServices;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;

namespace IxIFlow.Builders;

/// <summary>
///     Saga activity builder for method chaining
/// </summary>
public class SagaActivityBuilder<TWorkflowData, TPreviousStepData>(
    List<WorkflowStep> steps,
    List<SagaStepInfo> sagaSteps) : ISagaActivityBuilder<TWorkflowData, TPreviousStepData>
    where TWorkflowData : class
    where TPreviousStepData : class
{
    private readonly List<SagaStepInfo> _sagaSteps = sagaSteps ?? throw new ArgumentNullException(nameof(sagaSteps));
    private readonly List<WorkflowStep> _steps = steps ?? throw new ArgumentNullException(nameof(steps));

    public ISagaActivityBuilder<TWorkflowData, TPreviousStepData> WithId(string id, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var previousId = _steps.LastOrDefault()?.Id;
        Core.Runtime.WorkflowStepIdentity.Assign(_steps, id, sourcePath, sourceLine);
        foreach (var step in _sagaSteps.Where(step => step.StepId == previousId)) step.StepId = id;
        return this;
    }

    public ISagaActivityBuilder<TWorkflowData, TActivity> Step<TActivity>(
        Action<ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>>? configure = null)
        where TActivity : class, IAsyncActivity
    {
        var stepId = Guid.NewGuid().ToString();
        var workflowStep = new WorkflowStep
        {
            Runtime = Core.Runtime.WorkflowStepDelegates.Create<TWorkflowData, TPreviousStepData>(),
            Id = stepId,
            Name = typeof(TActivity).Name,
            StepType = WorkflowStepType.Activity,
            ActivityType = typeof(TActivity),
            WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = typeof(TPreviousStepData),
            InputMappings = [],
            OutputMappings = [],
            StepMetadata = new Dictionary<string, object>
            {
                ["IsSagaStep"] = true,
                ["SagaStepOrder"] = _sagaSteps.Count
            }
        };

        // Create saga step info for compensation tracking
        var sagaStepInfo = new SagaStepInfo
        {
            StepId = stepId,
            ActivityType = typeof(TActivity),
            Order = _sagaSteps.Count,
            CompensationActivities = []
        };

        // Create the setup builder
        var setupBuilder = new SagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>(
            workflowStep, sagaStepInfo);

        // Configure the step
        configure?.Invoke(setupBuilder);

        // Add to collections
        _steps.Add(workflowStep);
        _sagaSteps.Add(sagaStepInfo);

        // Create next activity builder with Activity as TPreviousStep for chaining
        return new SagaActivityBuilder<TWorkflowData, TActivity>(_steps, _sagaSteps);
    }

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    public ISagaActivityBuilder<TWorkflowData, TActivity> Request<TActivity>(
        string name,
        Action<ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>>? configure = null,
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, string>>? subjectExpression = null)
        where TActivity : class, IAsyncActivity
    {
        var expression = subjectExpression == null ? null : WorkflowExpressionSignature.Checked(subjectExpression);
        var result = Request<TActivity>(name, configure, subjectExpression?.Compile());
        _steps[^1].RequestSubjectExpression = expression;
        return result;
    }

    public ISagaActivityBuilder<TWorkflowData, TActivity> Request<TActivity>(string name,
        Action<ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>>? configure = null,
        Func<WorkflowContext<TWorkflowData, TPreviousStepData>, string>? subject = null)
        where TActivity : class, IAsyncActivity
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var replyType = RequestPairing.ReplyTypeOf(typeof(TActivity));
        var next = Step(configure);
        var step = _steps[^1];
        step.RequestName = name;
        step.ReplyType = replyType;
        if (subject != null)
            step.RequestSubject = context => subject((WorkflowContext<TWorkflowData, TPreviousStepData>)context);
        return next;
    }

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    public ISagaActivityBuilder<TWorkflowData, TResumeEvent> Suspend<TResumeEvent>(
        string suspendReason,
        Expression<Func<TResumeEvent, WorkflowContext<TWorkflowData, TPreviousStepData>, bool>>? resumeExpression = null,
        Action<ISuspendSetupBuilder<TWorkflowData, TResumeEvent, TPreviousStepData>>? configure = null)
        where TResumeEvent : class
    {
        var expression = resumeExpression == null ? null : WorkflowExpressionSignature.Checked(resumeExpression);
        var result = Suspend<TResumeEvent>(suspendReason, resumeExpression?.Compile(), configure);
        _steps[^1].ConditionExpression = expression;
        return result;
    }

    public ISagaActivityBuilder<TWorkflowData, TResumeEvent> Suspend<TResumeEvent>(
        string suspendReason,
        Func<TResumeEvent, WorkflowContext<TWorkflowData, TPreviousStepData>, bool>? resumeCondition = null,
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
                ["IsSagaStep"] = true,
                ["SagaStepOrder"] = _sagaSteps.Count,
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
                if (context is ResumeEventContext<TWorkflowData, TResumeEvent, TPreviousStepData> typed)
                {
                    var workflowContext = new WorkflowContext<TWorkflowData, TPreviousStepData>
                    {
                        WorkflowData = typed.WorkflowData,
                        PreviousStep = typed.PreviousStep,
                        Items = typed.Items
                    };
                    return resumeCondition(typed.ResumeEvent, workflowContext);
                }
                if (context is ResumeEventContext<TWorkflowData, TResumeEvent> resumeContext)
                {
                    var workflowContext = new WorkflowContext<TWorkflowData, TPreviousStepData>
                    {
                        WorkflowData = resumeContext.WorkflowData,
                        Items = resumeContext.Items
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
        return new SagaActivityBuilder<TWorkflowData, TResumeEvent>(_steps,sagaSteps);
    }

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    public ISagaActivityBuilder<TWorkflowData, TResumeEvent> WaitFor<TResumeEvent>(
        string key,
        Expression<Func<TResumeEvent, WorkflowContext<TWorkflowData, TPreviousStepData>, bool>>? matchesExpression = null,
        Action<ISuspendSetupBuilder<TWorkflowData, TResumeEvent, TPreviousStepData>>? configure = null)
        where TResumeEvent : class
    {
        var expression = matchesExpression == null ? null : WorkflowExpressionSignature.Checked(matchesExpression);
        var result = WaitFor<TResumeEvent>(key, matchesExpression?.Compile(), configure);
        _steps[^1].ConditionExpression = expression;
        return result;
    }

    public ISagaActivityBuilder<TWorkflowData, TResumeEvent> WaitFor<TResumeEvent>(
        string key,
        Func<TResumeEvent, WorkflowContext<TWorkflowData, TPreviousStepData>, bool>? matches = null,
        Action<ISuspendSetupBuilder<TWorkflowData, TResumeEvent, TPreviousStepData>>? configure = null)
        where TResumeEvent : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var next = Suspend(key, matches, configure);
        var waitStep = _steps[^1];
        waitStep.Name = "WaitFor";
        waitStep.StepMetadata["WaitKey"] = key;
        return next;
    }

    public ISagaActivityBuilder<TWorkflowData, TPreviousStepData> OutcomeOn<TProperty>(
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, TProperty>> propertySelector,
        Action<ISagaOutcomeBuilder<TWorkflowData, TPreviousStepData, TProperty>> configure)
    {
        var outcomeStep = new WorkflowStep
        {
            Runtime = Core.Runtime.WorkflowStepDelegates.Create<TWorkflowData, TPreviousStepData>(),
            Id = Guid.NewGuid().ToString(),
            Name = "SagaOutcome",
            StepType = WorkflowStepType.Conditional,
            WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = typeof(TPreviousStepData),
            Order = _steps.Count,
            StepMetadata =
            {
                ["IsSagaStep"] = true,
                ["SagaStepOrder"] = _sagaSteps.Count,
                ["OutcomeType"] = "PropertyBased"
            }
        };

        // Create outcome builder and configure it
        var outcomeBuilder = new SagaOutcomeBuilder<TWorkflowData, TPreviousStepData, TProperty>(
            outcomeStep, propertySelector, _steps, _sagaSteps);
        
        configure(outcomeBuilder);

        // Add the outcome step to the steps collection
        _steps.Add(outcomeStep);

        // Return the current builder for chaining
        return this;
    }

}

/// <summary>
///     Saga activity setup builder for configuring individual saga steps
/// </summary>
public class SagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> 
    : ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>
    where TWorkflowData : class
    where TActivity : class, IAsyncActivity
    where TPreviousStepData : class
{
    private readonly SagaStepInfo _sagaStepInfo;
    private readonly WorkflowStep _workflowStep;

    public SagaActivitySetupBuilder(WorkflowStep workflowStep, SagaStepInfo sagaStepInfo)
    {
        _workflowStep = workflowStep ?? throw new ArgumentNullException(nameof(workflowStep));
        _sagaStepInfo = sagaStepInfo ?? throw new ArgumentNullException(nameof(sagaStepInfo));
    }

    public ISagaActivitySetupInputBuilder<TWorkflowData, TActivity, TProperty, TPreviousStepData> Input<TProperty>(
        Expression<Func<TActivity, TProperty>> property)
    {
        return new SagaActivitySetupInputBuilder<TWorkflowData, TActivity, TProperty, TPreviousStepData>(
            this, _workflowStep, property);
    }

    public ISagaActivitySetupOutputBuilder<TWorkflowData, TActivity, TProperty, TPreviousStepData> Output<TProperty>(
        Expression<Func<TActivity, TProperty>> property)
    {
        return new SagaActivitySetupOutputBuilder<TWorkflowData, TActivity, TProperty, TPreviousStepData>(
            this, _workflowStep, property);
    }

    public ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> OnError<TException>(
        Action<ISagaStepErrorHandler<TException>> handler)
        where TException : Exception
    {
        // Store step-level error handling configuration in metadata
        if (!_workflowStep.StepMetadata.ContainsKey("StepErrorHandlers"))
            _workflowStep.StepMetadata["StepErrorHandlers"] = new List<StepErrorHandlerInfo>();

        var errorHandlers = (List<StepErrorHandlerInfo>)_workflowStep.StepMetadata["StepErrorHandlers"];
        var errorHandler = new StepErrorHandlerInfo
        {
            ExceptionType = typeof(TException),
            HandlerAction = StepErrorAction.Terminate
        };

        var handlerImpl = new SagaStepErrorHandler<TException>(errorHandler);
        handler(handlerImpl);

        errorHandlers.Add(errorHandler);
        return this;
    }

    public ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> OnError(
        Action<ISagaStepErrorHandler> handler)
    {
        return OnError<Exception>(h => handler(h));
    }

    public ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>
        CompensateWith<TCompensationActivity>()
        where TCompensationActivity : IAsyncActivity
    {
        // Create simple compensation without configuration
        var compensationInfo = new CompensationActivityInfo
        {
            Runtime = Core.Runtime.WorkflowStepDelegates.Create<TWorkflowData, TActivity>(),
            ActivityType = typeof(TCompensationActivity),
            InputMappings = [],
            OutputMappings = []
        };

        _sagaStepInfo.CompensationActivities.Add(compensationInfo);

        // Transfer to WorkflowStep metadata for WorkflowEngine
        _workflowStep.StepMetadata["CompensationActivities"] = _sagaStepInfo.CompensationActivities;

        return this;
    }

    public ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> CompensateWith<TCompensationActivity>(
        Action<ICompensationActivityBuilder<TWorkflowData, TCompensationActivity, TActivity>> configure)
        where TCompensationActivity : IAsyncActivity
    {
        // Create compensation with configuration
        var compensationInfo = new CompensationActivityInfo
        {
            Runtime = Core.Runtime.WorkflowStepDelegates.Create<TWorkflowData, TActivity>(),
            ActivityType = typeof(TCompensationActivity),
            InputMappings = [],
            OutputMappings = []
        };

        var compensationBuilder =
            new CompensationActivityBuilder<TWorkflowData, TCompensationActivity, TActivity>(
                compensationInfo);

        configure(compensationBuilder);

        _sagaStepInfo.CompensationActivities.Add(compensationInfo);

        // Transfer to WorkflowStep metadata for WorkflowEngine
        _workflowStep.StepMetadata["CompensationActivities"] = _sagaStepInfo.CompensationActivities;

        return this;
    }

    // Preferred over the answer overload when a lambda fits both, as on the interface.
    [System.Runtime.CompilerServices.OverloadResolutionPriority(1)]
    public ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> CompensateWith<TCompensationActivity,
        TPreviousChainActivity>(
        Action<ICompensationActivityBuilder<TWorkflowData, TCompensationActivity, TPreviousStepData,
            TPreviousChainActivity>> configure)
        where TCompensationActivity : IAsyncActivity
        where TPreviousChainActivity : class, IAsyncActivity
    {
        // Create compensation with access to previous chain activity
        var compensationInfo = new CompensationActivityInfo
        {
            Runtime = Core.Runtime.WorkflowStepDelegates.Create<TWorkflowData, TActivity>(),
            ActivityType = typeof(TCompensationActivity),
            InputMappings = [],
            OutputMappings = [],
            PreviousChainActivityType = typeof(TPreviousChainActivity)
        };

        var compensationBuilder =
            new CompensationActivityBuilder<TWorkflowData, TCompensationActivity, TPreviousStepData,
                TPreviousChainActivity>(
                compensationInfo);

        configure(compensationBuilder);

        _sagaStepInfo.CompensationActivities.Add(compensationInfo);

        // Transfer to WorkflowStep metadata for WorkflowEngine
        _workflowStep.StepMetadata["CompensationActivities"] = _sagaStepInfo.CompensationActivities;

        return this;
    }

    public ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> CompensateWith<TCompensationActivity,
        TReply>(Action<IRequestCompensationActivityBuilder<TWorkflowData, TCompensationActivity, TActivity, TReply>> configure)
        where TCompensationActivity : IAsyncActivity
        where TReply : class
    {
        var compensationInfo = new CompensationActivityInfo
        {
            Runtime = Core.Runtime.WorkflowStepDelegates.Create<TWorkflowData, TActivity>(),
            ActivityType = typeof(TCompensationActivity),
            InputMappings = [],
            OutputMappings = [],
            ReplyType = typeof(TReply)
        };
        configure(new RequestCompensationActivityBuilder<TWorkflowData, TCompensationActivity, TActivity, TReply>(
            compensationInfo));
        _sagaStepInfo.CompensationActivities.Add(compensationInfo);
        _workflowStep.StepMetadata["CompensationActivities"] = _sagaStepInfo.CompensationActivities;
        return this;
    }
}

/// <summary>
///     Saga activity input builder for property mapping
/// </summary>
public class SagaActivitySetupInputBuilder<TWorkflowData, TActivity, TProperty, TPreviousStepData> 
    : ISagaActivitySetupInputBuilder<TWorkflowData, TActivity, TProperty, TPreviousStepData>
    where TWorkflowData : class
    where TActivity : class, IAsyncActivity
    where TPreviousStepData : class
{
    private readonly Expression<Func<TActivity, TProperty>> _propertyExpression;
    private readonly ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> _setupBuilder;
    private readonly WorkflowStep _workflowStep;

    public SagaActivitySetupInputBuilder(
        ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> setupBuilder,
        WorkflowStep workflowStep,
        Expression<Func<TActivity, TProperty>> propertyExpression)
    {
        _setupBuilder = setupBuilder ?? throw new ArgumentNullException(nameof(setupBuilder));
        _workflowStep = workflowStep ?? throw new ArgumentNullException(nameof(workflowStep));
        _propertyExpression = propertyExpression ?? throw new ArgumentNullException(nameof(propertyExpression));
    }

    public ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> From(
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, TProperty>> source)
    {
        var propertyName = GetPropertyName(_propertyExpression);
        var sourceFunction = CompileToFunction(source);
        var propertyType = typeof(TProperty);

        var mapping = new PropertyMapping
        {
            TargetProperty = propertyName,
            SourceFunction = sourceFunction,
            Expression = Core.Runtime.WorkflowExpressionSignature.Checked(source),
            ContextFactory = Core.Runtime.WorkflowStepDelegates.Previous<TWorkflowData, TPreviousStepData>,
            SourceType = typeof(WorkflowContext<TWorkflowData, TPreviousStepData>),
            TargetType = propertyType,
            Direction = PropertyMappingDirection.Input
        };

        _workflowStep.InputMappings.Add(mapping);
        return _setupBuilder;
    }

    // Implement other inherited methods
    public ISagaActivitySetupInputBuilder<TWorkflowData, TActivity, TNewProperty, TPreviousStepData> Input<TNewProperty>(
        Expression<Func<TActivity, TNewProperty>> property)
    {
        return _setupBuilder.Input(property);
    }

    public ISagaActivitySetupOutputBuilder<TWorkflowData, TActivity, TNewProperty, TPreviousStepData> Output<TNewProperty>(
        Expression<Func<TActivity, TNewProperty>> property)
    {
        return _setupBuilder.Output(property);
    }

    public ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> OnError<TException>(
        Action<ISagaStepErrorHandler<TException>> handler) where TException : Exception
    {
        return _setupBuilder.OnError(handler);
    }

    public ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> OnError(
        Action<ISagaStepErrorHandler> handler)
    {
        return _setupBuilder.OnError(handler);
    }

    public ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>
        CompensateWith<TCompensationActivity>() where TCompensationActivity : IAsyncActivity
    {
        return _setupBuilder.CompensateWith<TCompensationActivity>();
    }

    public ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> CompensateWith<TCompensationActivity>(
        Action<ICompensationActivityBuilder<TWorkflowData, TCompensationActivity, TActivity>> configure)
        where TCompensationActivity : IAsyncActivity
    {
        return _setupBuilder.CompensateWith(configure);
    }

    // Preferred over the answer overload when a lambda fits both, as on the interface.
    [System.Runtime.CompilerServices.OverloadResolutionPriority(1)]
    public ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>
        CompensateWith<TCompensationActivity, TPreviousChainActivity>(
            Action<ICompensationActivityBuilder<TWorkflowData, TCompensationActivity, TPreviousStepData,
                TPreviousChainActivity>> configure) where TCompensationActivity : IAsyncActivity
        where TPreviousChainActivity : class, IAsyncActivity
    {
        return _setupBuilder.CompensateWith(configure);
    }

    public ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> CompensateWith<TCompensationActivity,
        TReply>(Action<IRequestCompensationActivityBuilder<TWorkflowData, TCompensationActivity, TActivity, TReply>> configure)
        where TCompensationActivity : IAsyncActivity
        where TReply : class =>
        _setupBuilder.CompensateWith<TCompensationActivity, TReply>(configure);

    private string GetPropertyName<T, TProp>(Expression<Func<T, TProp>> expression)
    {
        if (expression.Body is MemberExpression memberExpression) return memberExpression.Member.Name;
        throw new ArgumentException("Expression must be a member access", nameof(expression));
    }

    private Func<object, object?> CompileToFunction<TContext, TValue>(Expression<Func<TContext, TValue>> expression)
    {
        // Compile expression directly (same pattern as ActivitySetupBuilder)
        var compiled = expression.Compile();
        return context =>
        {
            if (context is TContext typedContext) return compiled(typedContext);
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                $"Expected {typeof(TContext).Name}, got {context.GetType().Name}");
        };
    }
}

/// <summary>
///     Saga activity output builder for property mapping
/// </summary>
public class SagaActivitySetupOutputBuilder<TWorkflowData, TActivity, TProperty, TPreviousStepData> 
    : ISagaActivitySetupOutputBuilder<TWorkflowData, TActivity, TProperty, TPreviousStepData>
    where TWorkflowData : class
    where TActivity : class, IAsyncActivity
    where TPreviousStepData : class
{
    private readonly Expression<Func<TActivity, TProperty>> _propertyExpression;
    private readonly ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> _setupBuilder;
    private readonly WorkflowStep _workflowStep;

    public SagaActivitySetupOutputBuilder(
        ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> setupBuilder,
        WorkflowStep workflowStep,
        Expression<Func<TActivity, TProperty>> propertyExpression)
    {
        _setupBuilder = setupBuilder ?? throw new ArgumentNullException(nameof(setupBuilder));
        _workflowStep = workflowStep ?? throw new ArgumentNullException(nameof(workflowStep));
        _propertyExpression = propertyExpression ?? throw new ArgumentNullException(nameof(propertyExpression));
    }

    public ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> To(
        Expression<Func<WorkflowContext<TWorkflowData>, TProperty>> destination)
    {
        var propertyName = GetPropertyName(_propertyExpression);
        var targetAssignmentFunction = CompileToAssignmentAction(destination);
        var propertyType = typeof(TProperty);

        var mapping = new PropertyMapping
        {
            TargetProperty = propertyName,
            TargetAssignmentFunction = targetAssignmentFunction,
            Expression = Core.Runtime.WorkflowExpressionSignature.Checked(destination),
            ContextFactory = Core.Runtime.WorkflowStepDelegates.Data<TWorkflowData>,
            TargetPath = Core.Runtime.MemberPath.Of(destination),
            SourceType = propertyType,
            TargetType = typeof(WorkflowContext<TWorkflowData>),
            Direction = PropertyMappingDirection.Output
        };

        _workflowStep.OutputMappings.Add(mapping);
        return _setupBuilder;
    }

    // Implement other inherited methods
    public ISagaActivitySetupInputBuilder<TWorkflowData, TActivity, TNewProperty, TPreviousStepData> Input<TNewProperty>(
        Expression<Func<TActivity, TNewProperty>> property)
    {
        return _setupBuilder.Input(property);
    }

    public ISagaActivitySetupOutputBuilder<TWorkflowData, TActivity, TNewProperty, TPreviousStepData> Output<TNewProperty>(
        Expression<Func<TActivity, TNewProperty>> property)
    {
        return _setupBuilder.Output(property);
    }

    public ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> OnError<TException>(
        Action<ISagaStepErrorHandler<TException>> handler) where TException : Exception
    {
        return _setupBuilder.OnError(handler);
    }

    public ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> OnError(
        Action<ISagaStepErrorHandler> handler)
    {
        return _setupBuilder.OnError(handler);
    }

    public ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>
        CompensateWith<TCompensationActivity>() where TCompensationActivity : IAsyncActivity
    {
        return _setupBuilder.CompensateWith<TCompensationActivity>();
    }

    public ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> CompensateWith<TCompensationActivity>(
        Action<ICompensationActivityBuilder<TWorkflowData, TCompensationActivity, TActivity>> configure)
        where TCompensationActivity : IAsyncActivity
    {
        return _setupBuilder.CompensateWith(configure);
    }

    // Preferred over the answer overload when a lambda fits both, as on the interface.
    [System.Runtime.CompilerServices.OverloadResolutionPriority(1)]
    public ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>
        CompensateWith<TCompensationActivity, TPreviousChainActivity>(
            Action<ICompensationActivityBuilder<TWorkflowData, TCompensationActivity, TPreviousStepData,
                TPreviousChainActivity>> configure) where TCompensationActivity : IAsyncActivity
        where TPreviousChainActivity : class, IAsyncActivity
    {
        return _setupBuilder.CompensateWith(configure);
    }

    public ISagaActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData> CompensateWith<TCompensationActivity,
        TReply>(Action<IRequestCompensationActivityBuilder<TWorkflowData, TCompensationActivity, TActivity, TReply>> configure)
        where TCompensationActivity : IAsyncActivity
        where TReply : class =>
        _setupBuilder.CompensateWith<TCompensationActivity, TReply>(configure);

    private string GetPropertyName<T, TProp>(Expression<Func<T, TProp>> expression)
    {
        if (expression.Body is MemberExpression memberExpression) return memberExpression.Member.Name;
        throw new ArgumentException("Expression must be a member access", nameof(expression));
    }

    private Action<object, object?> CompileToAssignmentAction<TContext, TValue>(
        Expression<Func<TContext, TValue>> expression)
    {
        // Compile assignment expression inline (same pattern as ActivitySetupBuilder)
        if (expression.Body is not MemberExpression memberExpression)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"Output destination must be a member access expression: {expression}");

        // Ensure it's a property
        if (memberExpression.Member is not PropertyInfo propertyInfo)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"Output destination must access a property: {expression}");

        // Ensure the property has a setter
        if (propertyInfo.SetMethod == null)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"Property {propertyInfo.Name} does not have a setter");

        // Create parameters for the setter expression
        var contextParameter = Expression.Parameter(typeof(TContext), "context");
        var valueParameter = Expression.Parameter(typeof(TValue), "value");

        // Create the property access expression by replacing the parameter
        var propertyAccess = Expression.Property(
            ReplaceParameterVisitor.Replace(memberExpression.Expression!, expression.Parameters[0], contextParameter),
            propertyInfo
        );

        // Create the assignment expression
        var assignment = Expression.Assign(propertyAccess, valueParameter);

        // Create and compile the lambda expression
        var lambda = Expression.Lambda<Action<TContext, TValue>>(assignment, contextParameter, valueParameter);
        var compiled = lambda.Compile();

        return (context, value) =>
        {
            if (context is TContext typedContext && value is TValue typedValue)
                compiled(typedContext, typedValue);
            else if (context is TContext typedContext2 && value == null)
                // Handle null values gracefully
                compiled(typedContext2, default!);
            else
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                    $"Expected {typeof(TContext).Name} and {typeof(TValue).Name}, got {context.GetType().Name} and {value?.GetType().Name}");
        };
    }

}

/// <summary>
/// Saga outcome builder for configuring outcome-based branching
/// </summary>
public class SagaOutcomeBuilder<TWorkflowData, TPreviousStepData, TProperty> : ISagaOutcomeBuilder<TWorkflowData, TPreviousStepData, TProperty>
    where TWorkflowData : class
    where TPreviousStepData : class
{
    private readonly WorkflowStep _outcomeStep;
    private readonly Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, TProperty>> _propertySelector;
    private readonly List<WorkflowStep> _steps;
    private readonly List<SagaStepInfo> _sagaSteps;
    private readonly List<WorkflowOutcomeBranch> _outcomeBranches = new();
    private WorkflowOutcomeBranch? _defaultBranch;

    public SagaOutcomeBuilder(
        WorkflowStep outcomeStep,
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, TProperty>> propertySelector,
        List<WorkflowStep> steps,
        List<SagaStepInfo> sagaSteps)
    {
        _outcomeStep = outcomeStep ?? throw new ArgumentNullException(nameof(outcomeStep));
        _propertySelector = propertySelector ?? throw new ArgumentNullException(nameof(propertySelector));
        _steps = steps ?? throw new ArgumentNullException(nameof(steps));
        _sagaSteps = sagaSteps ?? throw new ArgumentNullException(nameof(sagaSteps));
    }

    public ISagaOutcomeBuilder<TWorkflowData, TPreviousStepData, TProperty> Outcome(
        TProperty value,
        Action<ISagaActivityBuilder<TWorkflowData, TPreviousStepData>> configure)
    {
        // Create a sub-builder for this outcome branch
        var branchSteps = new List<WorkflowStep>();
        var branchSagaSteps = new List<SagaStepInfo>();
        var branchBuilder = new SagaActivityBuilder<TWorkflowData, TPreviousStepData>(branchSteps, branchSagaSteps);
        
        // Configure the branch
        configure(branchBuilder);

        // Create outcome branch
        var outcomeBranch = new WorkflowOutcomeBranch
        {
            Value = value,
            Steps = branchSteps,
            SagaSteps = branchSagaSteps
        };

        _outcomeBranches.Add(outcomeBranch);
        StoreOutcomeConfiguration();

        return this;
    }

    public ISagaActivityBuilder<TWorkflowData, TPreviousStepData> DefaultOutcome(
        Action<ISagaActivityBuilder<TWorkflowData, TPreviousStepData>> configure)
    {
        // Create a sub-builder for the default branch
        var branchSteps = new List<WorkflowStep>();
        var branchSagaSteps = new List<SagaStepInfo>();
        var branchBuilder = new SagaActivityBuilder<TWorkflowData, TPreviousStepData>(branchSteps, branchSagaSteps);
        
        // Configure the branch
        configure(branchBuilder);

        // Create default branch
        _defaultBranch = new WorkflowOutcomeBranch
        {
            Value = default(TProperty),
            IsDefault = true,
            Steps = branchSteps,
            SagaSteps = branchSagaSteps
        };

        // Store outcome configuration in step metadata
        StoreOutcomeConfiguration();

        // Return the original saga activity builder for continued chaining
        return new SagaActivityBuilder<TWorkflowData, TPreviousStepData>(_steps, _sagaSteps);
    }

    private void StoreOutcomeConfiguration()
    {
        // Compile the property selector for runtime evaluation
        var compiledSelector = _propertySelector.Compile();
        Func<object, object?> selectorWrapper = context =>
        {
            if (context is WorkflowContext<TWorkflowData, TPreviousStepData> typedContext)
                return compiledSelector(typedContext);
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                $"Expected WorkflowContext<{typeof(TWorkflowData).Name}, {typeof(TPreviousStepData).Name}>, got {context.GetType().Name}");
        };

        _outcomeStep.OutcomeExpression = Core.Runtime.WorkflowExpressionSignature.Checked(_propertySelector);
        _outcomeStep.OutcomeSelector = selectorWrapper;
        _outcomeStep.OutcomeBranches =
            _defaultBranch == null ? [.. _outcomeBranches] : [.. _outcomeBranches, _defaultBranch];
    }
}
