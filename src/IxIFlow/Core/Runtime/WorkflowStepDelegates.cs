namespace IxIFlow.Core.Runtime;

internal sealed record MappingContextValues(object? Data, object? Previous = null, object? Fault = null,
    object? Reply = null, bool Answered = false, object? Event = null);

/// <summary>Typed context constructors and activity dispatch captured by builders.</summary>
internal sealed record WorkflowStepDelegates
{
    private IContexts? Factories { get; init; }

    public required Func<MappingContextValues, object> Context { get; init; }
    public required Func<MappingContextValues, object> OutputContext { get; init; }
    public Func<MappingContextValues, object>? EventContext { get; init; }
    public Func<MappingContextValues, object>? PreviousEventContext { get; init; }
    public Func<IActivityExecutor, ActivityPreparation, Task<PreparedActivity>>? Prepare { get; init; }
    public Func<IServiceProvider, WorkflowDefinition>? ChildDefinition { get; init; }

    public static WorkflowStepDelegates Create<TData>() => new()
    {
        Factories = new Contexts<TData, object>(),
        Context = Data<TData>, OutputContext = Data<TData>,
        Prepare = ActivityTransitionExecutor.Bind<TData, object>()
    };

    public static WorkflowStepDelegates Create<TData, TPrevious>(
        Func<IServiceProvider, WorkflowDefinition>? child = null) where TPrevious : class => new()
    {
        Factories = new Contexts<TData, TPrevious>(),
        Context = Previous<TData, TPrevious>, OutputContext = Data<TData>,
        Prepare = ActivityTransitionExecutor.Bind<TData, TPrevious>(), ChildDefinition = child
    };

    public static WorkflowStepDelegates Wait<TData, TEvent, TPrevious>() where TPrevious : class => new()
    {
        Factories = new Contexts<TData, TPrevious>(),
        Context = Previous<TData, TPrevious>, OutputContext = Data<TData>,
        EventContext = values => new ResumeEventContext<TData, TEvent>
        {
            WorkflowData = (TData)values.Data!, ResumeEvent = (TEvent)values.Event!
        },
        PreviousEventContext = Event<TData, TEvent, TPrevious>
    };

    public WorkflowStepDelegates ForFault<TFault>() => this with
    {
        Context = Factories!.Fault<TFault>()
    };

    private interface IContexts
    {
        Func<MappingContextValues, object> Fault<TFault>();
    }

    private sealed class Contexts<TData, TPrevious> : IContexts where TPrevious : class
    {
        public Func<MappingContextValues, object> Fault<TFault>() =>
            WorkflowStepDelegates.Fault<TData, TFault, TPrevious>;
    }

    public static object Data<TData>(MappingContextValues values) =>
        new WorkflowContext<TData> { WorkflowData = (TData)values.Data! };

    public static object Previous<TData, TPrevious>(MappingContextValues values) where TPrevious : class =>
        new WorkflowContext<TData, TPrevious>
        {
            WorkflowData = (TData)values.Data!, PreviousStep = (TPrevious)values.Previous!
        };

    public static object Event<TData, TEvent, TPrevious>(MappingContextValues values) where TPrevious : class =>
        new ResumeEventContext<TData, TEvent, TPrevious>
        {
            WorkflowData = (TData)values.Data!, PreviousStep = (TPrevious)values.Previous!,
            ResumeEvent = (TEvent)values.Event!
        };

    public static object Fault<TData, TFault, TPrevious>(MappingContextValues values) where TPrevious : class =>
        new FaultContext<TData, TFault, TPrevious>
        {
            WorkflowData = (TData)values.Data!, PreviousStep = (TPrevious)values.Previous!, Fault = values.Fault is TFault fault ? fault : default!
        };

    public static object Compensation<TData, TCurrent>(MappingContextValues values) where TCurrent : class =>
        new CompensationContext<TData, TCurrent>
        {
            WorkflowData = (TData)values.Data!, CurrentStep = (TCurrent)values.Previous!
        };

    public static object RequestCompensation<TData, TCurrent, TReply>(MappingContextValues values)
        where TCurrent : class where TReply : class => new RequestCompensationContext<TData, TCurrent, TReply>
        {
            WorkflowData = (TData)values.Data!, CurrentStep = (TCurrent)values.Previous!,
            Reply = (TReply?)values.Reply, Answered = values.Answered
        };
}
