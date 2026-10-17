using IxIFlow.Core;

namespace IxIFlow.Tests.Infrastructure;

/// <summary>
/// An in-memory store that fails chosen reads or commits on demand, to test how the engine
/// treats infrastructure failures.
/// </summary>
public class FaultInjectingRepository : IWorkflowStateRepository, IWorkflowCompletionOutbox
{
    public Task<EventAppendResult> AppendEventAsync(WorkflowEvent workflowEvent) =>
        _inner.AppendEventAsync(workflowEvent);
    public Task<InboxPage> GetPendingEventsAsync(string instanceId, long afterSequence, int limit) =>
        _inner.GetPendingEventsAsync(instanceId, afterSequence, limit);
    public Task<StoredWorkflowEvent?> GetEventAsync(string instanceId, string eventId) =>
        _inner.GetEventAsync(instanceId, eventId);
    public Task<StoredWait?> GetWaitAsync(string instanceId, string waitId) =>
        _inner.GetWaitAsync(instanceId, waitId);
    public Task<IReadOnlyDictionary<string, long>> GetInboxSequencesAsync(
        IReadOnlyCollection<string> instanceIds) =>
        _inner.GetInboxSequencesAsync(instanceIds);
    public Task<IReadOnlyList<StoredWait>> GetOpenWaitsAsync(WaitQuery query) =>
        _inner.GetOpenWaitsAsync(query);

    private readonly InMemoryWorkflowStateRepository _inner;

    public FaultInjectingRepository() : this(TimeProvider.System)
    {
    }

    /// <summary>A store whose clock is <paramref name="time"/>, as a test that drives the engine's clock needs.</summary>
    public FaultInjectingRepository(TimeProvider time) => _inner = new InMemoryWorkflowStateRepository(time);
    private int _failedReads;
    private int _failedCommits;

    /// <summary>Instance reads that match fail with an IOException while this returns true.</summary>
    public Func<string, bool> FailRead { get; set; } = _ => false;

    /// <summary>Commits that match fail with an IOException while this returns true.</summary>
    public Func<WorkflowInstance, bool> FailCommit { get; set; } = _ => false;

    public int FailedReads => Volatile.Read(ref _failedReads);
    public int FailedCommits => Volatile.Read(ref _failedCommits);

    public Task<WorkflowInstance?> GetWorkflowInstanceAsync(string instanceId)
    {
        if (!FailRead(instanceId))
            return _inner.GetWorkflowInstanceAsync(instanceId);
        Interlocked.Increment(ref _failedReads);
        throw new IOException($"Injected read failure for '{instanceId}'");
    }

    public virtual Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
    {
        var instance = commit.ReadInstance();
        if (!FailCommit(instance))
            return _inner.CommitWorkflowInstanceAsync(commit);
        Interlocked.Increment(ref _failedCommits);
        throw new IOException($"Injected commit failure for '{instance.InstanceId}'");
    }

    public Task<bool> RequestCancellationAsync(string instanceId, CancellationReason reason) =>
        _inner.RequestCancellationAsync(instanceId, reason);
    public virtual Task<CancellationReason?> GetCancellationRequestAsync(string instanceId) =>
        _inner.GetCancellationRequestAsync(instanceId);
    public virtual Task<IReadOnlyDictionary<string, CancellationReason>> GetCancellationRequestsAsync(
        IReadOnlyCollection<string> instanceIds) =>
        _inner.GetCancellationRequestsAsync(instanceIds);
    public Task<IReadOnlyList<RunnableInstance>> GetRunnableInstancesAsync(int limit) =>
        _inner.GetRunnableInstancesAsync(limit);
    public Task<ExecutionLeaseGrant?> TryAcquireExecutionLeaseAsync(string instanceId, string token, TimeSpan duration) =>
        _inner.TryAcquireExecutionLeaseAsync(instanceId, token, duration);
    public virtual Task<bool> RenewExecutionLeaseAsync(string instanceId, string token, TimeSpan duration) =>
        _inner.RenewExecutionLeaseAsync(instanceId, token, duration);
    public Task<bool> ReleaseExecutionLeaseAsync(string instanceId, string token) =>
        _inner.ReleaseExecutionLeaseAsync(instanceId, token);
    public Task SaveWorkflowInstanceAsync(WorkflowInstance instance) => _inner.SaveWorkflowInstanceAsync(instance);
    public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByNameAsync(string workflowName) =>
        _inner.GetWorkflowInstancesByNameAsync(workflowName);
    public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByStatusAsync(WorkflowStatus status) =>
        _inner.GetWorkflowInstancesByStatusAsync(status);
    public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByCorrelationIdAsync(string correlationId) =>
        _inner.GetWorkflowInstancesByCorrelationIdAsync(correlationId);
    public Task DeleteWorkflowInstanceAsync(string instanceId) => _inner.DeleteWorkflowInstanceAsync(instanceId);

    public Task<WorkflowInstance?> GetUnpublishedCompletionAsync(string instanceId) =>
        _inner.GetUnpublishedCompletionAsync(instanceId);
    public Task<IEnumerable<WorkflowInstance>> GetUnpublishedCompletionsAsync() =>
        _inner.GetUnpublishedCompletionsAsync();
    public Task<IEnumerable<WorkflowInstance>> ClaimUnpublishedCompletionsAsync(
        string? instanceId, string token, TimeSpan duration) =>
        _inner.ClaimUnpublishedCompletionsAsync(instanceId, token, duration);
    public Task<bool> MarkCompletionPublishedAsync(string instanceId, long revision, string token) =>
        _inner.MarkCompletionPublishedAsync(instanceId, revision, token);
    public Task ReleaseCompletionClaimAsync(string instanceId, string token) =>
        _inner.ReleaseCompletionClaimAsync(instanceId, token);
}
