using System.Runtime.CompilerServices;
using IxIFlow.Core;

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
    /// <param name="id">Optional stable identifier for the constructed step.</param>
    /// <param name="sourceLine">Source line captured for the workflow step.</param>
    /// <param name="sourcePath">Source file path captured for the workflow step.</param>
    ICatchBuilder<TWorkflowData, TPreviousStepData> Catch<TException>(
        Action<ICatchWorkflowBuilder<TWorkflowData, EmptyFault, TPreviousStepData>> configure,
        string? id = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TException : Exception;

    ICatchBuilder<TWorkflowData, TPreviousStepData> Catch<TException, TFault>(
        Action<ICatchWorkflowBuilder<TWorkflowData, TFault, TPreviousStepData>> configure,
        string? id = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0)
        where TException : Exception;

    /// <summary>
    /// Catches all exceptions in the try-catch block
    /// </summary>
    /// <param name="configure">Configuration for the catch block</param>
    /// <param name="id">Optional stable identifier for the constructed step.</param>
    /// <param name="sourceLine">Source line captured for the workflow step.</param>
    /// <param name="sourcePath">Source file path captured for the workflow step.</param>
    ICatchBuilder<TWorkflowData, TPreviousStepData> Catch(
        Action<ICatchWorkflowBuilder<TWorkflowData, EmptyFault, TPreviousStepData>> configure,
        string? id = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0); // Catch-all

    /// <summary>
    /// Defines a finally block that executes regardless of whether an exception occurred
    /// </summary>
    /// <param name="configure">Configuration for the finally block</param>
    /// <param name="sourceLine">Source line captured for the workflow step.</param>
    /// <param name="sourcePath">Source file path captured for the workflow step.</param>
    IWorkflowBuilder<TWorkflowData, TPreviousStepData> Finally(
        Action<IWorkflowBuilder<TWorkflowData, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "",
        [CallerLineNumber] int sourceLine = 0);
}