using IxIFlow.Core;

namespace IxIFlow.Tests.Conformance;

/// <summary>
/// A profile repository with injected faults: a commit that fails before it is applied, as a host
/// dying would, or one that is applied but whose acknowledgement is lost, once.
/// </summary>
internal sealed class FaultRepository(IWorkflowStateRepository inner) : IWorkflowStateRepository
{
    public Task<EventAppendResult> AppendEventAsync(WorkflowEvent workflowEvent) =>
        inner.AppendEventAsync(workflowEvent);
    public Task<InboxPage> GetPendingEventsAsync(string instanceId, long afterSequence, int limit) =>
        inner.GetPendingEventsAsync(instanceId, afterSequence, limit);
    public Task<StoredWorkflowEvent?> GetEventAsync(string instanceId, string eventId) =>
        inner.GetEventAsync(instanceId, eventId);
    public Task<StoredWait?> GetWaitAsync(string instanceId, string waitId) =>
        inner.GetWaitAsync(instanceId, waitId);
    public Task<IReadOnlyDictionary<string, long>> GetInboxSequencesAsync(
        IReadOnlyCollection<string> instanceIds) =>
        inner.GetInboxSequencesAsync(instanceIds);
    public Task<IReadOnlyList<StoredWait>> GetOpenWaitsAsync(WaitQuery query) =>
        inner.GetOpenWaitsAsync(query);

    private int _acknowledgementsLost;

    /// <summary>Commits matching this fail without being applied.</summary>
    public Func<WorkflowInstance, bool> FailCommit { get; set; } = _ => false;

    /// <summary>The first commit matching this is applied, then reported as failed.</summary>
    public Func<WorkflowInstance, bool> LoseAcknowledgement { get; init; } = _ => false;

    public async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
    {
        var instance = commit.ReadInstance();
        if (FailCommit(instance))
            throw new IOException($"Injected commit failure for '{instance.InstanceId}'");
        var result = await inner.CommitWorkflowInstanceAsync(commit);
        if (result.Status == WorkflowCommitStatus.Applied && LoseAcknowledgement(instance) &&
            Interlocked.Exchange(ref _acknowledgementsLost, 1) == 0)
            throw new IOException($"Injected lost acknowledgement for '{instance.InstanceId}'");
        return result;
    }

    public Task<bool> RequestCancellationAsync(string instanceId, CancellationReason reason) =>
        inner.RequestCancellationAsync(instanceId, reason);
    public Task<CancellationReason?> GetCancellationRequestAsync(string instanceId) =>
        inner.GetCancellationRequestAsync(instanceId);
    public Task<IReadOnlyDictionary<string, CancellationReason>> GetCancellationRequestsAsync(
        IReadOnlyCollection<string> instanceIds) => inner.GetCancellationRequestsAsync(instanceIds);
    public Task<IReadOnlyList<RunnableInstance>> GetRunnableInstancesAsync(int limit) =>
        inner.GetRunnableInstancesAsync(limit);
    public Task<ExecutionLeaseGrant?> TryAcquireExecutionLeaseAsync(string instanceId, string token,
        TimeSpan duration) => inner.TryAcquireExecutionLeaseAsync(instanceId, token, duration);
    public Task<bool> RenewExecutionLeaseAsync(string instanceId, string token, TimeSpan duration) =>
        inner.RenewExecutionLeaseAsync(instanceId, token, duration);
    public Task<bool> ReleaseExecutionLeaseAsync(string instanceId, string token) =>
        inner.ReleaseExecutionLeaseAsync(instanceId, token);
    public Task MakeRunnableAsync(string instanceId) => inner.MakeRunnableAsync(instanceId);
    public Task SaveWorkflowInstanceAsync(WorkflowInstance instance) => inner.SaveWorkflowInstanceAsync(instance);
    public Task<WorkflowInstance?> GetWorkflowInstanceAsync(string instanceId) =>
        inner.GetWorkflowInstanceAsync(instanceId);
    public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByNameAsync(string workflowName) =>
        inner.GetWorkflowInstancesByNameAsync(workflowName);
    public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByStatusAsync(WorkflowStatus status) =>
        inner.GetWorkflowInstancesByStatusAsync(status);
    public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByCorrelationIdAsync(string correlationId) =>
        inner.GetWorkflowInstancesByCorrelationIdAsync(correlationId);
    public Task DeleteWorkflowInstanceAsync(string instanceId) => inner.DeleteWorkflowInstanceAsync(instanceId);
}
