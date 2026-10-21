using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Builders;

/// <summary>Builds keyed states sharing the machine's incoming previous value.</summary>
public sealed class StateMachineBuilder<TData, TPrevious> where TPrevious : class
{
    private readonly WorkflowStep _graph;
    private readonly IStateBodies<TData, TPrevious> _body;
    internal StateMachineBuilder(WorkflowStep graph, IStateBodies<TData, TPrevious> body) =>
        (_graph, _body) = (graph, body);

    public StateMachineBuilder<TData, TPrevious> Start(string key)
    {
        _graph.GraphStart = key;
        return this;
    }

    public StateMachineBuilder<TData, TPrevious> State(string key, Action<StateBuilder<TData, TPrevious>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var state = new StateMachineState { Key = key, Name = key };
        state.Steps.Add(StatePicks.Create<TData, TPrevious>(WorkflowStepType.Enter, sourcePath, sourceLine));
        configure(new(state, _body));
        if (!state.Final)
            state.Steps.Add(StatePicks.Create<TData, TPrevious>(WorkflowStepType.Arm, sourcePath, sourceLine));
        _graph.GraphStates.Add(state);
        return this;
    }
}

/// <summary>Declares entry, exit and ordered native triggers for one state.</summary>
public sealed class StateBuilder<TData, TPrevious> where TPrevious : class
{
    private readonly StateMachineState _state;
    private readonly IStateBodies<TData, TPrevious> _body;
    internal StateBuilder(StateMachineState state, IStateBodies<TData, TPrevious> body) =>
        (_state, _body) = (state, body);

    public StateBuilder<TData, TPrevious> Named(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _state.Name = name;
        return this;
    }

    public StateBuilder<TData, TPrevious> Final()
    {
        _state.Final = true;
        return this;
    }

    public StateBuilder<TData, TPrevious> Entry<TLast>(
        Func<ISequenceBuilder<TData, TPrevious>, IWorkflowBuilder<TData, TLast>> body) where TLast : class
    {
        body(_body.Body<TPrevious>(_state.Steps));
        return this;
    }

