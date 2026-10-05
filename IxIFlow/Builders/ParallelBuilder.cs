using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;

namespace IxIFlow.Builders;

/// <summary>
///     Builder for parallel execution branches with previous step data
/// </summary>
public class ParallelBuilder<TWorkflowData, TPreviousStepData> : IParallelBuilder<TWorkflowData, TPreviousStepData>
    where TWorkflowData : class
    where TPreviousStepData : class
{
    private readonly WorkflowStep _step;

    public ParallelBuilder(WorkflowStep step)
    {
        _step = step ?? throw new ArgumentNullException(nameof(step));
        _step.ParallelBranches = new List<List<WorkflowStep>>();

        _step.ParallelJoinMode = ParallelJoinMode.WaitAll;
    }

    public IParallelBuilder<TWorkflowData, TPreviousStepData> WaitAny()
    {
        SetJoinMode(ParallelJoinMode.WaitAny);
        return this;
    }

    public IParallelBuilder<TWorkflowData, TPreviousStepData> WaitConditionally(
        Func<TWorkflowData, bool> completeWhen)
    {
        ArgumentNullException.ThrowIfNull(completeWhen);
        SetJoinMode(ParallelJoinMode.WaitConditionally);
        _step.ParallelCompletionCondition = data => completeWhen((TWorkflowData)data);
        return this;
    }

    private void SetJoinMode(ParallelJoinMode mode)
    {
        if (_step.ParallelJoinMode != ParallelJoinMode.WaitAll)
            throw new InvalidOperationException("The parallel join mode has already been set");

        _step.ParallelJoinMode = mode;
    }

    /// <summary>
    ///     Adds a branch to the parallel execution
    /// </summary>
    public IParallelBuilder<TWorkflowData, TPreviousStepData> Do(
        Action<IWorkflowBuilder<TWorkflowData, TPreviousStepData>> configure)
    {
        // Create a new branch (list of steps)
        var branchSteps = new List<WorkflowStep>();

        // Create a workflow builder for this branch
        var branchBuilder = new WorkflowBuilder<TWorkflowData, TPreviousStepData>(branchSteps);

        // Configure the branch
        configure(branchBuilder);

        // Add the branch to the parallel step
        _step.ParallelBranches.Add(branchSteps);

        return this;
    }
}
