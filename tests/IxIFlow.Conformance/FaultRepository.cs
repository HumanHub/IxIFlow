using IxIFlow.Core;

namespace IxIFlow.Tests.Conformance;

/// <summary>
/// A profile repository with injected faults: a commit that fails before it is applied, as a host
/// dying would, or one that is applied but whose acknowledgement is lost, once.
/// </summary>
internal sealed class FaultRepository(IWorkflowStateRepository inner) : IWorkflowStateRepository
{
    public Task RegisterMemberAsync(MemberRegistration member, CancellationToken cancellationToken = default) =>
        inner.RegisterMemberAsync(member, cancellationToken);
    public Task<bool> HeartbeatAsync(Guid memberId, IReadOnlyDictionary<string, string> capabilities,
        CancellationToken cancellationToken = default) =>
        inner.HeartbeatAsync(memberId, capabilities, cancellationToken);
    public Task UnregisterMemberAsync(Guid memberId, CancellationToken cancellationToken = default) =>
        inner.UnregisterMemberAsync(memberId, cancellationToken);
    public Task<IReadOnlyList<StoreMember>> GetLiveMembersAsync(CancellationToken cancellationToken = default) =>
        inner.GetLiveMembersAsync(cancellationToken);

    public Task<RetentionResult> PurgeAsync(TimeSpan finishedRetention, TimeSpan tombstoneRetention, int batchSize,
        CancellationToken cancellationToken = default) =>
        inner.PurgeAsync(finishedRetention, tombstoneRetention, batchSize, cancellationToken);
    public Task<WorkflowTombstone?> GetTombstoneAsync(string instanceId) =>
        inner.GetTombstoneAsync(instanceId);
    public Task<WorkflowCommitResult> AdmitWorkflowInstanceAsync(CheckpointCommit commit, WorkflowAdmission admission) =>
        inner.AdmitWorkflowInstanceAsync(commit, admission);
    public Task<WorkflowOwnership?> GetWorkflowOwnershipAsync(string instanceId) =>
        inner.GetWorkflowOwnershipAsync(instanceId);
    public Task<WorkflowAdmissionRecord?> GetWorkflowAdmissionAsync(string sender, string trigger, string key) =>
        inner.GetWorkflowAdmissionAsync(sender, trigger, key);

    public Task RegisterPublisherAsync(string name, IReadOnlyCollection<string> eventTypes, bool replayRetained = false) =>
        inner.RegisterPublisherAsync(name, eventTypes, replayRetained);
    public Task UnregisterPublisherAsync(string name, string actor = "host") => inner.UnregisterPublisherAsync(name, actor);
    public Task<DeliveryPage> ListDeliveriesAsync(DeliveryQuery query) => inner.ListDeliveriesAsync(query);
    public Task<bool> RetryDeliveryAsync(string eventId, string publisherName, string actor) => inner.RetryDeliveryAsync(eventId, publisherName, actor);
    public Task<IReadOnlyList<OutboxPublisherState>> ListPublishersAsync() => inner.ListPublishersAsync();
    public Task<bool> EnablePublisherAsync(string publisherName, string actor) => inner.EnablePublisherAsync(publisherName, actor);
    public Task<IReadOnlyList<OutboxDelivery>> ClaimDeliveriesAsync(string publisherName, string token, TimeSpan leaseDuration, int limit) =>
        inner.ClaimDeliveriesAsync(publisherName, token, leaseDuration, limit);
    public Task<bool> RenewDeliveryLeaseAsync(string eventId, string publisherName, string token, TimeSpan leaseDuration) =>
        inner.RenewDeliveryLeaseAsync(eventId, publisherName, token, leaseDuration);
    public Task<bool> SettleDeliveryAsync(string eventId, string publisherName, string token, OutboxPublishResult result) =>
        inner.SettleDeliveryAsync(eventId, publisherName, token, result);
    public Task<OutboxDelivery?> GetDeliveryAsync(string eventId, string publisherName) =>
        inner.GetDeliveryAsync(eventId, publisherName);
    public Task<OutboxPublisherState?> GetPublisherAsync(string publisherName) =>
        inner.GetPublisherAsync(publisherName);

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
        // The whole instance the commit saves: the commit applied to the instance it was based on.
        var instance = commit.ReadInstance(commit.ReplacesParts ? null : await inner.GetWorkflowInstanceAsync(commit.InstanceId));
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

    public Task<WorkflowInstance?> GetWorkflowInstanceSummaryAsync(string instanceId) =>
        inner.GetWorkflowInstanceSummaryAsync(instanceId);

    public Task<WorkflowInstance?> GetWorkflowInstanceAsync(string instanceId) =>
        inner.GetWorkflowInstanceAsync(instanceId);
    public Task<InstancePage> QueryInstancesAsync(InstanceQuery query) =>
        inner.QueryInstancesAsync(query);

    public Task<WorkflowControlReceipt> RequestTerminationAsync(string instanceId, CancellationReason reason) =>
        inner.RequestTerminationAsync(instanceId, reason);
    public Task<IReadOnlyList<StoredTraceEntry>> GetTraceAsync(string instanceId, long afterSequence = 0,
        int limit = 1000) =>
        inner.GetTraceAsync(instanceId, afterSequence, limit);
    public Task DeleteWorkflowInstanceAsync(string instanceId, TimeSpan? tombstoneRetention = null) => inner.DeleteWorkflowInstanceAsync(instanceId, tombstoneRetention);
}
