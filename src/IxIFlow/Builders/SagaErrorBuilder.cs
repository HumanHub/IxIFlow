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

    public ISagaErrorBuilder<TWorkflowData, TFault, TPreviousStepData> Step<TActivity>()
        where TActivity : class, IAsyncActivity
    {
        steps.Add(new WorkflowStep
        {
            Runtime = Core.Runtime.WorkflowStepDelegates.Create<TWorkflowData, TPreviousStepData>().ForFault<TFault>(),
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
        Action<ICatchActivitySetupBuilder<TWorkflowData, TActivity, TFault, TPreviousStepData>> configure)
        where TActivity : class, IAsyncActivity
    {
        var step = new WorkflowStep
        {
            Runtime = Core.Runtime.WorkflowStepDelegates.Create<TWorkflowData, TPreviousStepData>().ForFault<TFault>(),
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
        Expression<Func<TEvent, WorkflowContext<TWorkflowData>, bool>>? matchesExpression = null,
        Action<ISuspendSetupBuilder<TWorkflowData, TEvent, TPreviousStepData>>? configure = null)
        where TEvent : class
    {
        var expression = matchesExpression == null ? null : WorkflowExpressionSignature.Checked(matchesExpression);
        var result = WaitFor<TEvent>(key, matchesExpression?.Compile(), configure);
        steps[^1].ConditionExpression = expression;
        return result;
    }

    public ISagaErrorBuilder<TWorkflowData, TFault, TEvent> WaitFor<TEvent>(
        string key,
        Func<TEvent, WorkflowContext<TWorkflowData>, bool>? matches = null,
        Action<ISuspendSetupBuilder<TWorkflowData, TEvent, TPreviousStepData>>? configure = null)
        where TEvent : class
    {
        new WorkflowBuilder<TWorkflowData, TPreviousStepData>(steps, string.Empty).WaitFor(key, matches, configure);
        steps[^1].Runtime = steps[^1].Runtime.ForFault<TFault>();
        return new SagaErrorBuilder<TWorkflowData, TFault, TEvent>(steps, sagaSteps, errorHandlers);
    }

}
