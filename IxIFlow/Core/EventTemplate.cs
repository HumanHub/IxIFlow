namespace IxIFlow.Core;

/// <summary>
///     Represents an event template for a suspended workflow
/// </summary>
/// <typeparam name="TEvent">The type of event</typeparam>
public class EventTemplate<TEvent> where TEvent : class
{
    /// <summary>
    ///     The workflow instance ID
    /// </summary>
    public string WorkflowInstanceId { get; set; } = "";

    /// <summary>
    ///     The workflow name
    /// </summary>
    public string WorkflowName { get; set; } = "";

    /// <summary>
    ///     The workflow version
    /// </summary>
    public int WorkflowVersion { get; set; }

    /// <summary>
    ///     The reason the workflow was suspended
    /// </summary>
    public string SuspendReason { get; set; } = "";

    /// <summary>
    ///     When the workflow was suspended
    /// </summary>
    public DateTime SuspendedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    ///     The event data
    /// </summary>
    public TEvent EventData { get; set; } = default!;

    /// <summary>
    ///     Additional metadata for the event template
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
}