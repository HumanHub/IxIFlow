using IxIFlow.Core;

namespace IxIFlow.Builders.Interfaces;

/// <summary>
/// Builder for configuring saga outcome branching based on property values
/// </summary>
/// <typeparam name="TWorkflowData">The workflow data type</typeparam>
/// <typeparam name="TPreviousStepData">The previous step data type</typeparam>
/// <typeparam name="TProperty">The property type used for outcome branching</typeparam>
public interface ISagaOutcomeBuilder<TWorkflowData, TPreviousStepData, TProperty>
    where TPreviousStepData : class
{
    /// <summary>
    /// Defines an outcome branch based on a specific property value
    /// </summary>
    /// <param name="value">The property value that triggers this outcome</param>
    /// <param name="configure">Configuration for the saga activities in this outcome branch</param>
    /// <returns>The saga outcome builder for chaining additional outcomes</returns>
    ISagaOutcomeBuilder<TWorkflowData, TPreviousStepData, TProperty> Outcome(
        TProperty value,
        Action<ISagaActivityBuilder<TWorkflowData, TPreviousStepData>> configure);

    /// <summary>
    /// Defines a default outcome branch that executes when no other outcomes match
    /// </summary>
    /// <param name="configure">Configuration for the saga activities in the default outcome branch</param>
    /// <returns>The saga activity builder for continuing the saga flow</returns>
    ISagaActivityBuilder<TWorkflowData, TPreviousStepData> DefaultOutcome(
        Action<ISagaActivityBuilder<TWorkflowData, TPreviousStepData>> configure);
}
