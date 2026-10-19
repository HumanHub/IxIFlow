using IxIFlow.Core;

namespace IxIFlow.Tests.Infrastructure;

/// <summary>
/// An in-memory store that fails chosen reads or commits on demand, to test how the engine
/// treats infrastructure failures.
/// </summary>
public class FaultInjectingRepository : IWorkflowStateRepository
{
    public Task<WorkflowDraft?> GetDefinitionAsync(string name, int version) =>
        _inner.GetDefinitionAsync(name, version);
    public Task<WorkflowDraft> SaveDefinitionAsync(WorkflowDraft definition, long expectedRevision) =>
        _inner.SaveDefinitionAsync(definition, expectedRevision);
    public Task RegisterWorkflowVersionAsync(WorkflowVersion version, IArtifactStore? artifacts = null) =>
        _inner.RegisterWorkflowVersionAsync(version, artifacts);
    public Task<WorkflowVersion?> GetWorkflowVersionAsync(string name, int version) =>
        _inner.GetWorkflowVersionAsync(name, version);
    public Task<IReadOnlyList<WorkflowVersion>> GetWorkflowVersionsAsync(string? name = null) =>
        _inner.GetWorkflowVersionsAsync(name);
    public Task SetWorkflowVersionStateAsync(string name, int version, WorkflowVersionState state, IArtifactStore? artifacts = null) =>
        _inner.SetWorkflowVersionStateAsync(name, version, state, artifacts);
    public Task<WorkflowVersion> PublishDefinitionAsync(WorkflowDraftId draftId, WorkflowVersion version) =>
        _inner.PublishDefinitionAsync(draftId, version);
    public Task<int> PurgeArtifactsAsync(IArtifactStore artifacts, TimeSpan retention, int batchSize, CancellationToken cancellationToken = default) =>
        _inner.PurgeArtifactsAsync(artifacts, retention, batchSize, cancellationToken);
    public ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken cancellationToken = default) =>
        _inner.PutAsync(kind, content, cancellationToken);
    public ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) =>
        _inner.OpenAsync(digest, cancellationToken);
    public ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) =>
        _inner.ExistsAsync(digest, cancellationToken);
    public ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime olderThanUtc, int batchSize, CancellationToken cancellationToken = default) =>
        _inner.PurgeUnreferencedAsync(references, olderThanUtc, batchSize, cancellationToken);
    public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) =>
        _inner.DeleteAsync(digest, cancellationToken);

    public Task RegisterMemberAsync(MemberRegistration member, CancellationToken cancellationToken = default) =>
        _inner.RegisterMemberAsync(member, cancellationToken);
    public Task<bool> HeartbeatAsync(Guid memberId, IReadOnlyDictionary<string, string> capabilities,
        CancellationToken cancellationToken = default) =>
        _inner.HeartbeatAsync(memberId, capabilities, cancellationToken);
    public Task UnregisterMemberAsync(Guid memberId, CancellationToken cancellationToken = default) =>
        _inner.UnregisterMemberAsync(memberId, cancellationToken);
    public Task<IReadOnlyList<StoreMember>> GetLiveMembersAsync(CancellationToken cancellationToken = default) =>
        _inner.GetLiveMembersAsync(cancellationToken);

    public Task<RetentionResult> PurgeAsync(TimeSpan finishedRetention, TimeSpan tombstoneRetention, int batchSize,
        CancellationToken cancellationToken = default) =>
        _inner.PurgeAsync(finishedRetention, tombstoneRetention, batchSize, cancellationToken);
    public Task<WorkflowTombstone?> GetTombstoneAsync(string instanceId) =>
        _inner.GetTombstoneAsync(instanceId);
    public Task<WorkflowCommitResult> AdmitWorkflowInstanceAsync(CheckpointCommit commit, WorkflowAdmission admission) =>
        _inner.AdmitWorkflowInstanceAsync(commit, admission);
    public Task<WorkflowOwnership?> GetWorkflowOwnershipAsync(string instanceId) =>
        _inner.GetWorkflowOwnershipAsync(instanceId);
    public Task<WorkflowAdmissionRecord?> GetWorkflowAdmissionAsync(string sender, string trigger, string key) =>
        _inner.GetWorkflowAdmissionAsync(sender, trigger, key);

    public Task<EventAppendResult> AppendEventAsync(WorkflowEvent workflowEvent) =>
        _inner.AppendEventAsync(workflowEvent);
    public Task<InboxPage> GetPendingEventsAsync(string instanceId, long afterSequence, int limit) =>
        _inner.GetPendingEventsAsync(instanceId, afterSequence, limit);
    public virtual Task<StoredWorkflowEvent?> GetEventAsync(string instanceId, string eventId) =>
        _inner.GetEventAsync(instanceId, eventId);
    public Task<StoredWait?> GetWaitAsync(string instanceId, string waitId) =>
        _inner.GetWaitAsync(instanceId, waitId);
    public Task<IReadOnlyDictionary<string, long>> GetInboxSequencesAsync(
        IReadOnlyCollection<string> instanceIds) =>
        _inner.GetInboxSequencesAsync(instanceIds);
    public Task<IReadOnlyList<StoredWait>> GetOpenWaitsAsync(WaitQuery query) =>
        _inner.GetOpenWaitsAsync(query);

    private readonly IWorkflowStateRepository _inner;

    public FaultInjectingRepository() : this(TimeProvider.System)
    {
    }

    /// <summary>A store whose clock is <paramref name="time"/>, as a test that drives the engine's clock needs.</summary>
    public FaultInjectingRepository(TimeProvider time) => _inner = new InMemoryWorkflowStateRepository(time);
    public FaultInjectingRepository(IWorkflowStateRepository inner) => _inner = inner;
    private int _failedReads;
    private int _failedCommits;

    /// <summary>Instance reads that match fail with an IOException while this returns true.</summary>
    public Func<string, bool> FailRead { get; set; } = _ => false;

    /// <summary>Commits that match fail with an IOException while this returns true.</summary>
    public Func<WorkflowInstance, bool> FailCommit { get; set; } = NeverFail;

    private static readonly Func<WorkflowInstance, bool> NeverFail = _ => false;

    public int FailedReads => Volatile.Read(ref _failedReads);
    public int FailedCommits => Volatile.Read(ref _failedCommits);

    public Task<WorkflowInstance?> GetWorkflowInstanceSummaryAsync(string instanceId) =>
        _inner.GetWorkflowInstanceSummaryAsync(instanceId);
    public Task<IEnumerable<WorkflowInstance>> GetChildInstancesAsync(string parentInstanceId) =>
        _inner.GetChildInstancesAsync(parentInstanceId);

    public Task<WorkflowInstance?> GetWorkflowInstanceAsync(string instanceId)
    {
        if (!FailRead(instanceId))
            return _inner.GetWorkflowInstanceAsync(instanceId);
        Interlocked.Increment(ref _failedReads);
        throw new IOException($"Injected read failure for '{instanceId}'");
    }

    public virtual async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
    {
        if (ReferenceEquals(FailCommit, NeverFail))
            return await _inner.CommitWorkflowInstanceAsync(commit);
        // The whole instance the commit saves: the commit applied to the instance it was based on.
        var instance = commit.ReadInstance(commit.ReplacesParts ? null : await _inner.GetWorkflowInstanceAsync(commit.InstanceId));
        if (!FailCommit(instance))
            return await _inner.CommitWorkflowInstanceAsync(commit);
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

    public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByNameAsync(string workflowName) =>
        _inner.GetWorkflowInstancesByNameAsync(workflowName);
    public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByStatusAsync(WorkflowStatus status) =>
        _inner.GetWorkflowInstancesByStatusAsync(status);
    public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByCorrelationIdAsync(string correlationId) =>
        _inner.GetWorkflowInstancesByCorrelationIdAsync(correlationId);
    public Task<IReadOnlyList<StoredTraceEntry>> GetTraceAsync(string instanceId, long afterSequence = 0,
        int limit = 1000) =>
        _inner.GetTraceAsync(instanceId, afterSequence, limit);
    public Task DeleteWorkflowInstanceAsync(string instanceId, TimeSpan? tombstoneRetention = null) => _inner.DeleteWorkflowInstanceAsync(instanceId, tombstoneRetention);

    public Task RegisterPublisherAsync(string name, IReadOnlyCollection<string> eventTypes, bool replayRetained = false) =>
        _inner.RegisterPublisherAsync(name, eventTypes, replayRetained);
    public Task UnregisterPublisherAsync(string name) => _inner.UnregisterPublisherAsync(name);
    public Task<IReadOnlyList<OutboxDelivery>> ClaimDeliveriesAsync(
        string publisherName, string token, TimeSpan leaseDuration, int limit) =>
        _inner.ClaimDeliveriesAsync(publisherName, token, leaseDuration, limit);
    public Task<bool> RenewDeliveryLeaseAsync(
        string eventId, string publisherName, string token, TimeSpan leaseDuration) =>
        _inner.RenewDeliveryLeaseAsync(eventId, publisherName, token, leaseDuration);
    public Task<bool> SettleDeliveryAsync(
        string eventId, string publisherName, string token, OutboxPublishResult result) =>
        _inner.SettleDeliveryAsync(eventId, publisherName, token, result);
    public Task<OutboxDelivery?> GetDeliveryAsync(string eventId, string publisherName) =>
        _inner.GetDeliveryAsync(eventId, publisherName);
    public Task<OutboxPublisherState?> GetPublisherAsync(string publisherName) =>
        _inner.GetPublisherAsync(publisherName);
}
