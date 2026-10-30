using IxIFlow.Core;

namespace IxIFlow.Tests.Conformance;

/// <summary>
/// A profile repository with injected faults: a commit that fails before it is applied, as a host
/// dying would, or one that is applied but whose acknowledgement is lost, once.
/// </summary>
internal sealed class FaultRepository(IWorkflowStateRepository inner) : IWorkflowStateRepository
{
    public Task<CatalogPage<CatalogWorkflow>>
        ListWorkflowsAsync(CatalogWorkflowQuery query, CancellationToken cancellationToken = default) =>
        inner.ListWorkflowsAsync(query, cancellationToken: cancellationToken);

    public Task<CatalogPage<CatalogWorkflowVersion>> ListWorkflowVersionsAsync(string name, CatalogVersionQuery query,
        CancellationToken cancellationToken = default) =>
        inner.ListWorkflowVersionsAsync(name, query, cancellationToken: cancellationToken);

    public Task<CatalogWorkflowVersionDetail?> GetWorkflowVersionDetailAsync(string name, int version, CatalogScope scope,
        CancellationToken cancellationToken = default) =>
        inner.GetWorkflowVersionDetailAsync(name, version, scope, cancellationToken: cancellationToken);

    public Task<CatalogPage<CatalogTemplate>>
        ListTemplatesAsync(CatalogTemplateQuery query, CancellationToken cancellationToken = default) =>
        inner.ListTemplatesAsync(query, cancellationToken: cancellationToken);

    public Task<CatalogPage<CatalogTemplate>> ListTemplateVersionsAsync(string key, CatalogVersionQuery query,
        CancellationToken cancellationToken = default) => inner.ListTemplateVersionsAsync(key, query, cancellationToken: cancellationToken);

    public Task<CatalogTemplateDetail?> GetTemplateDetailAsync(string key, int version, CatalogScope scope,
        CancellationToken cancellationToken = default) =>
        inner.GetTemplateDetailAsync(key, version, scope, cancellationToken: cancellationToken);

    public Task<IReadOnlyList<CatalogStepDescriptor>> ListStepDescriptorsAsync(CatalogScope scope,
        CancellationToken cancellationToken = default) => inner.ListStepDescriptorsAsync(scope, cancellationToken: cancellationToken);

    public Task<CatalogPage<CatalogActivity>>
        ListActivitiesAsync(CatalogActivityQuery query, CancellationToken cancellationToken = default) =>
        inner.ListActivitiesAsync(query, cancellationToken: cancellationToken);

    public Task<CatalogPage<CatalogActivity>> ListActivityVersionsAsync(string key, CatalogVersionQuery query,
        CancellationToken cancellationToken = default) => inner.ListActivityVersionsAsync(key, query, cancellationToken: cancellationToken);

    public Task<CatalogActivityDetail?> GetActivityAsync(string key, string version, ActivityProviderSelector provider, CatalogScope scope,
        CancellationToken cancellationToken = default) =>
        inner.GetActivityAsync(key, version, provider, scope, cancellationToken: cancellationToken);

    public Task<CatalogPage<CatalogPackage>> ListPackagesAsync(CatalogPackageQuery query, CancellationToken cancellationToken = default) =>
        inner.ListPackagesAsync(query, cancellationToken: cancellationToken);

    public Task<CatalogPage<CatalogPackage>> ListPackageVersionsAsync(string id, CatalogVersionQuery query,
        CancellationToken cancellationToken = default) => inner.ListPackageVersionsAsync(id, query, cancellationToken: cancellationToken);

    public Task<CatalogPackageDetail?> GetPackageDetailAsync(string id, string version, CatalogScope scope,
        CancellationToken cancellationToken = default) =>
        inner.GetPackageDetailAsync(id, version, scope, cancellationToken: cancellationToken);

    public Task<CatalogPage<CatalogActivity>> GetPackageActivitiesAsync(string id, string version, CatalogVersionQuery query,
        CancellationToken cancellationToken = default) =>
        inner.GetPackageActivitiesAsync(id, version, query, cancellationToken: cancellationToken);

    public Task<IReadOnlyList<WorkflowPackage>> GetPendingPackageDependentsAsync(string missingPackageId, string? afterIdentity, int limit,
        CancellationToken cancellationToken = default) =>
        inner.GetPendingPackageDependentsAsync(missingPackageId, afterIdentity, limit, cancellationToken: cancellationToken);

