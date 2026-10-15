namespace IxIFlow.Builders.Interfaces;

/// <summary>
/// Builder for defining parallel branches in the workflow
/// </summary>
public interface IParallelBuilder<TWorkflowData, TPreviousStepData>
    where TPreviousStepData : class
{
    /// <summary>
    /// Continue when the first branch completes and cancel the other branches.
    /// </summary>
    IParallelBuilder<TWorkflowData, TPreviousStepData> WaitAny();

    /// <summary>
    /// Continue when a completed branch makes the condition true, or when all branches complete.
    /// </summary>
    IParallelBuilder<TWorkflowData, TPreviousStepData> WaitConditionally(
        Func<TWorkflowData, bool> completeWhen);

    /// <summary>
    /// Adds a parallel branch to execute
    /// </summary>
    /// <param name="configure">Configuration for the parallel branch</param>
    IParallelBuilder<TWorkflowData, TPreviousStepData> Do(
        Action<IWorkflowBuilder<TWorkflowData, TPreviousStepData>> configure);
}
