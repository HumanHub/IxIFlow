namespace IxIFlow.Core.Runtime;

/// <summary>Executor-local state for one assignment, including its last acknowledged commit.</summary>
internal sealed class ExecutionSession(IExecutionHost host, Guid assignmentId, string instanceId,
    long epoch, ExecutionLimits limits, bool canPark)
{
    public Guid AssignmentId { get; } = assignmentId;
    public string InstanceId { get; } = instanceId;
    public long Epoch { get; } = epoch;
    public ExecutionLimits Limits { get; } = limits;
    public bool CanPark { get; } = canPark;
    public AcknowledgedCommit? LastAcknowledgedCommit { get; private set; }

    public async Task<CheckpointReply> CheckpointAsync(CheckpointCommit commit)
    {
        var reply = await host.CheckpointAsync(new(AssignmentId, commit));
        Check(reply.AssignmentId, reply.Conflict);
        if (reply.Kind == CheckpointReplyKind.Ack)
            LastAcknowledgedCommit = new(commit.CommitId, reply.Revision);
        return reply;
    }

    public async Task<StartChildReply> StartChildAsync(string stepId, ChildStart start, string hash)
    {
        var reply = await host.StartChildAsync(new(AssignmentId, InstanceId, Epoch, stepId, start, hash));
        Check(reply.AssignmentId, reply.Conflict);
        return reply;
    }

    public async Task<InboxPage> ReadInboxAsync(long afterSequence, int limit)
    {
        var reply = await host.ReadInboxAsync(new(AssignmentId, InstanceId, afterSequence, limit));
        Check(reply.AssignmentId, reply.Conflict);
        return reply.Page ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "Missing inbox page");
    }

    public async Task<StoredWait?> ReadWaitAsync(string waitId)
    {
        var reply = await host.ReadWaitAsync(new(AssignmentId, InstanceId, waitId));
        Check(reply.AssignmentId, reply.Conflict);
        return reply.Wait;
    }

    private void Check(Guid identity, ConflictReason? conflict)
    {
        if (identity != AssignmentId || conflict == ConflictReason.NotAssigned)
            throw new ExecutionOwnershipLostException(new InvalidOperationException("The request is not assigned"));
    }
}

internal sealed class CheckpointInterruptedException(Exception? error) : Exception(
    "The checkpoint was not acknowledged; recovery must reread the stored instance", error);
