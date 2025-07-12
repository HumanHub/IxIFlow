using Microsoft.Extensions.Logging;

namespace IxIFlow.Core;

/// <summary>
///     Activity execution context providing access to workflow services and metadata
/// </summary>
public interface IActivityContext
{
    /// <summary>
    ///     Service provider for dependency injection
    /// </summary>
    IServiceProvider Services { get; }

    /// <summary>
    ///     Unique identifier for the workflow instance
    /// </summary>
    string WorkflowInstanceId { get; }

    /// <summary>
    ///     Correlation identifier for tracking
    /// </summary>
    string CorrelationId { get; }

    /// <summary>
    ///     Name of the current activity
    /// </summary>
    string ActivityName { get; }

    /// <summary>
    ///     Step number in the workflow
    /// </summary>
    int StepNumber { get; }

    /// <summary>
    ///     Name of the workflow
    /// </summary>
    string WorkflowName { get; }

    /// <summary>
    ///     Version of the workflow
    /// </summary>
    int WorkflowVersion { get; }

    /// <summary>
    ///     When the activity started executing
    /// </summary>
    DateTime ActivityStartedAt { get; }

    ///// <summary>
    /////     Additional properties for the activity
    ///// </summary>
    //IDictionary<string, object> Properties { get; }

    /// <summary>
    ///     Logger for the activity
    /// </summary>
    ILogger Logger { get; }

    /// <summary>
    ///     Workflow data (untyped - cast to your known type)
    /// </summary>
    object WorkflowData { get; }

    /// <summary>
    ///     Previous step data (untyped - cast to your known type)
    /// </summary>
    object? PreviousStepData { get; }

    /// <summary>
    ///     Step-level metadata (e.g., retry attempts, engine context)
    /// </summary>
    IDictionary<string, object> Metadata { get; }
}
