namespace IxIFlow.Builders.Interfaces;

/// <summary>
/// Defines a try-catch block for error handling in workflows
/// </summary>
public interface ITryBuilder<TWorkflowData, TPreviousStepData>
    where TPreviousStepData : class
{
    /// <summary>
    /// Catches a specific exception type in the try-catch block
    /// </summary>
    /// <typeparam name="TException">Type of exception to catch</typeparam>
    /// <param name="configure">Configuration for the catch block</param>
    ICatchBuilder<TWorkflowData, TPreviousStepData> Catch<TException>(
        Action<ICatchWorkflowBuilder<TWorkflowData, TException, TPreviousStepData>> configure)
        where TException : Exception;

    /// <summary>
    /// Catches all exceptions in the try-catch block
    /// </summary>
    /// <param name="configure">Configuration for the catch block</param>
    ICatchBuilder<TWorkflowData, TPreviousStepData> Catch(
        Action<ICatchWorkflowBuilder<TWorkflowData, Exception, TPreviousStepData>> configure); // Catch-all

    /// <summary>
    /// Defines a finally block that executes regardless of whether an exception occurred
    /// </summary>
    /// <param name="configure">Configuration for the finally block</param>
    IWorkflowBuilder<TWorkflowData, TPreviousStepData> Finally(
        Action<IWorkflowBuilder<TWorkflowData, TPreviousStepData>> configure);
}