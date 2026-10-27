using IxIFlow.Core;

namespace IxIFlow.Tests.Infrastructure;

/// <summary>
/// An in-memory store that fails chosen reads or commits on demand, to test how the engine
/// treats infrastructure failures.
/// </summary>
public class FaultInjectingRepository : IWorkflowStateRepository
{
    public Task<CatalogPage<CatalogWorkflow>> ListWorkflowsAsync(CatalogWorkflowQuery query) => _inner.ListWorkflowsAsync(query);
    public Task<CatalogPage<CatalogWorkflowVersion>> ListWorkflowVersionsAsync(string name, CatalogVersionQuery query) => _inner.ListWorkflowVersionsAsync(name, query);
    public Task<CatalogWorkflowVersionDetail?> GetWorkflowVersionDetailAsync(string name, int version, CatalogScope scope) => _inner.GetWorkflowVersionDetailAsync(name, version, scope);
    public Task<CatalogPage<CatalogTemplate>> ListTemplatesAsync(CatalogTemplateQuery query) => _inner.ListTemplatesAsync(query);
    public Task<CatalogPage<CatalogTemplate>> ListTemplateVersionsAsync(string key, CatalogVersionQuery query) => _inner.ListTemplateVersionsAsync(key, query);
    public Task<CatalogTemplateDetail?> GetTemplateDetailAsync(string key, int version, CatalogScope scope) => _inner.GetTemplateDetailAsync(key, version, scope);
    public Task<IReadOnlyList<CatalogStepDescriptor>> ListStepDescriptorsAsync(CatalogScope scope) => _inner.ListStepDescriptorsAsync(scope);
    public Task<CatalogPage<CatalogActivity>> ListActivitiesAsync(CatalogActivityQuery query) => _inner.ListActivitiesAsync(query);
    public Task<CatalogPage<CatalogActivity>> ListActivityVersionsAsync(string key, CatalogVersionQuery query) => _inner.ListActivityVersionsAsync(key, query);
    public Task<CatalogActivityDetail?> GetActivityAsync(string key, string version, ActivityProviderSelector provider, CatalogScope scope) => _inner.GetActivityAsync(key, version, provider, scope);
    public Task<CatalogPage<CatalogPackage>> ListPackagesAsync(CatalogPackageQuery query) => _inner.ListPackagesAsync(query);
    public Task<CatalogPage<CatalogPackage>> ListPackageVersionsAsync(string id, CatalogVersionQuery query) => _inner.ListPackageVersionsAsync(id, query);
    public Task<CatalogPackageDetail?> GetPackageDetailAsync(string id, string version, CatalogScope scope) => _inner.GetPackageDetailAsync(id, version, scope);
    public Task<CatalogPage<CatalogActivity>> GetPackageActivitiesAsync(string id, string version, CatalogVersionQuery query) => _inner.GetPackageActivitiesAsync(id, version, query);
    public Task<IReadOnlyList<WorkflowPackage>> GetPendingPackageDependentsAsync(string missingPackageId, string? afterIdentity, int limit) => _inner.GetPendingPackageDependentsAsync(missingPackageId, afterIdentity, limit);
    public Task<IxIFlow.Templates.TemplateVersionEntry> RegisterTemplateVersionAsync(IxIFlow.Templates.TemplateVersion version, IArtifactStore artifacts, string? actor = null) => _inner.RegisterTemplateVersionAsync(version, artifacts, actor);
    public Task<IxIFlow.Templates.TemplateVersionEntry?> GetTemplateVersionAsync(string key, int version) => _inner.GetTemplateVersionAsync(key, version);
    public Task<IReadOnlyList<IxIFlow.Templates.TemplateVersionEntry>> GetTemplateVersionsAsync(string? key = null) => _inner.GetTemplateVersionsAsync(key);
    public Task<IxIFlow.Templates.TemplateVersionEntry> PublishTemplateDefinitionAsync(WorkflowDraftId draftId, IxIFlow.Templates.TemplateVersion version, IArtifactStore artifacts, string? actor = null) => _inner.PublishTemplateDefinitionAsync(draftId, version, artifacts, actor);
    public Task<PackageHolders> GetPackageHoldersAsync(string id, string version, CatalogScope scope) => _inner.GetPackageHoldersAsync(id, version, scope);
    public Task<PackageRegistration> PublishPackageDefinitionAsync(WorkflowDraftId draftId, WorkflowPackage package, PackageActivities activities, IArtifactStore artifacts, string? actor = null) => _inner.PublishPackageDefinitionAsync(draftId, package, activities, artifacts, actor);
    public Task<WorkflowPackage> SetPackageWithdrawnAsync(string id, string version, bool withdrawn, CatalogMutationToken expected, string? actor = null) => _inner.SetPackageWithdrawnAsync(id, version, withdrawn, expected, actor);
    public Task<PackageRegistration> RegisterPackageAsync(WorkflowPackage package, PackageActivities activities, IArtifactStore artifacts, string? actor = null) => _inner.RegisterPackageAsync(package, activities, artifacts, actor);
    public Task<PackageRegistration> CompletePackageExtractionAsync(string id, string version, ArtifactDigest expectedDigest, PackageActivities activities, IArtifactStore artifacts, string? actor = null) => _inner.CompletePackageExtractionAsync(id, version, expectedDigest, activities, artifacts, actor);
    public IArtifactStore? CatalogArtifacts { get => _inner.CatalogArtifacts; set => _inner.CatalogArtifacts = value; }
    public Task<IReadOnlyList<WorkflowPackage>> GetPackageExtractionPageAsync(PackageExtraction extraction, string? afterIdentity, int limit) => _inner.GetPackageExtractionPageAsync(extraction, afterIdentity, limit);
    public Task<IReadOnlyList<InstalledActivityPackage>> GetInstalledPackagesAsync(string? id = null) => _inner.GetInstalledPackagesAsync(id);
    public Task<IReadOnlyList<WorkflowPackage>> GetPackagesAsync(string? id = null) => _inner.GetPackagesAsync(id);
    public Task<WorkflowPackage?> GetPackageAsync(string id, string version) => _inner.GetPackageAsync(id, version);
    public Task<IReadOnlyList<WorkflowPackage>> GetPackagePageAsync(string id, string? afterVersion, int limit) => _inner.GetPackagePageAsync(id, afterVersion, limit);
    public IPayloadStore PayloadStore => _inner.PayloadStore;
    public Task<bool> DeleteDefinitionAsync(WorkflowDraftId expected) => _inner.DeleteDefinitionAsync(expected);
    public Task<CatalogPage<WorkflowDraftSummary>> GetDefinitionsAsync(WorkflowDraftQuery query) => _inner.GetDefinitionsAsync(query);
    public Task<RetirementBlockers> GetRetirementBlockersAsync(string name, int version, CatalogScope scope) => _inner.GetRetirementBlockersAsync(name, version, scope);
    public Task<WorkflowPublication> BeginPublicationAsync(WorkflowDraftId draftId, Guid hostId, string? actor, TimeSpan staleAfter) => _inner.BeginPublicationAsync(draftId, hostId, actor, staleAfter);
    public Task<bool> HeartbeatPublicationAsync(Guid id, Guid hostId) => _inner.HeartbeatPublicationAsync(id, hostId);
    public Task<WorkflowPublication> CompletePublicationAsync(Guid id, Guid hostId, WorkflowPublicationOutcome outcome) => _inner.CompletePublicationAsync(id, hostId, outcome);
    public Task<WorkflowPublication?> GetPublicationAsync(Guid id, CatalogScope scope) => _inner.GetPublicationAsync(id, scope);
    public Task<CatalogPage<WorkflowPublication>> GetPublicationsAsync(WorkflowPublicationQuery query) => _inner.GetPublicationsAsync(query);
    public Task<int> PurgePublicationsAsync(TimeSpan retention, int batchSize, CancellationToken cancellationToken = default) => _inner.PurgePublicationsAsync(retention, batchSize, cancellationToken);
    public Task<WorkflowDraft?> GetDefinitionAsync(string kind, string name, int version) =>
        _inner.GetDefinitionAsync(kind, name, version);
    public Task<WorkflowDraft> SaveDefinitionAsync(WorkflowDraft definition, WorkflowDraftId? expected, string? actor = null) =>
        _inner.SaveDefinitionAsync(definition, expected, actor);
    public Task RegisterWorkflowVersionAsync(WorkflowVersion version, IArtifactStore? artifacts = null) =>
        _inner.RegisterWorkflowVersionAsync(version, artifacts);
    public Task<WorkflowVersion?> GetWorkflowVersionAsync(string name, int version) =>
        _inner.GetWorkflowVersionAsync(name, version);
    public Task<long> GetCatalogRevisionAsync() => _inner.GetCatalogRevisionAsync();
    public Task<WorkflowCatalogChanges> GetWorkflowVersionChangesAsync(long afterRevision) => _inner.GetWorkflowVersionChangesAsync(afterRevision);
    public Task<IReadOnlyList<WorkflowVersion>> GetWorkflowVersionsAsync(string? name = null) =>
        _inner.GetWorkflowVersionsAsync(name);
    public Task<WorkflowVersion> SetWorkflowVersionStateAsync(string name, int version, WorkflowVersionState state, CatalogMutationToken expected, IArtifactStore? artifacts = null, string? actor = null) =>
        _inner.SetWorkflowVersionStateAsync(name, version, state, expected, artifacts, actor);
    public Task<WorkflowVersion> PublishDefinitionAsync(WorkflowDraftId draftId, WorkflowVersion version, IArtifactStore artifacts, string? actor = null) =>
        _inner.PublishDefinitionAsync(draftId, version, artifacts, actor);
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
    public Task<IReadOnlyList<WorkflowDemand>> GetWorkflowDemandsAsync(string instanceId) =>
        _inner.GetWorkflowDemandsAsync(instanceId);

