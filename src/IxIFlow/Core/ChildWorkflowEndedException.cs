namespace IxIFlow.Core;

/// <summary>
/// A child workflow ended without completing or failing on its own: it was cancelled, timed out or was
/// terminated (D-17). Raised at the parent's invocation step, where a <c>Catch</c> can handle it; never
/// while the parent itself is unwinding.
/// </summary>
public sealed class ChildWorkflowEndedException(string childInstanceId, WorkflowStatus status)
    : Exception($"Child workflow '{childInstanceId}' ended as {status}")
{
    /// <summary>The child workflow's instance ID.</summary>
    public string ChildInstanceId { get; } = childInstanceId;

    /// <summary>How the child ended: <see cref="WorkflowStatus.Cancelled"/>, <see cref="WorkflowStatus.TimedOut"/> or <see cref="WorkflowStatus.Terminated"/>.</summary>
    public WorkflowStatus Status { get; } = status;
}
