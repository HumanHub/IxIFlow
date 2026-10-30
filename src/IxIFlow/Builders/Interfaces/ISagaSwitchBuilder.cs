using System.Runtime.CompilerServices;
using IxIFlow.Core;

namespace IxIFlow.Builders.Interfaces;

/// <summary>
/// Builder for configuring saga switch cases based on property values
/// </summary>
/// <typeparam name="TWorkflowData">The workflow data type</typeparam>
/// <typeparam name="TPreviousStepData">The previous step data type</typeparam>
/// <typeparam name="TProperty">The property type used for switch cases</typeparam>
public interface ISagaSwitchBuilder<TWorkflowData, TPreviousStepData, TProperty>
    where TPreviousStepData : class
{
    /// <summary>
    /// Defines a case body based on a specific property value
    /// </summary>
    /// <param name="value">The typed label matched by this case</param>
    /// <param name="configure">Configuration for the saga activities in this case body</param>
    /// <returns>The saga switch builder for chaining additional cases</returns>
    /// <param name="sourceLine">Source line captured for the workflow step.</param>
    /// <param name="sourcePath">Source file path captured for the workflow step.</param>
    ISagaSwitchBuilder<TWorkflowData, TPreviousStepData, TProperty> Case(
        TProperty value,
        Action<ISagaActivityBuilder<TWorkflowData, TPreviousStepData>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);

    /// <summary>
    /// Defines a default case body that executes when no case matches
    /// </summary>
    /// <param name="configure">Configuration for the saga activities in the default case body</param>
    /// <returns>The switch builder for configuring cases</returns>
    /// <param name="sourceLine">Source line captured for the workflow step.</param>
    /// <param name="sourcePath">Source file path captured for the workflow step.</param>
    ISagaSwitchBuilder<TWorkflowData, TPreviousStepData, TProperty> Default(
        Action<ISagaActivityBuilder<TWorkflowData, TPreviousStepData>> configure,
        [CallerFilePath] string sourcePath = "", [CallerLineNumber] int sourceLine = 0);
}