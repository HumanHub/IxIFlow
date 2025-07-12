namespace IxIFlow.Builders.Interfaces;

/// <summary>
/// Builder for defining parallel branches in the workflow
/// </summary>
public interface IParallelBuilder<TWorkflowData, TPreviousStepData>
    where TPreviousStepData : class
{
    /// <summary>
    /// Adds a parallel branch to execute
    /// </summary>
    /// <param name="configure">Configuration for the parallel branch</param>
    IParallelBuilder<TWorkflowData, TPreviousStepData> Do(
        Action<IWorkflowBuilder<TWorkflowData, TPreviousStepData>> configure);
}