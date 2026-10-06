namespace IxIFlow.Core;

/// <summary>Reports which correlated workflows accepted an event and which still need delivery.</summary>
public sealed class WorkflowEventDeliveryException : InvalidOperationException
{
    public WorkflowEventDeliveryException(
        IReadOnlyList<string> acceptedInstanceIds, IReadOnlyList<string> pendingInstanceIds)
        : base($"The event was not accepted by {pendingInstanceIds.Count} workflow instance(s)")
    {
        AcceptedInstanceIds = acceptedInstanceIds.ToArray();
        PendingInstanceIds = pendingInstanceIds.ToArray();
    }

    public IReadOnlyList<string> AcceptedInstanceIds { get; }
    public IReadOnlyList<string> PendingInstanceIds { get; }
}
