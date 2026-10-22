using IxIFlow.Core.Runtime;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;

namespace IxIFlow.Builders;

/// <summary>Appends saga error activities to their own checkpointed handler scope.</summary>
public sealed class SagaErrorBuilder<TWorkflowData, TFault, TPreviousStepData>(
    List<WorkflowStep> steps,
    List<SagaStepInfo> sagaSteps,
    List<ErrorHandler> errorHandlers)
    : ISagaErrorBuilder<TWorkflowData, TFault, TPreviousStepData>
    where TWorkflowData : class
    where TPreviousStepData : class
{
    public ISagaErrorBuilder<TWorkflowData, TFault, TPreviousStepData> Switch<TValue>(
        Expression<Func<FaultContext<TWorkflowData, TFault, TPreviousStepData>, TValue>> selector,
        Action<SwitchCases<TValue, ISagaErrorBodyBuilder<TWorkflowData, TFault, TPreviousStepData>>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        var evaluate = selector.Compile();
        var step = new WorkflowStep
        {
            Name = "Switch", StepType = WorkflowStepType.Switch,
            WorkflowDataType = typeof(TWorkflowData), PreviousStepDataType = typeof(TPreviousStepData),
            Runtime = WorkflowStepDelegates.Create<TWorkflowData, TPreviousStepData>(), SourceLocation = sourcePath + ":" + sourceLine,
            OutcomeExpression = selector, OutcomeSelector = context => evaluate((FaultContext<TWorkflowData, TFault, TPreviousStepData>)context),
            SwitchValueType = typeof(TValue), SwitchEquals = (left, right) => EqualityComparer<TValue>.Default.Equals((TValue)left!, (TValue)right!)
        };
        configure(new(step, body => new SagaErrorBodyBuilder<TWorkflowData, TFault, TPreviousStepData>(body)));
        CatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>.CaptureFaults(step);
        steps.Add(step);
        return this;
    }

    public ISagaErrorBuilder<TWorkflowData, TFault, TPreviousStepData> WithId(string id, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
    {
        Core.Runtime.WorkflowStepIdentity.Assign(steps, id, sourcePath, sourceLine);
        return this;
    }

    public ISagaContinuationBuilder<TWorkflowData, TPreviousStepData> Compensate() =>
        ContinueWith(CompensationStrategy.CompensateAll);

    public ISagaContinuationBuilder<TWorkflowData, TPreviousStepData> CompensateNone() =>
        ContinueWith(CompensationStrategy.None);

    public ISagaContinuationBuilder<TWorkflowData, TPreviousStepData> CompensateUpTo<TActivity>()
        where TActivity : IAsyncActivity =>
        ContinueWith(CompensationStrategy.CompensateUpTo, typeof(TActivity));

    // The undo is recorded on its own, so a handler with no Then... keeps it; a Then... recorded after it
    // carries it on.
    private ISagaContinuationBuilder<TWorkflowData, TPreviousStepData> ContinueWith(
        CompensationStrategy strategy, Type? target = null)
    {
        var undo = new CompensationErrorHandler
        {
            Strategy = strategy,
            CompensationTargetType = target,
            SagaSteps = sagaSteps
        };
        errorHandlers.Add(new ErrorHandler { ExceptionType = typeof(Exception), Handler = undo });
        return new SagaContinuationBuilder<TWorkflowData, TPreviousStepData>(steps, sagaSteps, errorHandlers, undo);
    }

    public ISagaErrorBuilder<TWorkflowData, TFault, TPreviousStepData> Step<TActivity>([CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TActivity : class, IAsyncActivity
    {
        steps.Add(new WorkflowStep
        {
            SourceLocation = sourcePath + ":" + sourceLine,
            Runtime = Core.Runtime.WorkflowStepDelegates.Create<TWorkflowData, TPreviousStepData>().ForFault<TFault>(),
            LexicalFaultType = typeof(TFault),
            Name = typeof(TActivity).Name,
            StepType = WorkflowStepType.Activity,
            ActivityType = typeof(TActivity),
            WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = typeof(TPreviousStepData),
            Order = steps.Count
        });
        return this;
    }

    public ISagaErrorBuilder<TWorkflowData, TFault, TActivity> Step<TActivity>(
        Action<ICatchActivitySetupBuilder<TWorkflowData, TActivity, TFault, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TActivity : class, IAsyncActivity
    {
        var step = new WorkflowStep
        {
            SourceLocation = sourcePath + ":" + sourceLine,
            Runtime = Core.Runtime.WorkflowStepDelegates.Create<TWorkflowData, TPreviousStepData>().ForFault<TFault>(),
            LexicalFaultType = typeof(TFault),
            Name = typeof(TActivity).Name,
            StepType = WorkflowStepType.Activity,
            ActivityType = typeof(TActivity),
            WorkflowDataType = typeof(TWorkflowData),
            PreviousStepDataType = typeof(TPreviousStepData),
            FaultType = typeof(TFault),
            Order = steps.Count
        };
        configure(new CatchActivitySetupBuilder<TWorkflowData, TActivity, TFault, TPreviousStepData>(step));
        steps.Add(step);
        return new SagaErrorBuilder<TWorkflowData, TFault, TActivity>(steps, sagaSteps, errorHandlers);
    }

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    public ISagaErrorBuilder<TWorkflowData, TFault, TEvent> WaitFor<TEvent>(
        string key,
        Expression<Func<TEvent, FaultContext<TWorkflowData, TFault, TPreviousStepData>, bool>>? matchesExpression = null,
        Action<IFaultSuspendSetupBuilder<TWorkflowData, TFault, TEvent, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TEvent : class
    {
        var result = WaitFor<TEvent>(key, matchesExpression?.Compile(), configure, sourcePath: sourcePath, sourceLine: sourceLine);
        steps[^1].ConditionExpression = matchesExpression;
        return result;
    }

    public ISagaErrorBuilder<TWorkflowData, TFault, TEvent> WaitFor<TEvent>(
        string key,
        Func<TEvent, FaultContext<TWorkflowData, TFault, TPreviousStepData>, bool>? matches = null,
        Action<IFaultSuspendSetupBuilder<TWorkflowData, TFault, TEvent, TPreviousStepData>>? configure = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TEvent : class
    {
        new WorkflowBuilder<TWorkflowData, TPreviousStepData>(steps, string.Empty).WaitFor<TEvent>(key, matches: null, configure: configure == null ? null : setup => configure(new FaultSuspendSetupBuilder<TWorkflowData, TFault, TEvent, TPreviousStepData>(((SuspendSetupBuilder<TWorkflowData, TEvent, TPreviousStepData>)setup).Step)), sourcePath: sourcePath, sourceLine: sourceLine);
        CatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>.CaptureFaults(steps[^1]);
        if (matches != null) steps[^1].CompiledCondition = context =>
        {
            var value = (FaultEventValue<TEvent>)context;
            return matches(value.Event, (FaultContext<TWorkflowData, TFault, TPreviousStepData>)value.Context);
        };
        return new SagaErrorBuilder<TWorkflowData, TFault, TEvent>(steps, sagaSteps, errorHandlers);
    }

}
