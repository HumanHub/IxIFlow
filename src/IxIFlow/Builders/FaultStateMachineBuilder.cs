using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Builders;

/// <summary>Builds keyed states sharing the machine's incoming previous value.</summary>
public sealed class FaultStateMachineBuilder<TData, TFault, TPrevious> where TPrevious : class
{
    private readonly WorkflowStep _graph;
    private readonly FaultIStateBodies<TData, TFault, TPrevious> _body;
    internal FaultStateMachineBuilder(WorkflowStep graph, FaultIStateBodies<TData, TFault, TPrevious> body) =>
        (_graph, _body) = (graph, body);

    public FaultStateMachineBuilder<TData, TFault, TPrevious> Start(string key)
    {
        _graph.GraphStart = key;
        return this;
    }

    public FaultStateMachineBuilder<TData, TFault, TPrevious> State(string key, Action<FaultStateBuilder<TData, TFault, TPrevious>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var state = new StateMachineState { Key = key, Name = key };
        state.Steps.Add(StatePicks.Create<TData, TPrevious>(WorkflowStepType.Enter, sourcePath, sourceLine));
        configure(new(state, _body));
        if (state.Transitions.Any(t => t.Steps[0].TriggerKind == TriggerKind.Activity) &&
            state.Transitions.Any(t => t.Steps[0].TriggerKind is TriggerKind.Event or TriggerKind.Reply or TriggerKind.Timer))
            state.ListenSteps = [
                StatePicks.Create<TData, TPrevious>(WorkflowStepType.Listen, sourcePath, sourceLine),
                StatePicks.Create<TData, object>(WorkflowStepType.Fire, sourcePath, sourceLine)];
        if (!state.Final)
            state.Steps.Add(StatePicks.Create<TData, TPrevious>(WorkflowStepType.Arm, sourcePath, sourceLine));
        _graph.GraphStates.Add(state);
        return this;
    }
}

/// <summary>Declares entry, exit and ordered triggers for one state.</summary>
public sealed class FaultStateBuilder<TData, TFault, TPrevious> where TPrevious : class
{
    private readonly StateMachineState _state;
    private readonly FaultIStateBodies<TData, TFault, TPrevious> _body;
    internal FaultStateBuilder(StateMachineState state, FaultIStateBodies<TData, TFault, TPrevious> body) =>
        (_state, _body) = (state, body);

    public FaultStateBuilder<TData, TFault, TPrevious> Named(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _state.Name = name;
        return this;
    }

    public FaultStateBuilder<TData, TFault, TPrevious> Final()
    {
        _state.Final = true;
        return this;
    }

    public FaultStateBuilder<TData, TFault, TPrevious> Entry<TLast>(
        Func<ICatchWorkflowBuilder<TData, TFault, TPrevious>, ICatchWorkflowBuilder<TData, TFault, TLast>> body) where TLast : class
    {
        body(_body.Body<TPrevious>(_state.Steps));
        return this;
    }