    public StateBuilder<TData, TPrevious> Exit<TLast>(
        Func<ISequenceBuilder<TData, TPrevious>, IWorkflowBuilder<TData, TLast>> body,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TLast : class
    {
        if (_state.ExitSteps.Count == 0)
            _state.ExitSteps.Add(StatePicks.Create<TData, TPrevious>(WorkflowStepType.Enter, sourcePath, sourceLine));
        body(_body.Body<TPrevious>(_state.ExitSteps));
        return this;
    }

    public StateBuilder<TData, TPrevious> On<TEvent>(string key,
        Action<StateTransitionBuilder<TData, TPrevious, TEvent>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TEvent : class =>
        Event(TriggerKind.Event, key, configure, sourcePath, sourceLine);

    public StateBuilder<TData, TPrevious> On<TEvent>(string key, string? to,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TEvent : class =>
        On<TEvent>(key, t => t.Otherwise(to, sourcePath: sourcePath, sourceLine: sourceLine), sourcePath, sourceLine);

    public StateBuilder<TData, TPrevious> OnReply<TEvent>(string name,
        Action<StateTransitionBuilder<TData, TPrevious, TEvent>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TEvent : class =>
        Event(TriggerKind.Reply, name, configure, sourcePath, sourceLine);

    public StateBuilder<TData, TPrevious> OnReply<TEvent>(string name, string? to,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TEvent : class =>
        OnReply<TEvent>(name, t => t.Otherwise(to, sourcePath: sourcePath, sourceLine: sourceLine), sourcePath, sourceLine);

    private StateBuilder<TData, TPrevious> Event<TEvent>(TriggerKind kind, string key,
        Action<StateTransitionBuilder<TData, TPrevious, TEvent>> configure, string path, int line) where TEvent : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var trigger = StatePicks.Create<TData, TEvent>(WorkflowStepType.Trigger, path, line);
        trigger.TriggerKind = kind;
        trigger.ResumeEventType = typeof(TEvent);
        trigger.Runtime = WorkflowStepDelegates.Wait<TData, TEvent, TEvent>();
        trigger.StepMetadata["WaitKey"] = key;
        if (kind == TriggerKind.Reply) trigger.ReplyToRequest = key;
        Add(trigger, configure);
        return this;
    }

    public StateBuilder<TData, TPrevious> After(TimeSpan duration,
        Action<StateTransitionBuilder<TData, TPrevious, TPrevious>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        var trigger = StatePicks.Create<TData, TPrevious>(WorkflowStepType.Trigger, sourcePath, sourceLine);
        trigger.TriggerKind = TriggerKind.Timer;
        trigger.DelayDuration = duration;
        Add(trigger, configure);
        return this;
    }

    public StateBuilder<TData, TPrevious> After(TimeSpan duration, string? to,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) =>
        After(duration, t => t.Otherwise(to, sourcePath: sourcePath, sourceLine: sourceLine), sourcePath, sourceLine);

    public StateBuilder<TData, TPrevious> After(Expression<Func<WorkflowContext<TData, TPrevious>, TimeSpan>> afterExpression,
        Action<StateTransitionBuilder<TData, TPrevious, TPrevious>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var evaluate = afterExpression.Compile();
        var trigger = StatePicks.Create<TData, TPrevious>(WorkflowStepType.Trigger, sourcePath, sourceLine);
        trigger.TriggerKind = TriggerKind.Timer;
        trigger.AfterExpression = afterExpression;
        trigger.After = context => evaluate((WorkflowContext<TData, TPrevious>)context);
        Add(trigger, configure);
        return this;
    }

    public StateBuilder<TData, TPrevious> After(Expression<Func<WorkflowContext<TData, TPrevious>, TimeSpan>> afterExpression,
        string? to, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) =>
        After(afterExpression, t => t.Otherwise(to, sourcePath: sourcePath, sourceLine: sourceLine), sourcePath, sourceLine);

    public StateBuilder<TData, TPrevious> When(Expression<Func<WorkflowContext<TData, TPrevious>, bool>> whenExpression,
        string? to, Action<ISequenceBuilder<TData, TPrevious>>? action = null,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var trigger = StatePicks.Create<TData, TPrevious>(WorkflowStepType.Trigger, sourcePath, sourceLine);
        trigger.TriggerKind = TriggerKind.Immediate;
        Add<TPrevious>(trigger, t => t.When(whenExpression, to, action, sourcePath, sourceLine));
        return this;
    }

    public StateBuilder<TData, TPrevious> Otherwise(string? to,
        Action<ISequenceBuilder<TData, TPrevious>>? action = null,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var trigger = StatePicks.Create<TData, TPrevious>(WorkflowStepType.Trigger, sourcePath, sourceLine);
        trigger.TriggerKind = TriggerKind.Immediate;
        Add<TPrevious>(trigger, t => t.Otherwise(to, action, sourcePath, sourceLine));
        return this;
    }

    private void Add<TValue>(WorkflowStep trigger, Action<StateTransitionBuilder<TData, TPrevious, TValue>> configure)
        where TValue : class
    {
        var transition = new StateMachineTransition { Steps = [trigger] };
        configure(new(transition, _body));
        _state.Transitions.Add(transition);
    }
}

/// <summary>Declares intake mappings and first-match guarded actions for one trigger.</summary>
public sealed class StateTransitionBuilder<TData, TIncoming, TValue> where TIncoming : class where TValue : class
{
    private readonly StateMachineTransition _transition;
    private WorkflowStep Trigger => _transition.Steps[0];
    private readonly IStateBodies<TData, TIncoming> _body;
    internal StateTransitionBuilder(StateMachineTransition transition, IStateBodies<TData, TIncoming> body) =>
        (_transition, _body) = (transition, body);

    public StateTransitionBuilder<TData, TIncoming, TValue> Matches(
        Expression<Func<TValue, WorkflowContext<TData>, bool>> matchesExpression)
    {
        var evaluate = matchesExpression.Compile();
        Trigger.ConditionExpression = matchesExpression;
        Trigger.CompiledCondition = context =>
        {
            var resume = (ResumeEventContext<TData, TValue>)context;
            return evaluate(resume.ResumeEvent, new WorkflowContext<TData> { WorkflowData = resume.WorkflowData, Items = resume.Items });
        };
        return this;
    }

    public StateTransitionBuilder<TData, TIncoming, TValue> DefaultPayload(TValue payload)
    {
        Trigger.DefaultPayload = payload;
        return this;
    }

    public StateTriggerInput<TData, TIncoming, TValue, TProperty> Input<TProperty>(Expression<Func<TValue, TProperty>> property) =>
        new(this, _body.Setup<TValue>(Trigger).Input(property));

    public StateTriggerOutput<TData, TIncoming, TValue, TProperty> Output<TProperty>(Expression<Func<TValue, TProperty>> property) =>
        new(this, _body.Setup<TValue>(Trigger).Output(property));

    public StateTransitionBuilder<TData, TIncoming, TValue> When(
        Expression<Func<WorkflowContext<TData, TValue>, bool>> whenExpression, string? to,
        Action<ISequenceBuilder<TData, TValue>>? action = null,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var evaluate = whenExpression.Compile();
        var edge = Add(to, action, sourcePath, sourceLine);
        edge.ConditionExpression = whenExpression;
        edge.CompiledCondition = context => evaluate((WorkflowContext<TData, TValue>)context);
        return this;
    }

    public StateTransitionBuilder<TData, TIncoming, TValue> Otherwise(string? to,
        Action<ISequenceBuilder<TData, TValue>>? action = null,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        Add(to, action, sourcePath, sourceLine).IsDefault = true;
        return this;
    }

    private WorkflowStep Add(string? to, Action<ISequenceBuilder<TData, TValue>>? action, string path, int line)
    {
        var edge = StatePicks.Create<TData, TValue>(WorkflowStepType.Edge, path, line);
        edge.Target = to == "end" ? null : to;
        var steps = new List<WorkflowStep> { edge };
        action?.Invoke(_body.Body<TValue>(steps));
        _transition.Cases.Add(steps);
        return edge;
    }
}

public sealed class StateTriggerInput<TData, TIncoming, TValue, TProperty> where TIncoming : class where TValue : class
{
    private readonly StateTransitionBuilder<TData, TIncoming, TValue> _owner;
    private readonly ISuspendInputBuilder<TData, TValue, TProperty, TIncoming> _mapping;
    internal StateTriggerInput(StateTransitionBuilder<TData, TIncoming, TValue> owner,
        ISuspendInputBuilder<TData, TValue, TProperty, TIncoming> mapping) => (_owner, _mapping) = (owner, mapping);
    public StateTransitionBuilder<TData, TIncoming, TValue> From(Expression<Func<WorkflowContext<TData, TIncoming>, TProperty>> source)
    {
        _mapping.From(source);
        return _owner;
    }
}

public sealed class StateTriggerOutput<TData, TIncoming, TValue, TProperty> where TIncoming : class where TValue : class
{
    private readonly StateTransitionBuilder<TData, TIncoming, TValue> _owner;
    private readonly ISuspendOutputBuilder<TData, TValue, TProperty, TIncoming> _mapping;
    internal StateTriggerOutput(StateTransitionBuilder<TData, TIncoming, TValue> owner,
        ISuspendOutputBuilder<TData, TValue, TProperty, TIncoming> mapping) => (_owner, _mapping) = (owner, mapping);
    public StateTransitionBuilder<TData, TIncoming, TValue> To(Expression<Func<WorkflowContext<TData>, TProperty>> target)
    {
        _mapping.To(target);
        return _owner;
    }
}

internal static class StatePicks
{
    public static WorkflowStep Create<TData, TPrevious>(WorkflowStepType kind, string path, int line) where TPrevious : class => new()
    {
        StepType = kind, Name = kind.ToString(), SourceLocation = path + ":" + line,
        WorkflowDataType = typeof(TData), PreviousStepDataType = typeof(TPrevious),
        Runtime = WorkflowStepDelegates.Create<TData, TPrevious>()
    };
}

internal interface IStateBodies<TData, TIncoming> where TIncoming : class
{
    ISequenceBuilder<TData, TValue> Body<TValue>(List<WorkflowStep> steps) where TValue : class;
    ISuspendSetupBuilder<TData, TValue, TIncoming> Setup<TValue>(WorkflowStep trigger) where TValue : class;
}

internal sealed class StateBodies<TData, TIncoming> : IStateBodies<TData, TIncoming>
    where TData : class where TIncoming : class
{
    public ISequenceBuilder<TData, TValue> Body<TValue>(List<WorkflowStep> steps) where TValue : class =>
        new SequenceBuilder<TData, TValue>(steps);
    public ISuspendSetupBuilder<TData, TValue, TIncoming> Setup<TValue>(WorkflowStep trigger) where TValue : class =>
        new SuspendSetupBuilder<TData, TValue, TIncoming>(trigger);
}
