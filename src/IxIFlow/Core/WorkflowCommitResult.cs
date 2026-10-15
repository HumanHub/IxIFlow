namespace IxIFlow.Core;

public enum WorkflowCommitStatus
{
    Applied,
    AlreadyApplied,
    Conflict
}

/// <summary>
/// A durable commit receipt. On conflict, Revision is the current stored revision.
/// </summary>
public sealed record WorkflowCommitResult(WorkflowCommitStatus Status, long Revision);