    public Task<UnschedulablePage> GetUnschedulableAsync(int limit = 100, UnschedulableCursor? cursor = null,
        CancellationToken cancellationToken = default) =>
        _inner.GetUnschedulableAsync(limit, cursor, cancellationToken);
    public Task<bool> CanScheduleAsync(IReadOnlyList<WorkflowDemand> demands, CancellationToken cancellationToken = default) =>
        _inner.CanScheduleAsync(demands, cancellationToken);

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

    public Task<CancellationRequestResult> RequestCancellationAsync(string instanceId, CancellationReason reason, Guid? expectedIncarnation = null) =>
        _inner.RequestCancellationAsync(instanceId, reason, expectedIncarnation);
    public virtual Task<CancellationReason?> GetCancellationRequestAsync(string instanceId) =>
        _inner.GetCancellationRequestAsync(instanceId);
    public virtual Task<WorkflowControlRequests> GetControlRequestsAsync(IReadOnlyCollection<string> instanceIds) =>
        _inner.GetControlRequestsAsync(instanceIds);
    public Task<DebugCommandResult> RequestDebugCommandAsync(string instanceId, DebugCommandRequest command) =>
        _inner.RequestDebugCommandAsync(instanceId, command);
    public Task<IReadOnlyDictionary<string, WorkflowDebugState>> GetDebugStatesAsync(IReadOnlyCollection<string> instanceIds) =>
        _inner.GetDebugStatesAsync(instanceIds);
    public Task<IReadOnlyList<RunnableInstance>> GetRunnableInstancesAsync(ClaimQuery query) =>
        _inner.GetRunnableInstancesAsync(query);
    public Task<ExecutionLeaseGrant?> TryAcquireExecutionLeaseAsync(string instanceId, string token, TimeSpan duration, ClaimQuery query, ExecutionClaimKind kind = ExecutionClaimKind.Runnable) =>
        _inner.TryAcquireExecutionLeaseAsync(instanceId, token, duration, query, kind);
    public virtual Task<IReadOnlyList<ExecutionLeaseRenewalResult>> RenewExecutionLeasesAsync(IReadOnlyList<ExecutionLeaseRenewal> leases) =>
        _inner.RenewExecutionLeasesAsync(leases);
    public Task<ExecutionLeaseReleaseResult> ReleaseExecutionLeaseAsync(string instanceId, string token) =>
        _inner.ReleaseExecutionLeaseAsync(instanceId, token);

