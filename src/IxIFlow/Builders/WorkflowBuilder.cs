using IxIFlow.Core.Runtime;
using System.Runtime.CompilerServices;
using System.Linq.Expressions;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;

namespace IxIFlow.Builders;

/// <summary>
///     Concrete implementation of workflow builder for root-level workflow construction
/// </summary>
public class WorkflowBuilder<TWorkflowData>(string workflowName, int workflowVersion = 1)
    : IWorkflowBuilder<TWorkflowData>
    where TWorkflowData : class
{
    public IWorkflowBuilder<TWorkflowData, object> Throw<TException>(Expression<Func<WorkflowContext<TWorkflowData, object>, TException>> value,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TException : Exception =>
        new WorkflowBuilder<TWorkflowData, object>(_steps, workflowName, workflowVersion, _demands, declaredInputs, declaredOutputs).Throw(value, sourcePath, sourceLine);
    public IWorkflowBuilder<TWorkflowData, object> Rethrow([CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) =>
        new WorkflowBuilder<TWorkflowData, object>(_steps, workflowName, workflowVersion, _demands, declaredInputs, declaredOutputs).Rethrow(sourcePath, sourceLine);

    private readonly List<WorkflowStep> _steps = new();
    public IWorkflowBuilder<TWorkflowData> RequiresScope<TValue>() where TValue : class
    {
        ScopeDeclarations.For(_steps).Add(typeof(TValue));
        return this;
    }

    private List<string>? declaredInputs;
    private List<string>? declaredOutputs;

    public IWorkflowBuilder<TWorkflowData> Input(params Expression<Func<TWorkflowData, object?>>[] members)
    {
        declaredInputs = members is { Length: 0 } ? [] : declaredInputs ?? [];
        Declare(declaredInputs, members);
        return this;
    }

    public IWorkflowBuilder<TWorkflowData> Output(params Expression<Func<TWorkflowData, object?>>[] members)
    {
        declaredOutputs = members is { Length: 0 } ? [] : declaredOutputs ?? [];
        Declare(declaredOutputs, members);
        return this;
    }

    private static void Declare(List<string> names, Expression<Func<TWorkflowData, object?>>[] members)
    {
        if (members == null) throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
            $"Workflow '{typeof(TWorkflowData).Name}' declarations require property member-access expressions");
        foreach (var expression in members)
        {
            Expression? body = expression?.Body;
            if (body is UnaryExpression { NodeType: ExpressionType.Convert } convert) body = convert.Operand;
            if (body is not MemberExpression { Member: System.Reflection.PropertyInfo, Expression: ParameterExpression parameter } member ||
                parameter != expression!.Parameters[0])
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                    $"Workflow '{typeof(TWorkflowData).Name}' declarations require a direct property member-access expression");
            if (!names.Contains(member.Member.Name, StringComparer.Ordinal)) names.Add(member.Member.Name);
        }
    }
    private readonly List<WorkflowDemand> _demands = [];

    public IWorkflowBuilder<TWorkflowData> Demand(string key, string? value = null, bool inherit = false)
    {
        var demand = new WorkflowDemand(key, value, Inherit: inherit);
        WorkflowDemands.Validate(demand);
        _demands.Add(demand);
        return this;
    }

    public IWorkflowBuilder<TWorkflowData> DemandFrom(string key,
        Expression<Func<WorkflowContext<TWorkflowData>, string?>> expression, bool inherit = false)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var evaluate = expression.Compile();
        var demand = new WorkflowDemand(key, Inherit: inherit) { Expression = expression,
            Evaluate = data => evaluate(new WorkflowContext<TWorkflowData> { WorkflowData = (TWorkflowData)data }) };
        WorkflowDemands.Validate(demand);
        _demands.Add(demand);
        return this;
    }

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

    /// <summary>Composes a fresh workflow body with private data into this definition.</summary>
    public IWorkflowBuilder<TWorkflowData, TInvokedData> Use<TWorkflow, TInvokedData>(
        Action<IWorkflowInlineSetupBuilder<TWorkflowData, TInvokedData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TWorkflow : IWorkflow<TInvokedData>, new() where TInvokedData : class =>
        new WorkflowBuilder<TWorkflowData, object>(_steps, workflowName, workflowVersion, _demands, declaredInputs, declaredOutputs)
            .Use<TWorkflow, TInvokedData>(configure == null ? null : setup => configure(setup), sourcePath, sourceLine);

    /// <summary>Composes a fresh body after verifying its exact pin.</summary>
    public IWorkflowBuilder<TWorkflowData, TInvokedData> Use<TInvokedData>(Func<WorkflowDefinition> callee, WorkflowInlinePin pin,
        Action<IWorkflowInlineSetupBuilder<TWorkflowData, TInvokedData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TInvokedData : class =>
        new WorkflowBuilder<TWorkflowData, object>(_steps, workflowName, workflowVersion, _demands, declaredInputs, declaredOutputs)
            .Use<TInvokedData>(callee, pin, configure == null ? null : setup => configure(setup), sourcePath, sourceLine);

    /// <summary>Assigns one serialized workflow-data location, retaining the previous value.</summary>
    public IWorkflowBuilder<TWorkflowData, object> Assign<TValue>(
        Expression<Func<WorkflowContext<TWorkflowData, object>, TValue>> target,
        Expression<Func<WorkflowContext<TWorkflowData, object>, TValue>> value,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) =>
        new WorkflowBuilder<TWorkflowData, object>(_steps, workflowName, workflowVersion, _demands, declaredInputs, declaredOutputs)
            .Assign(target, value, sourcePath, sourceLine);

    /// <summary>Runs cleanup if the body is abandoned by cancellation.</summary>
    public IWorkflowBuilder<TWorkflowData, object> CancellationScope(
        Action<ISequenceBuilder<TWorkflowData, object>> body,
        Action<ISequenceBuilder<TWorkflowData, object>> onCancelled,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) =>
        new WorkflowBuilder<TWorkflowData, object>(_steps, workflowName, workflowVersion, _demands, declaredInputs, declaredOutputs)
            .CancellationScope(body, onCancelled, sourcePath, sourceLine);

    /// <summary>Selects one case body with no incoming activity value.</summary>
    public IWorkflowBuilder<TWorkflowData, object> Switch<TValue>(
        Expression<Func<WorkflowContext<TWorkflowData, object>, TValue>> selector,
        Action<SwitchCases<TValue, IWorkflowBuilder<TWorkflowData, object>>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) =>
        new WorkflowBuilder<TWorkflowData, object>(_steps, workflowName, workflowVersion, _demands, declaredInputs, declaredOutputs)
            .Switch(selector, configure, sourcePath, sourceLine);

    public IWorkflowBuilder<TWorkflowData, object> Flowchart(Action<FlowchartBuilder<TWorkflowData, object>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) =>
        new WorkflowBuilder<TWorkflowData, object>(_steps, workflowName, workflowVersion, _demands)
            .Flowchart(configure, sourcePath, sourceLine);

    /// <summary>Runs a native state machine, retaining the incoming previous value.</summary>
    public IWorkflowBuilder<TWorkflowData, object> StateMachine(Action<StateMachineBuilder<TWorkflowData, object>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) =>
        new WorkflowBuilder<TWorkflowData, object>(_steps, workflowName, workflowVersion, _demands)
            .StateMachine(configure, sourcePath, sourceLine);

    public IWorkflowBuilder<TWorkflowData> Delay(TimeSpan duration, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        _steps.Add(new WorkflowStep
        {
            SourceLocation = sourcePath + ":" + sourceLine,
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
            Demands = _demands.ToList(),
            DeclaredInputs = declaredInputs?.ToArray(),
            DeclaredOutputs = declaredOutputs?.ToArray(),
            Steps = _steps,
            RequiredScopes = ScopeDeclarations.For(_steps).Distinct().ToArray()
        };
        WorkflowDefinitionValidator.Apply(definition);
        return definition;
    }

    public IWorkflowBuilder<TWorkflowData, TActivity> Step<TActivity>(
        Action<IActivitySetupBuilder<TWorkflowData, TActivity>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TActivity : class, IAsyncActivity
    {
        var step = new WorkflowStep
        {
            SourceLocation = sourcePath + ":" + sourceLine,
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
        return new WorkflowBuilder<TWorkflowData, TActivity>(_steps, workflowName, workflowVersion, _demands, declaredInputs, declaredOutputs);
    }

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    public IWorkflowBuilder<TWorkflowData, TActivity> Request<TActivity>(
        string name,
        Action<IActivitySetupBuilder<TWorkflowData, TActivity>>? configure = null,
        Expression<Func<WorkflowContext<TWorkflowData>, string>>? subjectExpression = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TActivity : class, IAsyncActivity
    {
        var result = Request<TActivity>(name, configure, subjectExpression?.Compile(), sourcePath: sourcePath, sourceLine: sourceLine);
        _steps[^1].RequestSubjectExpression = subjectExpression;
        return result;
    }

    public IWorkflowBuilder<TWorkflowData, TActivity> Request<TActivity>(string name,
        Action<IActivitySetupBuilder<TWorkflowData, TActivity>>? configure = null,
        Func<WorkflowContext<TWorkflowData>, string>? subject = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TActivity : class, IAsyncActivity
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var replyType = RequestPairing.ReplyTypeOf(typeof(TActivity));
        var next = Step(configure, sourcePath: sourcePath, sourceLine: sourceLine);
        var step = _steps[^1];
        step.RequestName = name;
        step.ReplyType = replyType;
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
    string name,
    int workflowVersion = 1, List<WorkflowDemand>? demands = null, IReadOnlyList<string>? declaredInputs = null, IReadOnlyList<string>? declaredOutputs = null)
    : IWorkflowBuilder<TWorkflowData, TPreviousStepData>
    where TWorkflowData : class
    where TPreviousStepData : class
{
    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> Throw<TException>(Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, TException>> value,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(value);
        var evaluate = value.Compile();
        var step = Core.Runtime.ThrowConstruction.Leaf<TWorkflowData, TPreviousStepData>(WorkflowStepType.Throw, sourcePath, sourceLine);
        step.ExceptionType = typeof(TException);
        step.ExceptionExpression = value;
        step.ExceptionFactoryReturnType = value.ReturnType;
        step.ExceptionFactory = context => evaluate((WorkflowContext<TWorkflowData, TPreviousStepData>)context);

        _steps.Add(step);
        return this;
    }
    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> Rethrow([CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        _steps.Add(Core.Runtime.ThrowConstruction.Leaf<TWorkflowData, TPreviousStepData>(WorkflowStepType.Rethrow, sourcePath, sourceLine));
        return this;
    }

    public IWorkflowBuilder<TWorkflowData, TScope> Scope<TScope>(
        Action<IScopeSetupBuilder<TWorkflowData, TScope, TPreviousStepData>>? setup,
        Action<IWorkflowBuilder<TWorkflowData, TScope>> body,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TScope : class, IScopeActivity
    {
        ArgumentNullException.ThrowIfNull(body);
        var step = ScopeConstruction.Create<TWorkflowData, TPreviousStepData, TScope>(_steps.Count, sourcePath, sourceLine);
        setup?.Invoke(new ScopeSetupBuilder<TWorkflowData, TScope, TPreviousStepData>(step));
        body(new WorkflowBuilder<TWorkflowData, TScope>(step.ScopeBodySteps, string.Empty));
        _steps.Add(step);
        return new WorkflowBuilder<TWorkflowData, TScope>(_steps, name, workflowVersion, _demands, declaredInputs, declaredOutputs);
    }

    protected readonly List<WorkflowStep> _steps = steps;
    internal List<WorkflowStep> Steps => _steps;
    private readonly List<WorkflowDemand> _demands = demands ?? [];

    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> Demand(string key, string? value = null, bool inherit = false)
    {
        RequireWorkflowScope();
        var demand = new WorkflowDemand(key, value, Inherit: inherit);
        WorkflowDemands.Validate(demand);
        _demands.Add(demand);
        return this;
    }

    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> DemandFrom(string key,
        Expression<Func<WorkflowContext<TWorkflowData>, string?>> expression, bool inherit = false)
    {
        RequireWorkflowScope();
        ArgumentNullException.ThrowIfNull(expression);
        var evaluate = expression.Compile();
        var demand = new WorkflowDemand(key, Inherit: inherit) { Expression = expression,
            Evaluate = data => evaluate(new WorkflowContext<TWorkflowData> { WorkflowData = (TWorkflowData)data }) };
        WorkflowDemands.Validate(demand);
        _demands.Add(demand);
        return this;
    }

    private void RequireWorkflowScope()
    {
        if (string.IsNullOrEmpty(name))
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                "Declare workflow demands on the workflow builder, outside nested steps");
    }

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

    /// <summary>Assigns one serialized workflow-data location, retaining the previous value.</summary>
    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> Assign<TValue>(
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, TValue>> target,
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, TValue>> value,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        _steps.Add(AssignmentRules.Step<TWorkflowData, TPreviousStepData>(target, value, typeof(TValue), sourcePath, sourceLine, _steps.Count));
        return this;
    }

    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> Switch<TValue>(
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, TValue>> selector,
        Action<SwitchCases<TValue, IWorkflowBuilder<TWorkflowData, TPreviousStepData>>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(configure);
        var evaluate = selector.Compile();
        var step = new WorkflowStep
        {
            StepType = WorkflowStepType.Switch, Name = "Switch", Order = _steps.Count,
            Runtime = WorkflowStepDelegates.Create<TWorkflowData, TPreviousStepData>(),
            WorkflowDataType = typeof(TWorkflowData), PreviousStepDataType = typeof(TPreviousStepData),
            OutcomeExpression = selector, OutcomeSelector = context => evaluate((WorkflowContext<TWorkflowData, TPreviousStepData>)context),
            SwitchValueType = typeof(TValue),
            SwitchEquals = (left, right) => EqualityComparer<TValue>.Default.Equals((TValue)left!, (TValue)right!),
            SourceLocation = sourcePath + ":" + sourceLine
        };
        configure(new(step, body => new WorkflowBuilder<TWorkflowData, TPreviousStepData>(body, string.Empty)));
        _steps.Add(step);
        return this;
    }

    /// <summary>Composes a fresh workflow body with private data into this definition.</summary>
    public IWorkflowBuilder<TWorkflowData, TInvokedData> Use<TWorkflow, TInvokedData>(
        Action<IWorkflowInlineSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TWorkflow : IWorkflow<TInvokedData>, new() where TInvokedData : class =>
        AddInline<TInvokedData>(InlineConstruction.Typed<TWorkflow, TInvokedData>(), typeof(TWorkflow), configure, sourcePath, sourceLine);

    /// <summary>Composes a fresh body after verifying its exact pin.</summary>
    public IWorkflowBuilder<TWorkflowData, TInvokedData> Use<TInvokedData>(Func<WorkflowDefinition> callee, WorkflowInlinePin pin,
        Action<IWorkflowInlineSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TInvokedData : class =>
        AddInline<TInvokedData>(InlineConstruction.Factory(callee, pin), null, configure, sourcePath, sourceLine);

    private IWorkflowBuilder<TWorkflowData, TInvokedData> AddInline<TInvokedData>(WorkflowDefinition callee, Type? workflowType,
        Action<IWorkflowInlineSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>>? configure, string sourcePath, int sourceLine)
        where TInvokedData : class
    {
        if (callee.WorkflowDataType != typeof(TInvokedData))
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition, "Inline workflow data type differs from the callee");
        _ = InlineConstruction.MergeDemands(_demands.Concat(callee.Demands));
        var contract = WorkflowDefinitionValidator.GetContract(callee);
        var step = new WorkflowStep
        {
            Name = callee.Name, StepType = WorkflowStepType.InlineWorkflow,
            SourceLocation = sourcePath + ":" + sourceLine,
            Runtime = WorkflowStepDelegates.Create<TWorkflowData, TPreviousStepData>(),
            WorkflowDataType = typeof(TWorkflowData), PreviousStepDataType = typeof(TPreviousStepData),
            WorkflowType = workflowType, WorkflowName = callee.Name, WorkflowVersion = callee.Version,
            InvokedDataType = typeof(TInvokedData), InlineSteps = callee.Steps,
            InlineStructureHash = WorkflowDefinitionValidator.Inspect(callee),
            InvokedContract = contract, InvokedContractHash = contract.Hash,
            InvokedContractKind = WorkflowInvocationContractKind.ExactPin,
            InlineDemands = callee.Demands.ToArray(),
            InlineBodyIdentity = new HashSet<WorkflowStep>(ParallelRules.Flatten(callee.Steps), ReferenceEqualityComparer.Instance),
            Order = _steps.Count
        };
        configure?.Invoke(new WorkflowInlineSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>(step));
        MappingRules.CheckWorkflow(step);
        _steps.Add(step);
        return new WorkflowBuilder<TWorkflowData, TInvokedData>(_steps, name, workflowVersion, _demands, declaredInputs, declaredOutputs);
    }

    /// <summary>Runs a flowchart and retains the incoming previous value.</summary>
    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> Flowchart(
        Action<FlowchartBuilder<TWorkflowData, TPreviousStepData>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var step = new WorkflowStep
        {
            Name = "Flowchart", StepType = WorkflowStepType.Flowchart,
            WorkflowDataType = typeof(TWorkflowData), PreviousStepDataType = typeof(TPreviousStepData),
            Runtime = WorkflowStepDelegates.Create<TWorkflowData, TPreviousStepData>(),
            SourceLocation = sourcePath + ":" + sourceLine, Order = _steps.Count
        };
        configure(new(step, body => new SequenceBuilder<TWorkflowData, TPreviousStepData>(body)));
        _steps.Add(step);
        return this;
    }

    /// <summary>Runs a native state machine, retaining the incoming previous value.</summary>
    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> StateMachine(
        Action<StateMachineBuilder<TWorkflowData, TPreviousStepData>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var step = new WorkflowStep
        {
            Name = "StateMachine", StepType = WorkflowStepType.StateMachine,
            WorkflowDataType = typeof(TWorkflowData), PreviousStepDataType = typeof(TPreviousStepData),
            Runtime = WorkflowStepDelegates.Create<TWorkflowData, TPreviousStepData>(),
            SourceLocation = sourcePath + ":" + sourceLine, Order = _steps.Count
        };
        configure(new(step, new StateBodies<TWorkflowData, TPreviousStepData>()));
        _steps.Add(step);
        return this;
    }

    /// <summary>Selects a destination at the end of a timeout path leaving a node.</summary>
    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> Transition(string? to,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        _steps.Add(new WorkflowStep
        {
            Name = "Transition", StepType = WorkflowStepType.Edge, Explicit = true,
            Target = to == "end" ? null : to, WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = typeof(TPreviousStepData),
            Runtime = WorkflowStepDelegates.Create<TWorkflowData, TPreviousStepData>(),
            SourceLocation = sourcePath + ":" + sourceLine, Order = _steps.Count
        });
        return this;
    }

    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> Delay(TimeSpan duration, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        _steps.Add(new WorkflowStep
        {
            SourceLocation = sourcePath + ":" + sourceLine,
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
        Action<IActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TActivity : class, IAsyncActivity
    {
        var step = new WorkflowStep
        {
            SourceLocation = sourcePath + ":" + sourceLine,
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
        return new WorkflowBuilder<TWorkflowData, TActivity>(_steps, name, workflowVersion, _demands, declaredInputs, declaredOutputs);
    }

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    public IWorkflowBuilder<TWorkflowData, TActivity> Request<TActivity>(
        string name,
        Action<IActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>>? configure = null,
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, string>>? subjectExpression = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TActivity : class, IAsyncActivity
    {
        var result = Request<TActivity>(name, configure, subjectExpression?.Compile(), sourcePath: sourcePath, sourceLine: sourceLine);
        _steps[^1].RequestSubjectExpression = subjectExpression;
        return result;
    }

    public IWorkflowBuilder<TWorkflowData, TActivity> Request<TActivity>(string requestName,
        Action<IActivitySetupBuilder<TWorkflowData, TActivity, TPreviousStepData>>? configure = null,
        Func<WorkflowContext<TWorkflowData, TPreviousStepData>, string>? subject = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TActivity : class, IAsyncActivity
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestName);
        var replyType = RequestPairing.ReplyTypeOf(typeof(TActivity));
        var next = Step(configure, sourcePath: sourcePath, sourceLine: sourceLine);
        var step = _steps[^1];
        step.RequestName = requestName;
        step.ReplyType = replyType;
        if (subject != null)
            step.RequestSubject = context => subject((WorkflowContext<TWorkflowData, TPreviousStepData>)context);
        return next;
    }

    /// <summary>
    ///     Invokes a workflow by type reference
    /// </summary>
    public IWorkflowBuilder<TWorkflowData, TInvokedData> Invoke<TWorkflow, TInvokedData>(
        Action<IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TWorkflow : IWorkflow<TInvokedData> where TInvokedData : class
    {
        var identity = WorkflowAttribute.For(typeof(TWorkflow));
        var contract = WorkflowContract.OfType(typeof(TInvokedData));
        var step = new WorkflowStep
        {
            SourceLocation = sourcePath + ":" + sourceLine,
            Runtime = Core.Runtime.WorkflowStepDelegates.Create<TWorkflowData, TPreviousStepData>(Core.Runtime.ChildWorkflowBinding.Resolve<TWorkflow, TInvokedData>),
            Id = Guid.NewGuid().ToString(),
            Name = typeof(TWorkflow).Name,
            StepType = WorkflowStepType.WorkflowInvocation,
            WorkflowType = typeof(TWorkflow),
            WorkflowName = identity.Name,
            WorkflowVersion = identity.Version,
            InvokedDataType = typeof(TInvokedData),
            InvokedContract = contract,
            InvokedContractHash = contract.Hash,
            InvokedContractKind = WorkflowInvocationContractKind.CallerView,
            WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = typeof(TPreviousStepData),
            Order = _steps.Count
        };

        // Create invocation builder and configure it
        var invocationBuilder = new WorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>(step);
        configure?.Invoke(invocationBuilder);

        _steps.Add(step);

        return new WorkflowBuilder<TWorkflowData, TInvokedData>(_steps, name, workflowVersion, _demands, declaredInputs, declaredOutputs);
    }

    /// <summary>
    ///     Invokes a workflow by name and version
    /// </summary>
    public IWorkflowBuilder<TWorkflowData, TInvokedData> Invoke<TInvokedData>(string workflowName, int version,
        Action<IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TInvokedData : class => Invoke(workflowName, version, WorkflowContract.OfType(typeof(TInvokedData)),
            WorkflowInvocationContractKind.CallerView, configure, sourcePath, sourceLine);

    public IWorkflowBuilder<TWorkflowData, TInvokedData> Invoke<TInvokedData>(string workflowName, int version, WorkflowContract contract,
        Action<IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TInvokedData : class
        => Invoke(workflowName, version, contract, WorkflowInvocationContractKind.ExactPin, configure, sourcePath, sourceLine);

    private IWorkflowBuilder<TWorkflowData, TInvokedData> Invoke<TInvokedData>(string workflowName, int version, WorkflowContract contract,
        WorkflowInvocationContractKind contractKind,
        Action<IWorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>>? configure, string sourcePath, int sourceLine)
        where TInvokedData : class
    {
        ArgumentNullException.ThrowIfNull(contract);
        var step = new WorkflowStep
        {
            SourceLocation = sourcePath + ":" + sourceLine,
            Runtime = Core.Runtime.WorkflowStepDelegates.Create<TWorkflowData, TPreviousStepData>(),
            Id = Guid.NewGuid().ToString(),
            Name = workflowName,
            StepType = WorkflowStepType.WorkflowInvocation,
            WorkflowName = workflowName,
            InvokedDataType = typeof(TInvokedData),
            InvokedContract = contract,
            InvokedContractHash = contract.Hash,
            InvokedContractKind = contractKind,
            WorkflowVersion = version,
            WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = typeof(TPreviousStepData),
            Order = _steps.Count
        };

        // Create invocation builder and configure it
        var invocationBuilder = new WorkflowInvocationSetupBuilder<TWorkflowData, TInvokedData, TPreviousStepData>(step);
        configure?.Invoke(invocationBuilder);

        _steps.Add(step);

        return new WorkflowBuilder<TWorkflowData, TInvokedData>(_steps, name, workflowVersion, _demands, declaredInputs, declaredOutputs);
    }

    /// <summary>
    ///     Creates a sequence container
    /// </summary>
    /// <summary>Runs cleanup if the body is abandoned by cancellation.</summary>
    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> CancellationScope(
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> body,
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> onCancelled,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(onCancelled);
        var step = Core.Runtime.ThrowConstruction.Leaf<TWorkflowData, TPreviousStepData>(WorkflowStepType.CancellationScope, sourcePath, sourceLine);
        step.Order = _steps.Count;
        body(new SequenceBuilder<TWorkflowData, TPreviousStepData>(step.SequenceSteps));
        onCancelled(new SequenceBuilder<TWorkflowData, TPreviousStepData>(step.CancellationSteps));
        _steps.Add(step);
        return this;
    }

    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> Sequence(
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var sequenceStep = new WorkflowStep
        {
            SourceLocation = sourcePath + ":" + sourceLine,
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
        Action<IWorkflowBuilder<TWorkflowData, TPreviousStepData>>? @else = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        // Compile the condition expression at build time for maximum performance
        var compiledCondition = condition.Compile();

        // Create wrapper function that takes object and returns bool
        Func<object, bool> conditionWrapper = context =>
            compiledCondition((WorkflowContext<TWorkflowData, TPreviousStepData>)context);

        var conditionalStep = new WorkflowStep
        {
            SourceLocation = sourcePath + ":" + sourceLine,
            Runtime = Core.Runtime.WorkflowStepDelegates.Create<TWorkflowData, TPreviousStepData>(),
            Id = Guid.NewGuid().ToString(),
            Name = "Conditional",
            StepType = WorkflowStepType.Conditional,
            WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = typeof(TPreviousStepData),
            CompiledCondition = conditionWrapper,
            ConditionExpression = condition,
            Order = _steps.Count
        };

        // Configure then branch
        var thenBuilder = new WorkflowBuilder<TWorkflowData, TPreviousStepData>(conditionalStep.ThenSteps, string.Empty);
        then(thenBuilder);

        // Configure else branch if provided
        if (@else != null)
        {
            var elseBuilder = new WorkflowBuilder<TWorkflowData, TPreviousStepData>(conditionalStep.ElseSteps, string.Empty);
            @else(elseBuilder);
        }

        _steps.Add(conditionalStep);

        return this;
    }

    /// <summary>
    ///     Creates a parallel execution block
    /// </summary>
    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> Parallel(
        Action<IParallelBuilder<TWorkflowData, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var parallelStep = new WorkflowStep
        {
            SourceLocation = sourcePath + ":" + sourceLine,
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
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var tryStep = new WorkflowStep
        {
            SourceLocation = sourcePath + ":" + sourceLine,
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
            _steps, tryStep, name, workflowVersion, _demands, declaredInputs, declaredOutputs);
    }

    /// <summary>
    ///     Creates a saga container
    /// </summary>
    public ISagaContainerBuilder<TWorkflowData, TPreviousStepData> Saga(
        Action<ISagaActivityBuilder<TWorkflowData, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var sagaStep = new WorkflowStep
        {
            SourceLocation = sourcePath + ":" + sourceLine,
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
        return new SagaContainerBuilder<TWorkflowData, TPreviousStepData>(_steps, name, workflowVersion, _demands, declaredInputs, declaredOutputs);
    }

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    public IWorkflowBuilder<TWorkflowData, TEvent> WaitFor<TEvent>(
        string key,
        Expression<Func<TEvent, WorkflowContext<TWorkflowData>, bool>>? matchesExpression = null,
        Action<ISuspendSetupBuilder<TWorkflowData, TEvent, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TEvent : class
    {
        var result = WaitFor<TEvent>(key, matchesExpression?.Compile(), configure, sourcePath: sourcePath, sourceLine: sourceLine);
        _steps[^1].ConditionExpression = matchesExpression;
        return result;
    }

    /// <summary>
    ///     Creates a suspend/resume block
    /// </summary>
    public IWorkflowBuilder<TWorkflowData, TEvent> WaitFor<TEvent>(
        string key,
        Func<TEvent, WorkflowContext<TWorkflowData>, bool>? matches = null,
        Action<ISuspendSetupBuilder<TWorkflowData, TEvent, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TEvent : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var next = Suspend(key, matches, configure, sourcePath: sourcePath, sourceLine: sourceLine);
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
        Action<ISuspendSetupBuilder<TWorkflowData, TResumeEvent, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TResumeEvent : class
    {
        var result = Suspend<TResumeEvent>(suspendReason, resumeExpression?.Compile(), configure, sourcePath: sourcePath, sourceLine: sourceLine);
        _steps[^1].ConditionExpression = resumeExpression;
        return result;
    }

    public IWorkflowBuilder<TWorkflowData, TResumeEvent> Suspend<TResumeEvent>(
        string suspendReason,
        Func<TResumeEvent, WorkflowContext<TWorkflowData>, bool>? resumeCondition = null,
        Action<ISuspendSetupBuilder<TWorkflowData, TResumeEvent, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TResumeEvent : class
    {
        var suspendStep = new WorkflowStep
        {
            SourceLocation = sourcePath + ":" + sourceLine,
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
        }

        // Configure the suspend step if a setup action is provided
        if (configure != null)
        {
            var setupBuilder = new SuspendSetupBuilder<TWorkflowData, TResumeEvent, TPreviousStepData>(suspendStep);
            configure(setupBuilder);
        }

        _steps.Add(suspendStep);

        // Return a workflow builder with TResumeEvent as the previous step type
        return new WorkflowBuilder<TWorkflowData, TResumeEvent>(_steps, name, workflowVersion, _demands, declaredInputs, declaredOutputs);
    }

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> DoWhile(
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure,
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, bool>> conditionExpression, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var result = DoWhile(configure, conditionExpression.Compile(), sourcePath: sourcePath, sourceLine: sourceLine);
        _steps[^1].ConditionExpression = conditionExpression;
        return result;
    }

    /// <summary>
    ///     Creates a do-while loop
    /// </summary>
    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> DoWhile(
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure,
        Func<WorkflowContext<TWorkflowData, TPreviousStepData>, bool> condition, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        // Create wrapper function that takes object and returns bool for maximum performance
        Func<object, bool> conditionWrapper =
            context => condition((WorkflowContext<TWorkflowData, TPreviousStepData>)context);

        var loopStep = new WorkflowStep
        {
            SourceLocation = sourcePath + ":" + sourceLine,
            Runtime = Core.Runtime.WorkflowStepDelegates.Create<TWorkflowData, TPreviousStepData>(),
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

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> WhileDo(
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, bool>> conditionExpression,
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var result = WhileDo(conditionExpression.Compile(), configure, sourcePath: sourcePath, sourceLine: sourceLine);
        _steps[^1].ConditionExpression = conditionExpression;
        return result;
    }

    /// <summary>
    ///     Creates a while-do loop
    /// </summary>
    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> WhileDo(
        Func<WorkflowContext<TWorkflowData, TPreviousStepData>, bool> condition,
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        // Create wrapper function that takes object and returns bool for maximum performance
        Func<object, bool> conditionWrapper =
            context => condition((WorkflowContext<TWorkflowData, TPreviousStepData>)context);

        var loopStep = new WorkflowStep
        {
            SourceLocation = sourcePath + ":" + sourceLine,
            Runtime = Core.Runtime.WorkflowStepDelegates.Create<TWorkflowData, TPreviousStepData>(),
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

    [OverloadResolutionPriority(-1)]
    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> ForEach<TItem>(
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, IEnumerable<TItem>?>> itemsExpression,
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>, ItemScope<TItem>> body,
        Action<IForEachSetup<TWorkflowData>>? setup = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        ArgumentNullException.ThrowIfNull(itemsExpression);
        var result = ForEach(itemsExpression.Compile(), body, setup, sourcePath: sourcePath, sourceLine: sourceLine);
        _steps[^1].ForEachItemsExpression = itemsExpression;
        return result;
    }

    [OverloadResolutionPriority(-1)]
    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> Result<TResult>(
        Expression<Func<WorkflowContext<TWorkflowData, TPreviousStepData>, TResult>> valueExpression, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        ArgumentNullException.ThrowIfNull(valueExpression);
        var result = Result(valueExpression.Compile(), sourcePath: sourcePath, sourceLine: sourceLine);
        _steps[^1].ResultExpression = valueExpression;
        return result;
    }

    public IWorkflowBuilder<TWorkflowData, TPreviousStepData> ForEach<TItem>(
        Func<WorkflowContext<TWorkflowData, TPreviousStepData>, IEnumerable<TItem>?> items,
        Action<ISequenceBuilder<TWorkflowData, TPreviousStepData>, ItemScope<TItem>> body,
        Action<IForEachSetup<TWorkflowData>>? setup = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(body);
        var step = new WorkflowStep
        {
            SourceLocation = sourcePath + ":" + sourceLine,
            Runtime = Core.Runtime.WorkflowStepDelegates.Create<TWorkflowData, TPreviousStepData>(),
            Id = Guid.NewGuid().ToString(),
            Name = "ForEach",
            StepType = WorkflowStepType.ForEach,
            WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = typeof(TPreviousStepData),
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
        Func<WorkflowContext<TWorkflowData, TPreviousStepData>, TResult> value, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        ArgumentNullException.ThrowIfNull(value);
        _steps.Add(new WorkflowStep
        {
            SourceLocation = sourcePath + ":" + sourceLine,
            Runtime = Core.Runtime.WorkflowStepDelegates.Create<TWorkflowData, TPreviousStepData>(),
            Id = Guid.NewGuid().ToString(),
            Name = "Result",
            StepType = WorkflowStepType.ForEachResult,
            WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = typeof(TPreviousStepData),
            ResultType = typeof(TResult),
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
            Demands = _demands.ToList(),
            DeclaredInputs = declaredInputs?.ToArray(),
            DeclaredOutputs = declaredOutputs?.ToArray(),
            Steps = _steps,
            RequiredScopes = ScopeDeclarations.For(_steps).Distinct().ToArray()
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
        return new SagaContainerBuilder<TWorkflowData, TPreviousStepData>(_steps, name, workflowVersion, _demands, declaredInputs, declaredOutputs);
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
