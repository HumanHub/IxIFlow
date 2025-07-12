namespace IxIFlow.Builders.Interfaces;

/// <summary>
/// Builder for defining catch blocks in try-catch structures
/// </summary>
public interface ICatchBuilder<TWorkflowData, TPreviousStepData> : IWorkflowBuilder<TWorkflowData, TPreviousStepData>
    where TPreviousStepData : class
{
    /// <summary>
    /// Adds a catch block for a specific exception type
    /// </summary>
    /// <param name="configure">Configuration for the catch block</param>
    ICatchBuilder<TWorkflowData, TPreviousStepData> Catch(
        Action<ICatchWorkflowBuilder<TWorkflowData, Exception, TPreviousStepData>> configure);
        //where Exception : Exception;


    /// <summary>
    /// Adds a catch block for a specific exception type
    /// </summary>
    /// <typeparam name="TException">Type of exception to catch</typeparam>
    /// <param name="configure">Configuration for the catch block</param>
    ICatchBuilder<TWorkflowData, TPreviousStepData> Catch<TException>(
        Action<ICatchWorkflowBuilder<TWorkflowData, TException, TPreviousStepData>> configure)
        where TException : Exception;

    /// <summary>
    /// Adds a finally block to the try-catch structure
    /// </summary>
    /// <param name="configure">Configuration for the finally block</param>
    IWorkflowBuilder<TWorkflowData, TPreviousStepData> Finally(
        Action<IWorkflowBuilder<TWorkflowData, TPreviousStepData>> configure);
}