    public Task<InstancePage> QueryInstancesAsync(InstanceQuery query) =>
        _inner.QueryInstancesAsync(query);

    public Task<WorkflowControlReceipt> RequestTerminationAsync(string instanceId, CancellationReason reason, Guid? expectedIncarnation = null) =>
        _inner.RequestTerminationAsync(instanceId, reason, expectedIncarnation);
    public Task<IReadOnlyList<WorkflowDiagnostic>> GetDiagnosticsForIncarnationAsync(string instanceId, Guid incarnation) =>
        _inner.GetDiagnosticsForIncarnationAsync(instanceId, incarnation);
    public Task<IReadOnlyList<StoredTraceEntry>> GetTraceAsync(string instanceId, long afterSequence = 0,
        int limit = 1000) =>
        _inner.GetTraceAsync(instanceId, afterSequence, limit);
    public Task DeleteWorkflowInstanceAsync(string instanceId, TimeSpan? tombstoneRetention = null) => _inner.DeleteWorkflowInstanceAsync(instanceId, tombstoneRetention);

    public Task RegisterPublisherAsync(string name, IReadOnlyCollection<string> eventTypes, bool replayRetained = false) =>
        _inner.RegisterPublisherAsync(name, eventTypes, replayRetained);
    public Task UnregisterPublisherAsync(string name, string actor = "host") => _inner.UnregisterPublisherAsync(name, actor);
    public Task<DeliveryPage> ListDeliveriesAsync(DeliveryQuery query) => _inner.ListDeliveriesAsync(query);
    public Task<bool> RetryDeliveryAsync(string eventId, string publisherName, string actor) => _inner.RetryDeliveryAsync(eventId, publisherName, actor);
    public Task<IReadOnlyList<OutboxPublisherState>> ListPublishersAsync() => _inner.ListPublishersAsync();
    public Task<bool> EnablePublisherAsync(string publisherName, string actor) => _inner.EnablePublisherAsync(publisherName, actor);
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