    public Task<IxIFlow.Templates.TemplateVersionEntry> RegisterTemplateVersionAsync(IxIFlow.Templates.TemplateVersion version,
        IArtifactStore artifacts, string? actor = null, CancellationToken cancellationToken = default) =>
        inner.RegisterTemplateVersionAsync(version, artifacts, actor, cancellationToken: cancellationToken);

    public Task<IxIFlow.Templates.TemplateVersionEntry?> GetTemplateVersionAsync(string key, int version,
        CancellationToken cancellationToken = default) => inner.GetTemplateVersionAsync(key, version, cancellationToken: cancellationToken);

    public Task<IReadOnlyList<IxIFlow.Templates.TemplateVersionEntry>> GetTemplateVersionsAsync(string? key = null,
        CancellationToken cancellationToken = default) => inner.GetTemplateVersionsAsync(key, cancellationToken: cancellationToken);

    public Task<IxIFlow.Templates.TemplateVersionEntry> PublishTemplateDefinitionAsync(WorkflowDraftId draftId,
        IxIFlow.Templates.TemplateVersion version, IArtifactStore artifacts, string? actor = null,
        CancellationToken cancellationToken = default) =>
        inner.PublishTemplateDefinitionAsync(draftId, version, artifacts, actor, cancellationToken: cancellationToken);

    public Task<PackageHolders> GetPackageHoldersAsync(string id, string version, CatalogScope scope,
        CancellationToken cancellationToken = default) =>
        inner.GetPackageHoldersAsync(id, version, scope, cancellationToken: cancellationToken);

    public Task<PackageRegistration> PublishPackageDefinitionAsync(WorkflowDraftId draftId, WorkflowPackage package,
        PackageActivities activities, IArtifactStore artifacts, string? actor = null, CancellationToken cancellationToken = default) =>
        inner.PublishPackageDefinitionAsync(draftId, package, activities, artifacts, actor, cancellationToken: cancellationToken);

    public Task<WorkflowPackage> SetPackageWithdrawnAsync(string id, string version, bool withdrawn, CatalogMutationToken expected,
        string? actor = null, CancellationToken cancellationToken = default) =>
        inner.SetPackageWithdrawnAsync(id, version, withdrawn, expected, actor, cancellationToken: cancellationToken);

    public Task<PackageRegistration> RegisterPackageAsync(WorkflowPackage package, PackageActivities activities, IArtifactStore artifacts,
        string? actor = null, CancellationToken cancellationToken = default) =>
        inner.RegisterPackageAsync(package, activities, artifacts, actor, cancellationToken: cancellationToken);

    public Task<PackageRegistration> CompletePackageExtractionAsync(string id, string version, ArtifactDigest expectedDigest,
        PackageActivities activities, IArtifactStore artifacts, string? actor = null, CancellationToken cancellationToken = default) =>
        inner.CompletePackageExtractionAsync(id, version, expectedDigest, activities, artifacts, actor,
            cancellationToken: cancellationToken);

    public IArtifactStore? CatalogArtifacts
    {
        get => inner.CatalogArtifacts;
        set => inner.CatalogArtifacts = value;
    }

    public Task<IReadOnlyList<WorkflowPackage>> GetPackageExtractionPageAsync(PackageExtraction extraction, string? afterIdentity,
        int limit, CancellationToken cancellationToken = default) =>
        inner.GetPackageExtractionPageAsync(extraction, afterIdentity, limit, cancellationToken: cancellationToken);

    public Task<IReadOnlyList<InstalledActivityPackage>> GetInstalledPackagesAsync(string? id = null,
        CancellationToken cancellationToken = default) => inner.GetInstalledPackagesAsync(id, cancellationToken: cancellationToken);

    public Task<IReadOnlyList<WorkflowPackage>> GetPackagesAsync(string? id = null, CancellationToken cancellationToken = default) =>
        inner.GetPackagesAsync(id, cancellationToken: cancellationToken);

    public Task<WorkflowPackage?> GetPackageAsync(string id, string version, CancellationToken cancellationToken = default) =>
        inner.GetPackageAsync(id, version, cancellationToken: cancellationToken);

    public Task<IReadOnlyList<WorkflowPackage>> GetPackagePageAsync(string id, string? afterVersion, int limit,
        CancellationToken cancellationToken = default) =>
        inner.GetPackagePageAsync(id, afterVersion, limit, cancellationToken: cancellationToken);

    public IPayloadStore PayloadStore => inner.PayloadStore;

