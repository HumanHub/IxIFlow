using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Builders;

/// <summary>A lexical fault facet over the compensable saga builder.</summary>
public sealed class FaultSagaActivityBuilder<TData, TFault, TPrevious>(List<WorkflowStep> steps, List<SagaStepInfo> sagaSteps)
    : IFaultSagaActivityBuilder<TData, TFault, TPrevious>
    where TData : class where TPrevious : class
{
    public IFaultSagaActivityBuilder<TData, TFault, TPrevious> Throw<TException>(Expression<Func<FaultContext<TData, TFault, TPrevious>, TException>> value,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(value);
        var evaluate = value.Compile();
        var step = Core.Runtime.ThrowConstruction.Leaf<TData, TPrevious>(WorkflowStepType.Throw, sourcePath, sourceLine);
        step.ExceptionType = typeof(TException);
        step.ExceptionExpression = value;
        step.ExceptionFactoryReturnType = value.ReturnType;
        step.ExceptionFactory = context => evaluate((FaultContext<TData, TFault, TPrevious>)context);
        CatchWorkflowBuilder<TData, TFault, TPrevious>.CaptureFaults(step);
        steps.Add(step);
        return this;
    }
    public IFaultSagaActivityBuilder<TData, TFault, TPrevious> Rethrow([CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        steps.Add(Core.Runtime.ThrowConstruction.Leaf<TData, TPrevious>(WorkflowStepType.Rethrow, sourcePath, sourceLine));
        return this;
    }

    public IFaultSagaActivityBuilder<TData, TFault, TPrevious> CancellationScope(
        Action<IFaultSagaActivityBuilder<TData, TFault, TPrevious>> body,
        Action<ICatchWorkflowBuilder<TData, TFault, TPrevious>> onCancelled,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(onCancelled);
        var step = ThrowConstruction.Leaf<TData, TPrevious>(WorkflowStepType.CancellationScope, sourcePath, sourceLine);
        step.StepMetadata["IsSagaStep"] = true;
        body(new FaultSagaActivityBuilder<TData, TFault, TPrevious>(step.SequenceSteps, sagaSteps));
        onCancelled(new CatchWorkflowBuilder<TData, TFault, TPrevious>(step.CancellationSteps, typeof(Exception)));
        CatchWorkflowBuilder<TData, TFault, TPrevious>.CaptureFaults(step);
        steps.Add(step);
        return this;
    }

    private SagaActivityBuilder<TData, TPrevious> Inner => new(steps, sagaSteps);
    public IFaultSagaActivityBuilder<TData, TFault, TPrevious> WithId(string id,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        Inner.WithId(id, sourcePath, sourceLine);
        return this;
    }
    public IFaultSagaActivityBuilder<TData, TFault, TActivity> Step<TActivity>(
        Action<FaultSagaActivitySetup<TData, TFault, TActivity, TPrevious>>? configure = null,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TActivity : class, IAsyncActivity
    {
        Inner.Step<TActivity>(setup =>
        {
            var step = ((SagaActivitySetupBuilder<TData, TActivity, TPrevious>)setup).Step;
            configure?.Invoke(new(setup, step));
        }, sourcePath, sourceLine);
        CatchWorkflowBuilder<TData, TFault, TPrevious>.CaptureFaults(steps[^1]);
        return new FaultSagaActivityBuilder<TData, TFault, TActivity>(steps, sagaSteps);
    }
    public IFaultSagaActivityBuilder<TData, TFault, TActivity> Request<TActivity>(string name,
        Action<FaultSagaActivitySetup<TData, TFault, TActivity, TPrevious>>? configure = null,
        Expression<Func<FaultContext<TData, TFault, TPrevious>, string>>? subjectExpression = null,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TActivity : class, IAsyncActivity
    {
        Inner.Request<TActivity>(name, setup =>
        {
            var step = ((SagaActivitySetupBuilder<TData, TActivity, TPrevious>)setup).Step;
            configure?.Invoke(new(setup, step));
        }, sourcePath: sourcePath, sourceLine: sourceLine);
        var request = steps[^1];
        request.RequestSubjectExpression = subjectExpression;
        if (subjectExpression != null)
        {
            var evaluate = subjectExpression.Compile();
            request.RequestSubject = context => evaluate((FaultContext<TData, TFault, TPrevious>)context);
        }
        CatchWorkflowBuilder<TData, TFault, TPrevious>.CaptureFaults(request);
        return new FaultSagaActivityBuilder<TData, TFault, TActivity>(steps, sagaSteps);
    }
    public IFaultSagaActivityBuilder<TData, TFault, TEvent> WaitFor<TEvent>(string key,
        Expression<Func<TEvent, FaultContext<TData, TFault, TPrevious>, bool>>? matchesExpression = null,
        Action<IFaultSuspendSetupBuilder<TData, TFault, TEvent, TPrevious>>? configure = null,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TEvent : class
    {
        Inner.WaitFor<TEvent>(key, matchesExpression: null, configure: configure == null ? null : setup => configure(new FaultSuspendSetupBuilder<TData, TFault, TEvent, TPrevious>(((SuspendSetupBuilder<TData, TEvent, TPrevious>)setup).Step)), sourcePath: sourcePath, sourceLine: sourceLine);
        steps[^1].ConditionExpression = matchesExpression;
        if (matchesExpression != null)
        {
            var evaluate = matchesExpression.Compile();
            steps[^1].CompiledCondition = context =>
            {
                var value = (FaultEventValue<TEvent>)context;
                return evaluate(value.Event, (FaultContext<TData, TFault, TPrevious>)value.Context);
            };
        }
        CatchWorkflowBuilder<TData, TFault, TPrevious>.CaptureFaults(steps[^1]);
        return new FaultSagaActivityBuilder<TData, TFault, TEvent>(steps, sagaSteps);
    }
    public IFaultSagaActivityBuilder<TData, TFault, TEvent> Suspend<TEvent>(string suspendReason,
        Expression<Func<TEvent, FaultContext<TData, TFault, TPrevious>, bool>>? resumeExpression = null,
        Action<IFaultSuspendSetupBuilder<TData, TFault, TEvent, TPrevious>>? configure = null,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TEvent : class
    {
        Inner.Suspend<TEvent>(suspendReason, resumeExpression: null, configure: configure == null ? null : setup => configure(new FaultSuspendSetupBuilder<TData, TFault, TEvent, TPrevious>(((SuspendSetupBuilder<TData, TEvent, TPrevious>)setup).Step)), sourcePath: sourcePath, sourceLine: sourceLine);
        steps[^1].ConditionExpression = resumeExpression;
        if (resumeExpression != null)
        {
            var evaluate = resumeExpression.Compile();
            steps[^1].CompiledCondition = context =>
            {
                var value = (FaultEventValue<TEvent>)context;
                return evaluate(value.Event, (FaultContext<TData, TFault, TPrevious>)value.Context);
            };
        }
        CatchWorkflowBuilder<TData, TFault, TPrevious>.CaptureFaults(steps[^1]);
        return new FaultSagaActivityBuilder<TData, TFault, TEvent>(steps, sagaSteps);
    }
    /// <summary>Assigns one serialized workflow-data location, retaining the previous value.</summary>
    public IFaultSagaActivityBuilder<TData, TFault, TPrevious> Assign<TValue>(
        Expression<Func<FaultContext<TData, TFault, TPrevious>, TValue>> target,
        Expression<Func<FaultContext<TData, TFault, TPrevious>, TValue>> value,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        steps.Add(AssignmentRules.Step<TData, TPrevious>(target, value, typeof(TValue), sourcePath, sourceLine, steps.Count));
        CatchWorkflowBuilder<TData, TFault, TPrevious>.CaptureFaults(steps[^1]);
        return this;
    }

    public IFaultSagaActivityBuilder<TData, TFault, TPrevious> Switch<TValue>(
        Expression<Func<FaultContext<TData, TFault, TPrevious>, TValue>> selector,
        Action<SwitchCases<TValue, IFaultSagaActivityBuilder<TData, TFault, TPrevious>>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var evaluate = selector.Compile();
        var step = new WorkflowStep
        {
            Name = "Switch", StepType = WorkflowStepType.Switch,
            WorkflowDataType = typeof(TData), PreviousStepDataType = typeof(TPrevious),
            Runtime = WorkflowStepDelegates.Create<TData, TPrevious>(), SourceLocation = sourcePath + ":" + sourceLine,
            OutcomeExpression = selector, OutcomeSelector = context => evaluate((FaultContext<TData, TFault, TPrevious>)context),
            SwitchValueType = typeof(TValue), SwitchEquals = (left, right) => EqualityComparer<TValue>.Default.Equals((TValue)left!, (TValue)right!)
        };
        step.StepMetadata["IsSagaStep"] = true;
        configure(new(step, body => new FaultSagaActivityBuilder<TData, TFault, TPrevious>(body, sagaSteps)));
        CatchWorkflowBuilder<TData, TFault, TPrevious>.CaptureFaults(step);
        steps.Add(step);
        return this;
    }
}

public class FaultSagaActivitySetup<TData, TFault, TActivity, TPrevious>(
    ISagaActivitySetupBuilder<TData, TActivity, TPrevious> inner, WorkflowStep step)
    where TActivity : class, IAsyncActivity where TPrevious : class
{
    public FaultSagaInput<TData, TFault, TActivity, TPrevious, TValue> Input<TValue>(Expression<Func<TActivity, TValue>> property) => new(this, step, property);
    public FaultSagaOutput<TData, TFault, TActivity, TPrevious, TValue> Output<TValue>(Expression<Func<TActivity, TValue>> property) => new(this, inner.Output(property));
    public FaultSagaActivitySetup<TData, TFault, TActivity, TPrevious> OnError<TException>(Action<IStepErrorHandler<TException>> configure) where TException : Exception
    { inner.OnError(configure); return this; }
    public FaultSagaActivitySetup<TData, TFault, TActivity, TPrevious> OnError(Action<IStepErrorHandler> configure)
    { inner.OnError(configure); return this; }
    public FaultSagaActivitySetup<TData, TFault, TActivity, TPrevious> CompensateWith<TUndo>(
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TUndo : IAsyncActivity
    { inner.CompensateWith<TUndo>(sourcePath, sourceLine); return this; }
    public FaultSagaActivitySetup<TData, TFault, TActivity, TPrevious> CompensateWith<TUndo>(
        Action<ICompensationActivityBuilder<TData, TUndo, TActivity>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TUndo : IAsyncActivity
    { inner.CompensateWith(configure, sourcePath, sourceLine); return this; }
    [OverloadResolutionPriority(1)]
    public FaultSagaActivitySetup<TData, TFault, TActivity, TPrevious> CompensateWith<TUndo, TChain>(
        Action<ICompensationActivityBuilder<TData, TUndo, TPrevious, TChain>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TUndo : IAsyncActivity where TChain : class, IAsyncActivity
    { inner.CompensateWith(configure, sourcePath, sourceLine); return this; }
    public FaultSagaActivitySetup<TData, TFault, TActivity, TPrevious> CompensateWith<TUndo, TReply>(
        Action<IRequestCompensationActivityBuilder<TData, TUndo, TActivity, TReply>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TUndo : IAsyncActivity where TReply : class
    { inner.CompensateWith(configure, sourcePath, sourceLine); return this; }
}
public sealed class FaultSagaInput<TData, TFault, TActivity, TPrevious, TValue>(
    FaultSagaActivitySetup<TData, TFault, TActivity, TPrevious> owner, WorkflowStep step, Expression<Func<TActivity, TValue>> property)
    where TActivity : class, IAsyncActivity where TPrevious : class
{
    public FaultSagaActivitySetup<TData, TFault, TActivity, TPrevious> From(Expression<Func<FaultContext<TData, TFault, TPrevious>, TValue>> source)
    {
        var evaluate = source.Compile();
        step.InputMappings.Add(new PropertyMapping
        {
            TargetProperty = ((MemberExpression)property.Body).Member.Name, Expression = source,
            SourceFunction = context => evaluate((FaultContext<TData, TFault, TPrevious>)context),
            ContextFactory = WorkflowStepDelegates.Fault<TData, TFault, TPrevious>,
            SourceType = typeof(TValue), TargetType = typeof(TValue), Direction = PropertyMappingDirection.Input
        });
        return owner;
    }
}
public sealed class FaultSagaOutput<TData, TFault, TActivity, TPrevious, TValue>(
    FaultSagaActivitySetup<TData, TFault, TActivity, TPrevious> owner, ISagaActivitySetupOutputBuilder<TData, TActivity, TValue, TPrevious> mapping)
    where TActivity : class, IAsyncActivity where TPrevious : class
{
    public FaultSagaActivitySetup<TData, TFault, TActivity, TPrevious> To(Expression<Func<WorkflowContext<TData>, TValue>> target)
    { mapping.To(target); return owner; }
}

public sealed class FaultSagaContainerBuilder<TData, TFault, TPrevious>(List<WorkflowStep> steps, Type exceptionType)
    : CatchWorkflowBuilder<TData, TFault, TPrevious>(steps, exceptionType), IFaultSagaContainerBuilder<TData, TFault, TPrevious> where TData : class where TPrevious : class
{
    public IFaultSagaContainerBuilder<TData, TFault, TPrevious> OnError<TException, TInnerFault>(
        Action<ISagaErrorBuilder<TData, TInnerFault, TPrevious>> configure, string? id = null,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TException : Exception
    {
        new WorkflowBuilder<TData, TPrevious>(Steps, string.Empty).OnError<TException, TInnerFault>(configure, id, sourcePath, sourceLine);
        return this;
    }
    public IFaultSagaContainerBuilder<TData, TFault, TPrevious> OnError<TException>(Action<ISagaErrorBuilder<TData, EmptyFault, TPrevious>> configure,
        string? id = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) where TException : Exception =>
        OnError<TException, EmptyFault>(configure, id, sourcePath, sourceLine);
    public IFaultSagaContainerBuilder<TData, TFault, TPrevious> OnError(Action<ISagaErrorBuilder<TData, EmptyFault, TPrevious>> configure,
        string? id = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0) =>
        OnError<Exception, EmptyFault>(configure, id, sourcePath, sourceLine);
}
