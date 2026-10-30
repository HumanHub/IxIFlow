using System.Runtime.CompilerServices;
using IxIFlow.Core;

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
    /// <param name="id">Optional stable identifier for the constructed step.</param>
    /// <param name="sourceLine">Source line captured for the workflow step.</param>
    /// <param name="sourcePath">Source file path captured for the workflow step.</param>
    ICatchBuilder<TWorkflowData, TPreviousStepData> Catch(
        Action<ICatchWorkflowBuilder<TWorkflowData, EmptyFault, TPreviousStepData>> configure,
        string? id = null, [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    //where Exception : Exception;


    /// <summary>
    /// Adds a catch block for a specific exception type
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
    /// Adds a finally block to the try-catch structure
    /// </summary>
    /// <param name="configure">Configuration for the finally block</param>
    /// <param name="sourceLine">Source line captured for the workflow step.</param>
    /// <param name="sourcePath">Source file path captured for the workflow step.</param>
    IWorkflowBuilder<TWorkflowData, TPreviousStepData> Finally(
        Action<IWorkflowBuilder<TWorkflowData, TPreviousStepData>> configure, [CallerFilePath] string sourcePath = "",
        [CallerLineNumber] int sourceLine = 0);
}