    public Task<bool> DeleteDefinitionAsync(WorkflowDraftId expected, CancellationToken cancellationToken = default) =>
        inner.DeleteDefinitionAsync(expected, cancellationToken: cancellationToken);

    public Task<CatalogPage<WorkflowDraftSummary>> GetDefinitionsAsync(WorkflowDraftQuery query,
        CancellationToken cancellationToken = default) => inner.GetDefinitionsAsync(query, cancellationToken: cancellationToken);

    public Task<RetirementBlockers> GetRetirementBlockersAsync(string name, int version, CatalogScope scope,
        CancellationToken cancellationToken = default) =>
        inner.GetRetirementBlockersAsync(name, version, scope, cancellationToken: cancellationToken);

    public Task<WorkflowPublication> BeginPublicationAsync(WorkflowDraftId draftId, Guid hostId, string? actor, TimeSpan staleAfter,
        CancellationToken cancellationToken = default) =>
        inner.BeginPublicationAsync(draftId, hostId, actor, staleAfter, cancellationToken: cancellationToken);

    public Task<bool> HeartbeatPublicationAsync(Guid id, Guid hostId, CancellationToken cancellationToken = default) =>
        inner.HeartbeatPublicationAsync(id, hostId, cancellationToken: cancellationToken);

    public Task<WorkflowPublication> CompletePublicationAsync(Guid id, Guid hostId, WorkflowPublicationOutcome outcome,
        CancellationToken cancellationToken = default) =>
        inner.CompletePublicationAsync(id, hostId, outcome, cancellationToken: cancellationToken);

    public Task<WorkflowPublication?> GetPublicationAsync(Guid id, CatalogScope scope, CancellationToken cancellationToken = default) =>
        inner.GetPublicationAsync(id, scope, cancellationToken: cancellationToken);

    public Task<WorkflowPublication?> GetLatestPublicationAsync(string kind, string name, int version, Guid generation, CatalogScope scope,
        CancellationToken cancellationToken = default) =>
        inner.GetLatestPublicationAsync(kind, name, version, generation, scope, cancellationToken);

    public Task<CatalogPage<WorkflowPublication>> GetPublicationsAsync(WorkflowPublicationQuery query,
        CancellationToken cancellationToken = default) => inner.GetPublicationsAsync(query, cancellationToken: cancellationToken);

    public Task<int> PurgePublicationsAsync(TimeSpan retention, int batchSize, CancellationToken cancellationToken = default) =>
        inner.PurgePublicationsAsync(retention, batchSize, cancellationToken);

    public Task<WorkflowDraft?> GetDefinitionAsync(string kind, string name, int version, CancellationToken cancellationToken = default) =>
        inner.GetDefinitionAsync(kind, name, version, cancellationToken: cancellationToken);

    public Task<WorkflowDraft> SaveDefinitionAsync(WorkflowDraft definition, WorkflowDraftId? expected, string? actor = null,
        CancellationToken cancellationToken = default) =>
        inner.SaveDefinitionAsync(definition, expected, actor, cancellationToken: cancellationToken);

    public Task RegisterWorkflowVersionAsync(WorkflowVersion version, IArtifactStore? artifacts = null,
        CancellationToken cancellationToken = default) =>
        inner.RegisterWorkflowVersionAsync(version, artifacts, cancellationToken: cancellationToken);

    public Task<WorkflowVersion?> GetWorkflowVersionAsync(string name, int version, CancellationToken cancellationToken = default) =>
        inner.GetWorkflowVersionAsync(name, version, cancellationToken: cancellationToken);

    public Task<long> GetCatalogRevisionAsync(CancellationToken cancellationToken = default) =>
        inner.GetCatalogRevisionAsync(cancellationToken: cancellationToken);

    public Task<WorkflowCatalogChanges> GetWorkflowVersionChangesAsync(long afterRevision, CancellationToken cancellationToken = default) =>
        inner.GetWorkflowVersionChangesAsync(afterRevision, cancellationToken: cancellationToken);

    public Task<IReadOnlyList<WorkflowVersion>>
        GetWorkflowVersionsAsync(string? name = null, CancellationToken cancellationToken = default) =>
        inner.GetWorkflowVersionsAsync(name, cancellationToken: cancellationToken);

    public Task<WorkflowVersion> SetWorkflowVersionStateAsync(string name, int version, WorkflowVersionState state,
        CatalogMutationToken expected, IArtifactStore? artifacts = null, string? actor = null,
        CancellationToken cancellationToken = default) =>
        inner.SetWorkflowVersionStateAsync(name, version, state, expected, artifacts, actor, cancellationToken: cancellationToken);

