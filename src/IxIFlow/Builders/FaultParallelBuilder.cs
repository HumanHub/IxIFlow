using IxIFlow.Core.Runtime;
using System.Runtime.CompilerServices;
using System.Linq.Expressions;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;

namespace IxIFlow.Builders;

/// <summary>
///     Builder for parallel execution branches with previous step data
/// </summary>
internal class
    FaultParallelBuilder<TWorkflowData, TFault, TPreviousStepData> : IFaultParallelBuilder<TWorkflowData, TFault, TPreviousStepData>
    where TWorkflowData : class
    where TPreviousStepData : class
{
    private readonly WorkflowStep _step;
    private readonly Type _exceptionType;

    internal FaultParallelBuilder(WorkflowStep step, Type exceptionType)
    {
        _exceptionType = exceptionType;
        _step = step ?? throw new ArgumentNullException(nameof(step));
        _step.ParallelBranches = new List<List<WorkflowStep>>();

        _step.ParallelJoinMode = ParallelJoinMode.WaitAll;
    }

    public IFaultParallelBuilder<TWorkflowData, TFault, TPreviousStepData> WaitAny()
    {
        SetJoinMode(ParallelJoinMode.WaitAny);

        return this;
    }

    /// <summary>Retains the author expression for validation and definition identity.</summary>
    [OverloadResolutionPriority(-1)]
    public IFaultParallelBuilder<TWorkflowData, TFault, TPreviousStepData> WaitConditionally(
        Expression<Func<TWorkflowData, bool>> completeWhenExpression)
    {
        var result = WaitConditionally(completeWhenExpression.Compile());
        _step.ParallelCompletionExpression = completeWhenExpression;

        return result;
    }

    public IFaultParallelBuilder<TWorkflowData, TFault, TPreviousStepData> WaitConditionally(
        Func<TWorkflowData, bool> completeWhen)
    {
        ArgumentNullException.ThrowIfNull(completeWhen);
        SetJoinMode(ParallelJoinMode.WaitConditionally);
        _step.ParallelCompletionCondition = data => completeWhen((TWorkflowData)data);

        return this;
    }

    public IFaultParallelBuilder<TWorkflowData, TFault, TPreviousStepData> Combine<TValue, TCombiner>(
        System.Linq.Expressions.Expression<Func<TWorkflowData, TValue>> property)
        where TCombiner : ICombiner<TValue>, new()
    {
        Core.Runtime.BranchData.Choose(_step, property, typeof(TWorkflowData), typeof(TValue), typeof(TCombiner));

        return this;
    }

    private void SetJoinMode(ParallelJoinMode mode)
    {
        if (_step.ParallelJoinMode != ParallelJoinMode.WaitAll)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                "The parallel join mode has already been set");

        _step.ParallelJoinMode = mode;
    }

    /// <summary>
    ///     Adds a branch to the parallel execution
    /// </summary>
    public IFaultParallelBuilder<TWorkflowData, TFault, TPreviousStepData> Do(
        Action<ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>> configure)
    {
        // Create a new branch (list of steps)
        var branchSteps = new List<WorkflowStep>();

        // Create a workflow builder for this branch
        var branchBuilder = new CatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>(branchSteps, _exceptionType);

        // Configure the branch
        configure(branchBuilder);

        // Add the branch to the parallel step
        _step.ParallelBranches.Add(branchSteps);

        return this;
    }
}