    public FaultStateBuilder<TData, TFault, TPrevious> Exit<TLast>(
        Func<ICatchWorkflowBuilder<TData, TFault, TPrevious>, ICatchWorkflowBuilder<TData, TFault, TLast>> body,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TLast : class
    {
        if (_state.ExitSteps.Count == 0)
            _state.ExitSteps.Add(StatePicks.Create<TData, TPrevious>(WorkflowStepType.Enter, sourcePath, sourceLine));
        body(_body.Body<TPrevious>(_state.ExitSteps));
        return this;
    }

    public FaultStateBuilder<TData, TFault, TPrevious> On<TEvent>(string key,
        Action<FaultStateTransitionBuilder<TData, TFault, TPrevious, TEvent>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TEvent : class =>
        Event(TriggerKind.Event, key, configure, sourcePath, sourceLine);

    public FaultStateBuilder<TData, TFault, TPrevious> On<TEvent>(string key, string? to,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TEvent : class =>
        On<TEvent>(key, t => t.Otherwise(to, sourcePath: sourcePath, sourceLine: sourceLine), sourcePath, sourceLine);

    public FaultStateBuilder<TData, TFault, TPrevious> OnReply<TEvent>(string name,
        Action<FaultStateTransitionBuilder<TData, TFault, TPrevious, TEvent>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TEvent : class =>
        Event(TriggerKind.Reply, name, configure, sourcePath, sourceLine);

    public FaultStateBuilder<TData, TFault, TPrevious> OnReply<TEvent>(string name, string? to,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TEvent : class =>
        OnReply<TEvent>(name, t => t.Otherwise(to, sourcePath: sourcePath, sourceLine: sourceLine), sourcePath, sourceLine);

    /// <summary>Races one code activity against the state's other triggers.</summary>
    public FaultStateBuilder<TData, TFault, TPrevious> OnActivity<TActivity>(
        Action<FaultStateTransitionBuilder<TData, TFault, TPrevious, TActivity>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TActivity : class, IAsyncActivity
    {
        var trigger = StatePicks.Create<TData, TActivity>(WorkflowStepType.Trigger, sourcePath, sourceLine);
        trigger.TriggerKind = TriggerKind.Activity;
        trigger.ActivityType = typeof(TActivity);
        var transition = new StateMachineTransition { Steps = [trigger] };
        configure(new(transition, _body));
        var activity = StatePicks.Create<TData, TPrevious>(WorkflowStepType.Activity, sourcePath, sourceLine);
        activity.ActivityType = typeof(TActivity);
        activity.Name = typeof(TActivity).Name;
        activity.InputMappings = trigger.InputMappings;
        activity.OutputMappings = trigger.OutputMappings;
        transition.RunSteps = [activity, StatePicks.Create<TData, TActivity>(WorkflowStepType.Fire, sourcePath, sourceLine)];
        _state.Transitions.Add(transition);
        return this;
    }

    public FaultStateBuilder<TData, TFault, TPrevious> OnActivity<TActivity>(string? to,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TActivity : class, IAsyncActivity =>
        OnActivity<TActivity>(t => t.Otherwise(to, sourcePath: sourcePath, sourceLine: sourceLine), sourcePath, sourceLine);

    private FaultStateBuilder<TData, TFault, TPrevious> Event<TEvent>(TriggerKind kind, string key,
        Action<FaultStateTransitionBuilder<TData, TFault, TPrevious, TEvent>> configure, string path, int line) where TEvent : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var trigger = StatePicks.Create<TData, TEvent>(WorkflowStepType.Trigger, path, line);
        trigger.TriggerKind = kind;
        trigger.ResumeEventType = typeof(TEvent);
        trigger.Runtime = WorkflowStepDelegates.Wait<TData, TEvent, TPrevious>();
        trigger.StepMetadata["WaitKey"] = key;
        if (kind == TriggerKind.Reply) trigger.ReplyToRequest = key;
        Add(trigger, configure);
        return this;
    }

    public FaultStateBuilder<TData, TFault, TPrevious> After(TimeSpan duration,
        Action<FaultStateTransitionBuilder<TData, TFault, TPrevious, TPrevious>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        var trigger = StatePicks.Create<TData, TPrevious>(WorkflowStepType.Trigger, sourcePath, sourceLine);
        trigger.TriggerKind = TriggerKind.Timer;
        trigger.DelayDuration = duration;
        Add(trigger, configure);
        return this;
    }

    public FaultStateBuilder<TData, TFault, TPrevious> After(TimeSpan duration, string? to,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) =>
        After(duration, t => t.Otherwise(to, sourcePath: sourcePath, sourceLine: sourceLine), sourcePath, sourceLine);

    public FaultStateBuilder<TData, TFault, TPrevious> After(Expression<Func<FaultContext<TData, TFault, TPrevious>, TimeSpan>> afterExpression,
        Action<FaultStateTransitionBuilder<TData, TFault, TPrevious, TPrevious>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var evaluate = afterExpression.Compile();
        var trigger = StatePicks.Create<TData, TPrevious>(WorkflowStepType.Trigger, sourcePath, sourceLine);
        trigger.TriggerKind = TriggerKind.Timer;
        trigger.AfterExpression = afterExpression;
        trigger.After = context => evaluate((FaultContext<TData, TFault, TPrevious>)context);
        Add(trigger, configure);
        return this;
    }

    public FaultStateBuilder<TData, TFault, TPrevious> After(Expression<Func<FaultContext<TData, TFault, TPrevious>, TimeSpan>> afterExpression,
        string? to, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) =>
        After(afterExpression, t => t.Otherwise(to, sourcePath: sourcePath, sourceLine: sourceLine), sourcePath, sourceLine);

    /// <summary>Declares ordered alternatives of one immediate trigger.</summary>
    public FaultStateBuilder<TData, TFault, TPrevious> Immediately(
        Action<FaultStateTransitionBuilder<TData, TFault, TPrevious, TPrevious>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var trigger = StatePicks.Create<TData, TPrevious>(WorkflowStepType.Trigger, sourcePath, sourceLine);
        trigger.TriggerKind = TriggerKind.Immediate;
        Add(trigger, configure);
        return this;
    }

    public FaultStateBuilder<TData, TFault, TPrevious> When(Expression<Func<FaultContext<TData, TFault, TPrevious>, bool>> whenExpression,
        string? to, Action<ICatchWorkflowBuilder<TData, TFault, TPrevious>>? action = null,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var trigger = StatePicks.Create<TData, TPrevious>(WorkflowStepType.Trigger, sourcePath, sourceLine);
        trigger.TriggerKind = TriggerKind.Immediate;
        Add<TPrevious>(trigger, t => t.When(whenExpression, to, action, sourcePath, sourceLine));
        return this;
    }

    public FaultStateBuilder<TData, TFault, TPrevious> Otherwise(string? to,
        Action<ICatchWorkflowBuilder<TData, TFault, TPrevious>>? action = null,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var trigger = StatePicks.Create<TData, TPrevious>(WorkflowStepType.Trigger, sourcePath, sourceLine);
        trigger.TriggerKind = TriggerKind.Immediate;
        Add<TPrevious>(trigger, t => t.Otherwise(to, action, sourcePath, sourceLine));
        return this;
    }

    private void Add<TValue>(WorkflowStep trigger, Action<FaultStateTransitionBuilder<TData, TFault, TPrevious, TValue>> configure)
        where TValue : class
    {
        var transition = new StateMachineTransition { Steps = [trigger] };
        configure(new(transition, _body));
        _state.Transitions.Add(transition);
    }
}

/// <summary>Declares intake mappings and first-match guarded actions for one trigger.</summary>
public sealed class FaultStateTransitionBuilder<TData, TFault, TIncoming, TValue> where TIncoming : class where TValue : class
{
    private readonly StateMachineTransition _transition;
    private WorkflowStep Trigger => _transition.Steps[0];
    private readonly FaultIStateBodies<TData, TFault, TIncoming> _body;
    internal FaultStateTransitionBuilder(StateMachineTransition transition, FaultIStateBodies<TData, TFault, TIncoming> body) =>
        (_transition, _body) = (transition, body);

    public FaultStateTransitionBuilder<TData, TFault, TIncoming, TValue> Matches(
        Expression<Func<TValue, FaultContext<TData, TFault, TIncoming>, bool>> matchesExpression)
    {
        var evaluate = matchesExpression.Compile();
        Trigger.ConditionExpression = matchesExpression;
        Trigger.CompiledCondition = context =>
        {
            var value = (FaultEventValue<TValue>)context;
            return evaluate(value.Event, (FaultContext<TData, TFault, TIncoming>)value.Context);
        };
        return this;
    }

    public FaultStateTransitionBuilder<TData, TFault, TIncoming, TValue> DefaultPayload(TValue payload)
    {
        Trigger.DefaultPayload = payload;
        return this;
    }

    public FaultStateTriggerInput<TData, TFault, TIncoming, TValue, TProperty> Input<TProperty>(Expression<Func<TValue, TProperty>> property) =>
        new(this, Trigger, property);

    public FaultStateTriggerOutput<TData, TFault, TIncoming, TValue, TProperty> Output<TProperty>(Expression<Func<TValue, TProperty>> property) =>
        new(this, _body.Setup<TValue>(Trigger).Output(property));

    public FaultStateTransitionBuilder<TData, TFault, TIncoming, TValue> When(
        Expression<Func<FaultContext<TData, TFault, TValue>, bool>> whenExpression, string? to,
        Action<ICatchWorkflowBuilder<TData, TFault, TValue>>? action = null,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var evaluate = whenExpression.Compile();
        var edge = Add(to, action, sourcePath, sourceLine);
        edge.ConditionExpression = whenExpression;
        edge.CompiledCondition = context => evaluate((FaultContext<TData, TFault, TValue>)context);
        return this;
    }

    public FaultStateTransitionBuilder<TData, TFault, TIncoming, TValue> Otherwise(string? to,
        Action<ICatchWorkflowBuilder<TData, TFault, TValue>>? action = null,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        Add(to, action, sourcePath, sourceLine).IsDefault = true;
        return this;
    }

    private WorkflowStep Add(string? to, Action<ICatchWorkflowBuilder<TData, TFault, TValue>>? action, string path, int line)
    {
        var edge = StatePicks.Create<TData, TValue>(WorkflowStepType.Edge, path, line);
        edge.Target = to == "end" ? null : to;
        var steps = new List<WorkflowStep> { edge };
        action?.Invoke(_body.Body<TValue>(steps));
        _transition.Cases.Add(steps);
        return edge;
    }
}

public sealed class FaultStateTriggerInput<TData, TFault, TIncoming, TValue, TProperty> where TIncoming : class where TValue : class
{
    private readonly FaultStateTransitionBuilder<TData, TFault, TIncoming, TValue> _owner;
    private readonly WorkflowStep _trigger;
    private readonly Expression<Func<TValue, TProperty>> _property;
    internal FaultStateTriggerInput(FaultStateTransitionBuilder<TData, TFault, TIncoming, TValue> owner,
        WorkflowStep trigger, Expression<Func<TValue, TProperty>> property) => (_owner, _trigger, _property) = (owner, trigger, property);
    public FaultStateTransitionBuilder<TData, TFault, TIncoming, TValue> From(Expression<Func<FaultContext<TData, TFault, TIncoming>, TProperty>> source)
    {
        var evaluate = source.Compile();
        _trigger.InputMappings.Add(new PropertyMapping
        {
            TargetProperty = ((MemberExpression)_property.Body).Member.Name,
            SourceFunction = context => evaluate((FaultContext<TData, TFault, TIncoming>)context),
            Expression = source, ContextFactory = WorkflowStepDelegates.Fault<TData, TFault, TIncoming>,
            SourceType = typeof(TProperty), TargetType = typeof(TProperty), Direction = PropertyMappingDirection.Input
        });
        return _owner;
    }
}

public sealed class FaultStateTriggerOutput<TData, TFault, TIncoming, TValue, TProperty> where TIncoming : class where TValue : class
{
    private readonly FaultStateTransitionBuilder<TData, TFault, TIncoming, TValue> _owner;
    private readonly ISuspendOutputBuilder<TData, TValue, TProperty, TIncoming> _mapping;
    internal FaultStateTriggerOutput(FaultStateTransitionBuilder<TData, TFault, TIncoming, TValue> owner,
        ISuspendOutputBuilder<TData, TValue, TProperty, TIncoming> mapping) => (_owner, _mapping) = (owner, mapping);
    public FaultStateTransitionBuilder<TData, TFault, TIncoming, TValue> To(Expression<Func<WorkflowContext<TData>, TProperty>> target)
    {
        _mapping.To(target);
        return _owner;
    }
}

internal interface FaultIStateBodies<TData, TFault, TIncoming> where TIncoming : class
{
    ICatchWorkflowBuilder<TData, TFault, TValue> Body<TValue>(List<WorkflowStep> steps) where TValue : class;
    ISuspendSetupBuilder<TData, TValue, TIncoming> Setup<TValue>(WorkflowStep trigger) where TValue : class;
}
internal sealed class FaultStateBodies<TData, TFault, TIncoming>(Type exceptionType) : FaultIStateBodies<TData, TFault, TIncoming>
    where TData : class where TIncoming : class
{
    public ICatchWorkflowBuilder<TData, TFault, TValue> Body<TValue>(List<WorkflowStep> steps) where TValue : class =>
        new CatchWorkflowBuilder<TData, TFault, TValue>(steps, exceptionType);
    public ISuspendSetupBuilder<TData, TValue, TIncoming> Setup<TValue>(WorkflowStep trigger) where TValue : class =>
        new SuspendSetupBuilder<TData, TValue, TIncoming>(trigger);
}