    public Task<WorkflowVersion> PublishDefinitionAsync(WorkflowDraftId draftId, WorkflowVersion version, IArtifactStore artifacts,
        string? actor = null, CancellationToken cancellationToken = default) =>
        inner.PublishDefinitionAsync(draftId, version, artifacts, actor, cancellationToken: cancellationToken);

    public Task<int> PurgeArtifactsAsync(IArtifactStore artifacts, TimeSpan retention, int batchSize,
        CancellationToken cancellationToken = default) =>
        inner.PurgeArtifactsAsync(artifacts, retention, batchSize, cancellationToken);

    public ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken cancellationToken = default) =>
        inner.PutAsync(kind, content, cancellationToken);

    public ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) =>
        inner.OpenAsync(digest, cancellationToken);

    public ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) =>
        inner.ExistsAsync(digest, cancellationToken);

    public ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime olderThanUtc, int batchSize,
        CancellationToken cancellationToken = default) =>
        inner.PurgeUnreferencedAsync(references, olderThanUtc, batchSize, cancellationToken);

    public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) =>
        inner.DeleteAsync(digest, cancellationToken);

    public Task RegisterMemberAsync(MemberRegistration member, CancellationToken cancellationToken = default) =>
        inner.RegisterMemberAsync(member, cancellationToken);

    public Task<bool> HeartbeatAsync(Guid memberId, IReadOnlyDictionary<string, string> capabilities,
        CancellationToken cancellationToken = default) =>
        inner.HeartbeatAsync(memberId, capabilities, cancellationToken);

    public Task UnregisterMemberAsync(Guid memberId, CancellationToken cancellationToken = default) =>
        inner.UnregisterMemberAsync(memberId, cancellationToken);

    public Task<IReadOnlyList<WorkflowDemand>> GetWorkflowDemandsAsync(string instanceId, CancellationToken cancellationToken = default) =>
        inner.GetWorkflowDemandsAsync(instanceId, cancellationToken: cancellationToken);

    public Task<UnschedulablePage> GetUnschedulableAsync(int limit = 100, UnschedulableCursor? cursor = null,
        CancellationToken cancellationToken = default) =>
        inner.GetUnschedulableAsync(limit, cursor, cancellationToken);

    public Task<bool> CanScheduleAsync(IReadOnlyList<WorkflowDemand> demands, CancellationToken cancellationToken = default) =>
        inner.CanScheduleAsync(demands, cancellationToken);

    public Task<IReadOnlyList<StoreMember>> GetLiveMembersAsync(CancellationToken cancellationToken = default) =>
        inner.GetLiveMembersAsync(cancellationToken);

    public Task<RetentionResult> PurgeAsync(TimeSpan finishedRetention, TimeSpan tombstoneRetention, int batchSize,
        CancellationToken cancellationToken = default) =>
        inner.PurgeAsync(finishedRetention, tombstoneRetention, batchSize, cancellationToken);

    public Task<WorkflowTombstone?> GetTombstoneAsync(string instanceId, CancellationToken cancellationToken = default) =>
        inner.GetTombstoneAsync(instanceId, cancellationToken: cancellationToken);

    public Task<WorkflowCommitResult> AdmitWorkflowInstanceAsync(CheckpointCommit commit, WorkflowAdmission admission) =>
        inner.AdmitWorkflowInstanceAsync(commit, admission);

    public Task<WorkflowOwnership?> GetWorkflowOwnershipAsync(string instanceId) =>
        inner.GetWorkflowOwnershipAsync(instanceId);

    public Task<WorkflowAdmissionRecord?> GetWorkflowAdmissionAsync(string sender, string trigger, string key) =>
        inner.GetWorkflowAdmissionAsync(sender, trigger, key);

    public Task RegisterPublisherAsync(string name, IReadOnlyCollection<string> eventTypes, bool replayRetained = false,
        CancellationToken cancellationToken = default) =>
        inner.RegisterPublisherAsync(name, eventTypes, replayRetained, cancellationToken: cancellationToken);

    public Task UnregisterPublisherAsync(string name, string actor = "host", CancellationToken cancellationToken = default) =>
        inner.UnregisterPublisherAsync(name, actor, cancellationToken: cancellationToken);

    public Task<DeliveryPage> ListDeliveriesAsync(DeliveryQuery query, CancellationToken cancellationToken = default) =>
        inner.ListDeliveriesAsync(query, cancellationToken: cancellationToken);

    public Task<bool>
        RetryDeliveryAsync(string eventId, string publisherName, string actor, CancellationToken cancellationToken = default) =>
        inner.RetryDeliveryAsync(eventId, publisherName, actor, cancellationToken: cancellationToken);

    public Task<IReadOnlyList<OutboxPublisherState>> ListPublishersAsync(CancellationToken cancellationToken = default) =>
        inner.ListPublishersAsync(cancellationToken: cancellationToken);

    public Task<bool> EnablePublisherAsync(string publisherName, string actor, CancellationToken cancellationToken = default) =>
        inner.EnablePublisherAsync(publisherName, actor, cancellationToken: cancellationToken);

    public Task<IReadOnlyList<OutboxDelivery>> ClaimDeliveriesAsync(string publisherName, string token, TimeSpan leaseDuration, int limit,
        CancellationToken cancellationToken = default) =>
        inner.ClaimDeliveriesAsync(publisherName, token, leaseDuration, limit);

    public Task<bool> RenewDeliveryLeaseAsync(string eventId, string publisherName, string token, TimeSpan leaseDuration,
        CancellationToken cancellationToken = default) =>
        inner.RenewDeliveryLeaseAsync(eventId, publisherName, token, leaseDuration);

    public Task<bool> SettleDeliveryAsync(string eventId, string publisherName, string token, OutboxPublishResult result,
        CancellationToken cancellationToken = default) =>
        inner.SettleDeliveryAsync(eventId, publisherName, token, result);

    public Task<OutboxDelivery?> GetDeliveryAsync(string eventId, string publisherName, CancellationToken cancellationToken = default) =>
        inner.GetDeliveryAsync(eventId, publisherName, cancellationToken: cancellationToken);

    public Task<OutboxPublisherState?> GetPublisherAsync(string publisherName, CancellationToken cancellationToken = default) =>
        inner.GetPublisherAsync(publisherName, cancellationToken: cancellationToken);

    public Task<EventAppendResult> AppendEventAsync(WorkflowEvent workflowEvent, CancellationToken cancellationToken = default) =>
        inner.AppendEventAsync(workflowEvent, cancellationToken: cancellationToken);

    public Task<InboxPage> GetPendingEventsAsync(string instanceId, long afterSequence, int limit,
        CancellationToken cancellationToken = default) =>
        inner.GetPendingEventsAsync(instanceId, afterSequence, limit, cancellationToken: cancellationToken);

    public Task<StoredWorkflowEvent?> GetEventAsync(string instanceId, string eventId, CancellationToken cancellationToken = default) =>
        inner.GetEventAsync(instanceId, eventId, cancellationToken: cancellationToken);

    public Task<StoredWait?> GetWaitAsync(string instanceId, string waitId, CancellationToken cancellationToken = default) =>
        inner.GetWaitAsync(instanceId, waitId, cancellationToken: cancellationToken);

    public Task<IReadOnlyDictionary<string, long>> GetInboxSequencesAsync(
        IReadOnlyCollection<string> instanceIds, CancellationToken cancellationToken = default) =>
        inner.GetInboxSequencesAsync(instanceIds, cancellationToken: cancellationToken);

    public Task<IReadOnlyList<StoredWait>> GetOpenWaitsAsync(WaitQuery query, CancellationToken cancellationToken = default) =>
        inner.GetOpenWaitsAsync(query, cancellationToken: cancellationToken);

    private int _acknowledgementsLost;

    /// <summary>Commits matching this fail without being applied.</summary>
    public Func<WorkflowInstance, bool> FailCommit { get; set; } = _ => false;

    /// <summary>The first commit matching this is applied, then reported as failed.</summary>
    public Func<WorkflowInstance, bool> LoseAcknowledgement { get; init; } = _ => false;

    public async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit,
        CancellationToken cancellationToken = default)
    {
        // The whole instance the commit saves: the commit applied to the instance it was based on.
        var instance = commit.ReadInstance(commit.ReplacesParts
            ? null
            : await inner.GetWorkflowInstanceAsync(commit.InstanceId, cancellationToken: cancellationToken));

        if (FailCommit(instance))
            throw new IOException($"Injected commit failure for '{instance.InstanceId}'");

        var result = await inner.CommitWorkflowInstanceAsync(commit, cancellationToken: cancellationToken);

        if (result.Status == WorkflowCommitStatus.Applied && LoseAcknowledgement(instance) &&
            Interlocked.Exchange(ref _acknowledgementsLost, 1) == 0)
            throw new IOException($"Injected lost acknowledgement for '{instance.InstanceId}'");

        return result;
    }

    public Task<CancellationRequestResult> RequestCancellationAsync(string instanceId, CancellationReason reason,
        Guid? expectedIncarnation = null, CancellationToken cancellationToken = default) =>
        inner.RequestCancellationAsync(instanceId, reason, expectedIncarnation, cancellationToken: cancellationToken);

    public Task<CancellationReason?> GetCancellationRequestAsync(string instanceId, CancellationToken cancellationToken = default) =>
        inner.GetCancellationRequestAsync(instanceId, cancellationToken: cancellationToken);

    public Task<WorkflowControlRequests> GetControlRequestsAsync(IReadOnlyCollection<string> instanceIds) =>
        inner.GetControlRequestsAsync(instanceIds);

    public Task<DebugCommandResult> RequestDebugCommandAsync(string instanceId, DebugCommandRequest command) =>
        inner.RequestDebugCommandAsync(instanceId, command);

    public Task<IReadOnlyDictionary<string, WorkflowDebugState>> GetDebugStatesAsync(IReadOnlyCollection<string> instanceIds) =>
        inner.GetDebugStatesAsync(instanceIds);

    public Task<IReadOnlyList<RunnableInstance>>
        GetRunnableInstancesAsync(ClaimQuery query, CancellationToken cancellationToken = default) =>
        inner.GetRunnableInstancesAsync(query, cancellationToken: cancellationToken);

    public Task<ExecutionLeaseGrant?> TryAcquireExecutionLeaseAsync(string instanceId, string token,
        TimeSpan duration, ClaimQuery query, ExecutionClaimKind kind = ExecutionClaimKind.Runnable,
        CancellationToken cancellationToken = default) => inner.TryAcquireExecutionLeaseAsync(instanceId, token, duration, query, kind);

    public Task<IReadOnlyList<ExecutionLeaseRenewalResult>> RenewExecutionLeasesAsync(IReadOnlyList<ExecutionLeaseRenewal> leases,
        CancellationToken cancellationToken = default) =>
        inner.RenewExecutionLeasesAsync(leases, cancellationToken: cancellationToken);

    public Task<ExecutionLeaseReleaseResult> ReleaseExecutionLeaseAsync(string instanceId, string token,
        CancellationToken cancellationToken = default) =>
        inner.ReleaseExecutionLeaseAsync(instanceId, token);

    public Task<WorkflowInstance?> GetWorkflowInstanceSummaryAsync(string instanceId, CancellationToken cancellationToken = default) =>
        inner.GetWorkflowInstanceSummaryAsync(instanceId, cancellationToken: cancellationToken);

    public Task<WorkflowInstance?> GetWorkflowInstanceAsync(string instanceId, CancellationToken cancellationToken = default) =>
        inner.GetWorkflowInstanceAsync(instanceId, cancellationToken: cancellationToken);

    public Task<InstancePage> QueryInstancesAsync(InstanceQuery query, CancellationToken cancellationToken = default) =>
        inner.QueryInstancesAsync(query, cancellationToken: cancellationToken);

    public Task<WorkflowControlReceipt> RequestTerminationAsync(string instanceId, CancellationReason reason,
        Guid? expectedIncarnation = null, CancellationToken cancellationToken = default) =>
        inner.RequestTerminationAsync(instanceId, reason, expectedIncarnation, cancellationToken: cancellationToken);

    public Task<IReadOnlyList<WorkflowDiagnostic>> GetDiagnosticsForIncarnationAsync(string instanceId, Guid incarnation,
        CancellationToken cancellationToken = default) =>
        inner.GetDiagnosticsForIncarnationAsync(instanceId, incarnation, cancellationToken: cancellationToken);

    public Task<IReadOnlyList<StoredTraceEntry>> GetTraceAsync(string instanceId, long afterSequence = 0,
        int limit = 1000, CancellationToken cancellationToken = default) =>
        inner.GetTraceAsync(instanceId, afterSequence, limit, cancellationToken: cancellationToken);

    public Task DeleteWorkflowInstanceAsync(string instanceId, TimeSpan? tombstoneRetention = null,
        CancellationToken cancellationToken = default) =>
        inner.DeleteWorkflowInstanceAsync(instanceId, tombstoneRetention, cancellationToken: cancellationToken);
}