namespace IxIFlow.Core.Runtime;

/// <summary>
/// The state store as the engine sees it. Any store failure becomes an
/// <see cref="WorkflowErrorKind.Infrastructure"/> engine failure, so it interrupts the
/// instance instead of being handled as a workflow failure.
/// </summary>
internal sealed class GuardedStateRepository : IWorkflowStateRepository
{
    public Task<CatalogPage<CatalogWorkflow>>
        ListWorkflowsAsync(CatalogWorkflowQuery query, CancellationToken cancellationToken = default) => Guard(
        () => _store.ListWorkflowsAsync(query, cancellationToken: cancellationToken), "Reading catalog",
        cancellationToken: cancellationToken);

    public Task<CatalogPage<CatalogWorkflowVersion>> ListWorkflowVersionsAsync(string name, CatalogVersionQuery query,
        CancellationToken cancellationToken = default) =>
        Guard(() => _store.ListWorkflowVersionsAsync(name, query, cancellationToken: cancellationToken), "Reading catalog",
            cancellationToken: cancellationToken);

    public Task<CatalogWorkflowVersionDetail?> GetWorkflowVersionDetailAsync(string name, int version, CatalogScope scope,
        CancellationToken cancellationToken = default) =>
        Guard(() => _store.GetWorkflowVersionDetailAsync(name, version, scope, cancellationToken: cancellationToken), "Reading catalog",
            cancellationToken: cancellationToken);

    public Task<CatalogPage<CatalogTemplate>>
        ListTemplatesAsync(CatalogTemplateQuery query, CancellationToken cancellationToken = default) => Guard(
        () => _store.ListTemplatesAsync(query, cancellationToken: cancellationToken), "Reading catalog",
        cancellationToken: cancellationToken);

    public Task<CatalogPage<CatalogTemplate>>
        ListTemplateVersionsAsync(string key, CatalogVersionQuery query, CancellationToken cancellationToken = default) => Guard(
        () => _store.ListTemplateVersionsAsync(key, query, cancellationToken: cancellationToken), "Reading catalog",
        cancellationToken: cancellationToken);

    public Task<CatalogTemplateDetail?>
        GetTemplateDetailAsync(string key, int version, CatalogScope scope, CancellationToken cancellationToken = default) => Guard(
        () => _store.GetTemplateDetailAsync(key, version, scope, cancellationToken: cancellationToken), "Reading catalog",
        cancellationToken: cancellationToken);

    public Task<IReadOnlyList<CatalogStepDescriptor>>
        ListStepDescriptorsAsync(CatalogScope scope, CancellationToken cancellationToken = default) => Guard(
        () => _store.ListStepDescriptorsAsync(scope, cancellationToken: cancellationToken), "Reading catalog",
        cancellationToken: cancellationToken);

    public Task<CatalogPage<CatalogActivity>>
        ListActivitiesAsync(CatalogActivityQuery query, CancellationToken cancellationToken = default) => Guard(
        () => _store.ListActivitiesAsync(query, cancellationToken: cancellationToken), "Reading catalog",
        cancellationToken: cancellationToken);

    public Task<CatalogPage<CatalogActivity>>
        ListActivityVersionsAsync(string key, CatalogVersionQuery query, CancellationToken cancellationToken = default) => Guard(
        () => _store.ListActivityVersionsAsync(key, query, cancellationToken: cancellationToken), "Reading catalog",
        cancellationToken: cancellationToken);

    public Task<CatalogActivityDetail?> GetActivityAsync(string key, string version, ActivityProviderSelector provider, CatalogScope scope,
        CancellationToken cancellationToken = default) =>
        Guard(() => _store.GetActivityAsync(key, version, provider, scope, cancellationToken: cancellationToken), "Reading catalog",
            cancellationToken: cancellationToken);

    public Task<CatalogPage<CatalogPackage>> ListPackagesAsync(CatalogPackageQuery query, CancellationToken cancellationToken = default) =>
        Guard(() => _store.ListPackagesAsync(query, cancellationToken: cancellationToken), "Reading catalog",
            cancellationToken: cancellationToken);

    public Task<CatalogPage<CatalogPackage>>
        ListPackageVersionsAsync(string id, CatalogVersionQuery query, CancellationToken cancellationToken = default) => Guard(
        () => _store.ListPackageVersionsAsync(id, query, cancellationToken: cancellationToken), "Reading catalog",
        cancellationToken: cancellationToken);

    public Task<CatalogPackageDetail?> GetPackageDetailAsync(string id, string version, CatalogScope scope,
        CancellationToken cancellationToken = default) =>
        Guard(() => _store.GetPackageDetailAsync(id, version, scope, cancellationToken: cancellationToken), "Reading catalog",
            cancellationToken: cancellationToken);

    public Task<CatalogPage<CatalogActivity>> GetPackageActivitiesAsync(string id, string version, CatalogVersionQuery query,
        CancellationToken cancellationToken = default) =>
        Guard(() => _store.GetPackageActivitiesAsync(id, version, query, cancellationToken: cancellationToken), "Reading catalog",
            cancellationToken: cancellationToken);

    public Task<IReadOnlyList<WorkflowPackage>> GetPendingPackageDependentsAsync(string missingPackageId, string? afterIdentity, int limit,
        CancellationToken cancellationToken = default) =>
        Guard(() => _store.GetPendingPackageDependentsAsync(missingPackageId, afterIdentity, limit, cancellationToken: cancellationToken),
            "Reading package work", cancellationToken: cancellationToken);

    public Task<IxIFlow.Templates.TemplateVersionEntry> RegisterTemplateVersionAsync(IxIFlow.Templates.TemplateVersion version,
        IArtifactStore artifacts, string? actor = null, CancellationToken cancellationToken = default) => Guard(
        () => _store.RegisterTemplateVersionAsync(version, artifacts, actor, cancellationToken: cancellationToken),
        "Registering template version", cancellationToken: cancellationToken);

    public Task<IxIFlow.Templates.TemplateVersionEntry?>
        GetTemplateVersionAsync(string key, int version, CancellationToken cancellationToken = default) => Guard(
        () => _store.GetTemplateVersionAsync(key, version, cancellationToken: cancellationToken), "Reading template version",
        cancellationToken: cancellationToken);

    public Task<IReadOnlyList<IxIFlow.Templates.TemplateVersionEntry>>
        GetTemplateVersionsAsync(string? key = null, CancellationToken cancellationToken = default) => Guard(
        () => _store.GetTemplateVersionsAsync(key, cancellationToken: cancellationToken), "Reading template versions",
        cancellationToken: cancellationToken);

    public Task<IxIFlow.Templates.TemplateVersionEntry> PublishTemplateDefinitionAsync(WorkflowDraftId draftId,
        IxIFlow.Templates.TemplateVersion version, IArtifactStore artifacts, string? actor = null,
        CancellationToken cancellationToken = default) =>
        Guard(() => _store.PublishTemplateDefinitionAsync(draftId, version, artifacts, actor, cancellationToken: cancellationToken),
            "Publishing template definition", cancellationToken: cancellationToken);

    public Task<PackageHolders>
        GetPackageHoldersAsync(string id, string version, CatalogScope scope, CancellationToken cancellationToken = default) => Guard(
        () => _store.GetPackageHoldersAsync(id, version, scope, cancellationToken: cancellationToken), "Reading package holders",
        cancellationToken: cancellationToken);

    public Task<PackageRegistration> PublishPackageDefinitionAsync(WorkflowDraftId draftId, WorkflowPackage package,
        PackageActivities activities, IArtifactStore artifacts, string? actor = null, CancellationToken cancellationToken = default) =>
        Guard(
            () => _store.PublishPackageDefinitionAsync(draftId, package, activities, artifacts, actor,
                cancellationToken: cancellationToken), "Publishing activity package", cancellationToken: cancellationToken);

    public Task<WorkflowPackage> SetPackageWithdrawnAsync(string id, string version, bool withdrawn, CatalogMutationToken expected,
        string? actor = null, CancellationToken cancellationToken = default) => Guard(
        () => _store.SetPackageWithdrawnAsync(id, version, withdrawn, expected, actor, cancellationToken: cancellationToken),
        "Withdrawing package", cancellationToken: cancellationToken);

    public Task<PackageRegistration> RegisterPackageAsync(WorkflowPackage package, PackageActivities activities, IArtifactStore artifacts,
        string? actor = null, CancellationToken cancellationToken = default) =>
        Guard(() => _store.RegisterPackageAsync(package, activities, artifacts, actor, cancellationToken: cancellationToken),
            "Registering package artifact", cancellationToken: cancellationToken);

    public Task<PackageRegistration> CompletePackageExtractionAsync(string id, string version, ArtifactDigest expectedDigest,
        PackageActivities activities, IArtifactStore artifacts, string? actor = null, CancellationToken cancellationToken = default) =>
        Guard(
            () => _store.CompletePackageExtractionAsync(id, version, expectedDigest, activities, artifacts, actor,
                cancellationToken: cancellationToken), "Completing package extraction", cancellationToken: cancellationToken);

    public IArtifactStore? CatalogArtifacts
    {
        get => _store.CatalogArtifacts;
        set => _store.CatalogArtifacts = value;
    }

    public Task<IReadOnlyList<WorkflowPackage>> GetPackageExtractionPageAsync(PackageExtraction extraction, string? afterIdentity,
        int limit, CancellationToken cancellationToken = default) =>
        Guard(() => _store.GetPackageExtractionPageAsync(extraction, afterIdentity, limit, cancellationToken: cancellationToken),
            "Reading package extraction work", cancellationToken: cancellationToken);

    public Task<IReadOnlyList<InstalledActivityPackage>> GetInstalledPackagesAsync(string? id = null,
        CancellationToken cancellationToken = default) =>
        Guard(() => _store.GetInstalledPackagesAsync(id, cancellationToken: cancellationToken), "Reading installed activity packages",
            cancellationToken: cancellationToken);

    public Task<IReadOnlyList<WorkflowPackage>> GetPackagesAsync(string? id = null, CancellationToken cancellationToken = default) =>
        Guard(() => _store.GetPackagesAsync(id, cancellationToken: cancellationToken), "Reading package catalog",
            cancellationToken: cancellationToken);

    public Task<WorkflowPackage?> GetPackageAsync(string id, string version, CancellationToken cancellationToken = default) =>
        Guard(() => _store.GetPackageAsync(id, version, cancellationToken: cancellationToken), "Reading package identity",
            cancellationToken: cancellationToken);

    public Task<IReadOnlyList<WorkflowPackage>> GetPackagePageAsync(string id, string? afterVersion, int limit,
        CancellationToken cancellationToken = default) =>
        Guard(() => _store.GetPackagePageAsync(id, afterVersion, limit, cancellationToken: cancellationToken),
            "Reading package catalog page", cancellationToken: cancellationToken);

    public IPayloadStore PayloadStore => _store.PayloadStore;

    public Task<UnschedulablePage> GetUnschedulableAsync(int limit = 100, UnschedulableCursor? cursor = null,
        CancellationToken cancellationToken = default) =>
        Guard(() => _store.GetUnschedulableAsync(limit, cursor, cancellationToken), "Reading unschedulable work",
            cancellationToken: cancellationToken);

    public Task<bool> CanScheduleAsync(IReadOnlyList<WorkflowDemand> demands, CancellationToken cancellationToken = default) =>
        Guard(() => _store.CanScheduleAsync(demands, cancellationToken), "Checking live host eligibility",
            cancellationToken: cancellationToken);

    public Task<bool> DeleteDefinitionAsync(WorkflowDraftId expected, CancellationToken cancellationToken = default) => Guard(
        () => _store.DeleteDefinitionAsync(expected, cancellationToken: cancellationToken), "Accessing workflow catalog or artifacts",
        cancellationToken: cancellationToken);

    public Task<CatalogPage<WorkflowDraftSummary>>
        GetDefinitionsAsync(WorkflowDraftQuery query, CancellationToken cancellationToken = default) => Guard(
        () => _store.GetDefinitionsAsync(query, cancellationToken: cancellationToken), "Accessing workflow catalog or artifacts",
        cancellationToken: cancellationToken);

    public Task<RetirementBlockers>
        GetRetirementBlockersAsync(string name, int version, CatalogScope scope, CancellationToken cancellationToken = default) => Guard(
        () => _store.GetRetirementBlockersAsync(name, version, scope, cancellationToken: cancellationToken),
        "Accessing workflow catalog or artifacts", cancellationToken: cancellationToken);

    public Task<WorkflowPublication> BeginPublicationAsync(WorkflowDraftId draftId, Guid hostId, string? actor, TimeSpan staleAfter,
        CancellationToken cancellationToken = default) =>
        Guard(() => _store.BeginPublicationAsync(draftId, hostId, actor, staleAfter, cancellationToken: cancellationToken),
            "Accessing workflow catalog or artifacts", cancellationToken: cancellationToken);

    public Task<bool> HeartbeatPublicationAsync(Guid id, Guid hostId, CancellationToken cancellationToken = default) => Guard(
        () => _store.HeartbeatPublicationAsync(id, hostId, cancellationToken: cancellationToken), "Accessing workflow catalog or artifacts",
        cancellationToken: cancellationToken);

    public Task<WorkflowPublication> CompletePublicationAsync(Guid id, Guid hostId, WorkflowPublicationOutcome outcome,
        CancellationToken cancellationToken = default) =>
        Guard(() => _store.CompletePublicationAsync(id, hostId, outcome, cancellationToken: cancellationToken),
            "Accessing workflow catalog or artifacts", cancellationToken: cancellationToken);

    public Task<WorkflowPublication?> GetPublicationAsync(Guid id, CatalogScope scope, CancellationToken cancellationToken = default) =>
        Guard(() => _store.GetPublicationAsync(id, scope, cancellationToken: cancellationToken), "Accessing workflow catalog or artifacts",
            cancellationToken: cancellationToken);

    public Task<WorkflowPublication?> GetLatestPublicationAsync(string kind, string name, int version, Guid generation, CatalogScope scope,
        CancellationToken cancellationToken = default) =>
        Guard(() => _store.GetLatestPublicationAsync(kind, name, version, generation, scope, cancellationToken),
            "Accessing workflow catalog or artifacts", cancellationToken: cancellationToken);

    public Task<CatalogPage<WorkflowPublication>>
        GetPublicationsAsync(WorkflowPublicationQuery query, CancellationToken cancellationToken = default) => Guard(
        () => _store.GetPublicationsAsync(query, cancellationToken: cancellationToken), "Accessing workflow catalog or artifacts",
        cancellationToken: cancellationToken);

    public Task<int> PurgePublicationsAsync(TimeSpan retention, int batchSize, CancellationToken cancellationToken = default) => Guard(
        () => _store.PurgePublicationsAsync(retention, batchSize, cancellationToken), "Accessing workflow catalog or artifacts",
        cancellationToken: cancellationToken);

    public Task<WorkflowDraft?> GetDefinitionAsync(string kind, string name, int version, CancellationToken cancellationToken = default) =>
        Guard(() => _store.GetDefinitionAsync(kind, name, version, cancellationToken: cancellationToken),
            "Accessing workflow catalog or artifacts", cancellationToken: cancellationToken);

    public Task<WorkflowDraft> SaveDefinitionAsync(WorkflowDraft definition, WorkflowDraftId? expected, string? actor = null,
        CancellationToken cancellationToken = default) =>
        Guard(() => _store.SaveDefinitionAsync(definition, expected, actor, cancellationToken: cancellationToken),
            "Accessing workflow catalog or artifacts", cancellationToken: cancellationToken);

    public Task RegisterWorkflowVersionAsync(WorkflowVersion version, IArtifactStore? artifacts = null,
        CancellationToken cancellationToken = default) =>
        Guard(() => _store.RegisterWorkflowVersionAsync(version, artifacts, cancellationToken: cancellationToken),
            "Accessing workflow catalog or artifacts", cancellationToken: cancellationToken);

    public Task<WorkflowVersion?> GetWorkflowVersionAsync(string name, int version, CancellationToken cancellationToken = default) =>
        Guard(() => _store.GetWorkflowVersionAsync(name, version, cancellationToken: cancellationToken),
            "Accessing workflow catalog or artifacts", cancellationToken: cancellationToken);

    public Task<long> GetCatalogRevisionAsync(CancellationToken cancellationToken = default) =>
        Guard(() => _store.GetCatalogRevisionAsync(cancellationToken: cancellationToken), "Reading catalog revision",
            cancellationToken: cancellationToken);

    public Task<WorkflowCatalogChanges> GetWorkflowVersionChangesAsync(long afterRevision, CancellationToken cancellationToken = default) =>
        Guard(() => _store.GetWorkflowVersionChangesAsync(afterRevision, cancellationToken: cancellationToken), "Reading catalog changes",
            cancellationToken: cancellationToken);

    public Task<IReadOnlyList<WorkflowVersion>>
        GetWorkflowVersionsAsync(string? name = null, CancellationToken cancellationToken = default) =>
        Guard(() => _store.GetWorkflowVersionsAsync(name, cancellationToken: cancellationToken), "Accessing workflow catalog or artifacts",
            cancellationToken: cancellationToken);

    public Task<WorkflowVersion> SetWorkflowVersionStateAsync(string name, int version, WorkflowVersionState state,
        CatalogMutationToken expected, IArtifactStore? artifacts = null, string? actor = null,
        CancellationToken cancellationToken = default) =>
        Guard(
            () => _store.SetWorkflowVersionStateAsync(name, version, state, expected, artifacts, actor,
                cancellationToken: cancellationToken), "Accessing workflow catalog or artifacts", cancellationToken: cancellationToken);

    public Task<WorkflowVersion> PublishDefinitionAsync(WorkflowDraftId draftId, WorkflowVersion version, IArtifactStore artifacts,
        string? actor = null, CancellationToken cancellationToken = default) =>
        Guard(() => _store.PublishDefinitionAsync(draftId, version, artifacts, actor, cancellationToken: cancellationToken),
            "Accessing workflow catalog or artifacts", cancellationToken: cancellationToken);

    public Task<int> PurgeArtifactsAsync(IArtifactStore artifacts, TimeSpan retention, int batchSize,
        CancellationToken cancellationToken = default) =>
        Guard(() => _store.PurgeArtifactsAsync(artifacts, retention, batchSize, cancellationToken),
            "Accessing workflow catalog or artifacts", cancellationToken: cancellationToken);

    public async ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken cancellationToken = default) =>
        await Guard(() => _store.PutAsync(kind, content, cancellationToken).AsTask(), "Accessing workflow catalog or artifacts",
            cancellationToken: cancellationToken);

    public async ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) =>
        await Guard(() => _store.OpenAsync(digest, cancellationToken).AsTask(), "Accessing workflow catalog or artifacts",
            cancellationToken: cancellationToken);

    public async ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) =>
        await Guard(() => _store.ExistsAsync(digest, cancellationToken).AsTask(), "Accessing workflow catalog or artifacts",
            cancellationToken: cancellationToken);

    public async ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime olderThanUtc, int batchSize,
        CancellationToken cancellationToken = default) =>
        await Guard(() => _store.PurgeUnreferencedAsync(references, olderThanUtc, batchSize, cancellationToken).AsTask(),
            "Collecting artifacts", cancellationToken: cancellationToken);

    public async ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) =>
        await Guard(() => _store.DeleteAsync(digest, cancellationToken).AsTask(), "Accessing workflow catalog or artifacts",
            cancellationToken: cancellationToken);

    public Task RegisterMemberAsync(MemberRegistration member, CancellationToken cancellationToken = default) =>
        Guard(() => _store.RegisterMemberAsync(member, cancellationToken), "Registering store member",
            cancellationToken: cancellationToken);

    public Task<bool> HeartbeatAsync(Guid memberId, IReadOnlyDictionary<string, string> capabilities,
        CancellationToken cancellationToken = default) =>
        Guard(() => _store.HeartbeatAsync(memberId, capabilities, cancellationToken), "Renewing store member heartbeat",
            cancellationToken: cancellationToken);

    public Task UnregisterMemberAsync(Guid memberId, CancellationToken cancellationToken = default) =>
        Guard(() => _store.UnregisterMemberAsync(memberId, cancellationToken), "Unregistering store member",
            cancellationToken: cancellationToken);

    public Task<IReadOnlyList<WorkflowDemand>> GetWorkflowDemandsAsync(string instanceId, CancellationToken cancellationToken = default) =>
        Guard(() => _store.GetWorkflowDemandsAsync(instanceId, cancellationToken: cancellationToken), "Reading workflow demands",
            cancellationToken: cancellationToken);

    public Task<IReadOnlyList<StoreMember>> GetLiveMembersAsync(CancellationToken cancellationToken = default) =>
        Guard(() => _store.GetLiveMembersAsync(cancellationToken), "Reading live store members", cancellationToken: cancellationToken);

    public Task RegisterPublisherAsync(string name, IReadOnlyCollection<string> eventTypes, bool replayRetained = false,
        CancellationToken cancellationToken = default) =>
        Guard(() => _store.RegisterPublisherAsync(name, eventTypes, replayRetained, cancellationToken: cancellationToken),
            "Accessing workflow outbox", cancellationToken: cancellationToken);

    public Task UnregisterPublisherAsync(string name, string actor = "host", CancellationToken cancellationToken = default) =>
        Guard(() => _store.UnregisterPublisherAsync(name, actor, cancellationToken: cancellationToken),
            "Unregistering workflow outbox publisher", cancellationToken: cancellationToken);

    public Task<DeliveryPage> ListDeliveriesAsync(DeliveryQuery query, CancellationToken cancellationToken = default) =>
        Guard(() => _store.ListDeliveriesAsync(query, cancellationToken: cancellationToken), "Listing workflow outbox deliveries",
            cancellationToken: cancellationToken);

    public Task<bool> RetryDeliveryAsync(string eventId, string publisherName, string actor,
        CancellationToken cancellationToken = default) =>
        Guard(() => _store.RetryDeliveryAsync(eventId, publisherName, actor, cancellationToken: cancellationToken),
            "Retrying workflow outbox delivery", cancellationToken: cancellationToken);

    public Task<IReadOnlyList<OutboxPublisherState>> ListPublishersAsync(CancellationToken cancellationToken = default) =>
        Guard(() => _store.ListPublishersAsync(cancellationToken: cancellationToken), "Listing workflow outbox publishers",
            cancellationToken: cancellationToken);

    public Task<bool> EnablePublisherAsync(string publisherName, string actor, CancellationToken cancellationToken = default) =>
        Guard(() => _store.EnablePublisherAsync(publisherName, actor, cancellationToken: cancellationToken),
            "Enabling workflow outbox publisher", cancellationToken: cancellationToken);

    public Task<IReadOnlyList<OutboxDelivery>> ClaimDeliveriesAsync(string publisherName, string token, TimeSpan leaseDuration, int limit,
        CancellationToken cancellationToken = default) =>
        Guard(() => _store.ClaimDeliveriesAsync(publisherName, token, leaseDuration, limit, cancellationToken: cancellationToken),
            "Accessing workflow outbox",
            cancellationToken: cancellationToken);

    public Task<bool> RenewDeliveryLeaseAsync(string eventId, string publisherName, string token, TimeSpan leaseDuration,
        CancellationToken cancellationToken = default) =>
        Guard(() => _store.RenewDeliveryLeaseAsync(eventId, publisherName, token, leaseDuration, cancellationToken: cancellationToken),
            "Accessing workflow outbox",
            cancellationToken: cancellationToken);

    public Task<bool> SettleDeliveryAsync(string eventId, string publisherName, string token, OutboxPublishResult result,
        CancellationToken cancellationToken = default) =>
        Guard(() => _store.SettleDeliveryAsync(eventId, publisherName, token, result, cancellationToken: cancellationToken),
            "Accessing workflow outbox",
            cancellationToken: cancellationToken);

    public Task<OutboxDelivery?> GetDeliveryAsync(string eventId, string publisherName, CancellationToken cancellationToken = default) =>
        Guard(() => _store.GetDeliveryAsync(eventId, publisherName, cancellationToken: cancellationToken), "Accessing workflow outbox",
            cancellationToken: cancellationToken);

    public Task<OutboxPublisherState?> GetPublisherAsync(string publisherName, CancellationToken cancellationToken = default) =>
        Guard(() => _store.GetPublisherAsync(publisherName, cancellationToken: cancellationToken), "Accessing workflow outbox",
            cancellationToken: cancellationToken);

    public Task<WorkflowControlRequests> GetControlRequestsAsync(IReadOnlyCollection<string> ids) =>
        Guard(() => _store.GetControlRequestsAsync(ids), "Reading execution controls");

    public Task<DebugCommandResult> RequestDebugCommandAsync(string instanceId, DebugCommandRequest command) =>
        Guard(() => _store.RequestDebugCommandAsync(instanceId, command), "Requesting debug command");

    public Task<IReadOnlyDictionary<string, WorkflowDebugState>> GetDebugStatesAsync(IReadOnlyCollection<string> ids) =>
        Guard(() => _store.GetDebugStatesAsync(ids), "Reading debug commands");

    private readonly IWorkflowStateRepository _store;

    private GuardedStateRepository(IWorkflowStateRepository store) => _store = store;

    public static IWorkflowStateRepository Wrap(IWorkflowStateRepository store) =>
        store as GuardedStateRepository ?? new GuardedStateRepository(store);

    public Task<RetentionResult> PurgeAsync(TimeSpan finishedRetention, TimeSpan tombstoneRetention, int batchSize,
        CancellationToken cancellationToken = default) =>
        Guard(() => _store.PurgeAsync(finishedRetention, tombstoneRetention, batchSize, cancellationToken),
            "Purging retained workflows", cancellationToken: cancellationToken);

    public Task<WorkflowTombstone?> GetTombstoneAsync(string instanceId, CancellationToken cancellationToken = default) =>
        Guard(() => _store.GetTombstoneAsync(instanceId, cancellationToken: cancellationToken),
            $"Reading retention tombstone for '{instanceId}'", cancellationToken: cancellationToken);

    public Task<CancellationRequestResult> RequestCancellationAsync(string instanceId, CancellationReason reason,
        Guid? expectedIncarnation = null, CancellationToken cancellationToken = default) =>
        Guard(() => _store.RequestCancellationAsync(instanceId, reason, expectedIncarnation, cancellationToken: cancellationToken),
            $"Requesting cancellation of workflow instance '{instanceId}'", cancellationToken: cancellationToken);

    public Task<CancellationReason?> GetCancellationRequestAsync(string instanceId, CancellationToken cancellationToken = default) =>
        Guard(() => _store.GetCancellationRequestAsync(instanceId, cancellationToken: cancellationToken),
            $"Reading the cancellation request of workflow instance '{instanceId}'", cancellationToken: cancellationToken);

    public Task<IReadOnlyList<RunnableInstance>>
        GetRunnableInstancesAsync(ClaimQuery query, CancellationToken cancellationToken = default) =>
        Guard(() => _store.GetRunnableInstancesAsync(query, cancellationToken: cancellationToken), "Listing runnable workflow instances",
            cancellationToken: cancellationToken);

    public Task<EventAppendResult> AppendEventAsync(WorkflowEvent workflowEvent, CancellationToken cancellationToken = default) =>
        Guard(() => _store.AppendEventAsync(workflowEvent, cancellationToken: cancellationToken),
            $"Storing event '{workflowEvent.EventId}' for workflow instance '{workflowEvent.InstanceId}'",
            cancellationToken: cancellationToken);

    public Task<InboxPage> GetPendingEventsAsync(string instanceId, long afterSequence, int limit,
        CancellationToken cancellationToken = default) =>
        Guard(() => _store.GetPendingEventsAsync(instanceId, afterSequence, limit, cancellationToken: cancellationToken),
            $"Reading the inbox of workflow instance '{instanceId}'", cancellationToken: cancellationToken);

    public Task<StoredWorkflowEvent?> GetEventAsync(string instanceId, string eventId, CancellationToken cancellationToken = default) =>
        Guard(() => _store.GetEventAsync(instanceId, eventId, cancellationToken: cancellationToken),
            $"Reading event '{eventId}' of workflow instance '{instanceId}'", cancellationToken: cancellationToken);

    public Task<StoredWait?> GetWaitAsync(string instanceId, string waitId, CancellationToken cancellationToken = default) =>
        Guard(() => _store.GetWaitAsync(instanceId, waitId, cancellationToken: cancellationToken),
            $"Reading wait reference '{WaitReference.Of(instanceId, waitId)}' of workflow instance '{instanceId}'",
            cancellationToken: cancellationToken);

    public Task<IReadOnlyList<StoredWait>> GetOpenWaitsAsync(WaitQuery query, CancellationToken cancellationToken = default) =>
        Guard(() => _store.GetOpenWaitsAsync(query, cancellationToken: cancellationToken), "Listing open workflow waits",
            cancellationToken: cancellationToken);

    public Task<IReadOnlyDictionary<string, long>> GetInboxSequencesAsync(IReadOnlyCollection<string> instanceIds,
        CancellationToken cancellationToken = default) =>
        Guard(() => _store.GetInboxSequencesAsync(instanceIds, cancellationToken: cancellationToken), "Reading workflow inbox sequences",
            cancellationToken: cancellationToken);

    public Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit, CancellationToken cancellationToken = default) =>
        Guard(() => _store.CommitWorkflowInstanceAsync(commit, cancellationToken: cancellationToken),
            $"Saving workflow instance '{commit.InstanceId}'", preserveArtifactUnavailable: true, cancellationToken: cancellationToken);

    public Task<WorkflowCommitResult> AdmitWorkflowInstanceAsync(CheckpointCommit commit, WorkflowAdmission admission) =>
        Guard(() => _store.AdmitWorkflowInstanceAsync(commit, admission),
            $"Admitting workflow instance '{commit.InstanceId}'", preserveArtifactUnavailable: true);

    public Task<WorkflowOwnership?> GetWorkflowOwnershipAsync(string instanceId) =>
        Guard(() => _store.GetWorkflowOwnershipAsync(instanceId), $"Reading ownership of '{instanceId}'");

    public Task<WorkflowAdmissionRecord?> GetWorkflowAdmissionAsync(string sender, string trigger, string key) =>
        Guard(() => _store.GetWorkflowAdmissionAsync(sender, trigger, key), "Reading workflow admission");

    public Task<ExecutionLeaseGrant?> TryAcquireExecutionLeaseAsync(string instanceId, string token,
        TimeSpan duration, ClaimQuery query, ExecutionClaimKind kind = ExecutionClaimKind.Runnable,
        CancellationToken cancellationToken = default) =>
        Guard(() => _store.TryAcquireExecutionLeaseAsync(instanceId, token, duration, query, kind, cancellationToken: cancellationToken),
            $"Acquiring the execution lease of workflow instance '{instanceId}'", cancellationToken: cancellationToken);

    public Task<IReadOnlyList<ExecutionLeaseRenewalResult>> RenewExecutionLeasesAsync(IReadOnlyList<ExecutionLeaseRenewal> leases,
        CancellationToken cancellationToken = default) =>
        Guard(() => _store.RenewExecutionLeasesAsync(leases, cancellationToken: cancellationToken), "Renewing workflow execution leases",
            cancellationToken: cancellationToken);

    public Task<ExecutionLeaseReleaseResult> ReleaseExecutionLeaseAsync(string instanceId, string token,
        CancellationToken cancellationToken = default) =>
        Guard(() => _store.ReleaseExecutionLeaseAsync(instanceId, token, cancellationToken: cancellationToken),
            $"Releasing the execution lease of workflow instance '{instanceId}'", cancellationToken: cancellationToken);

    public Task<IReadOnlyList<WorkflowDiagnostic>> GetDiagnosticsForIncarnationAsync(string instanceId, Guid incarnation,
        CancellationToken cancellationToken = default) =>
        Guard(() => _store.GetDiagnosticsForIncarnationAsync(instanceId, incarnation, cancellationToken: cancellationToken),
            "reading incarnation-bound workflow diagnostics", cancellationToken: cancellationToken);

    public Task<IReadOnlyList<StoredTraceEntry>> GetTraceAsync(string instanceId, long afterSequence = 0,
        int limit = 1000, CancellationToken cancellationToken = default) =>
        Guard(() => _store.GetTraceAsync(instanceId, afterSequence, limit, cancellationToken: cancellationToken),
            $"Reading the trace of workflow instance '{instanceId}'", cancellationToken: cancellationToken);


    public Task<WorkflowInstance?> GetWorkflowInstanceAsync(string instanceId, CancellationToken cancellationToken = default) =>
        Guard(() => _store.GetWorkflowInstanceAsync(instanceId, cancellationToken: cancellationToken),
            $"Reading workflow instance '{instanceId}'", preserveArtifactUnavailable: true, cancellationToken: cancellationToken);

    public Task<WorkflowInstance?> GetWorkflowInstanceSummaryAsync(string instanceId, CancellationToken cancellationToken = default) =>
        Guard(() => _store.GetWorkflowInstanceSummaryAsync(instanceId, cancellationToken: cancellationToken),
            $"Reading workflow instance '{instanceId}'", preserveArtifactUnavailable: true, cancellationToken: cancellationToken);

    public Task<InstancePage> QueryInstancesAsync(InstanceQuery query, CancellationToken cancellationToken = default) =>
        Guard(() => _store.QueryInstancesAsync(query, cancellationToken: cancellationToken), "Searching workflow instances",
            cancellationToken: cancellationToken);

    public Task<WorkflowControlReceipt> RequestTerminationAsync(string instanceId, CancellationReason reason,
        Guid? expectedIncarnation = null, CancellationToken cancellationToken = default) =>
        Guard(() => _store.RequestTerminationAsync(instanceId, reason, expectedIncarnation, cancellationToken: cancellationToken),
            "Requesting workflow termination", cancellationToken: cancellationToken);


    public Task DeleteWorkflowInstanceAsync(string instanceId, TimeSpan? tombstoneRetention = null,
        CancellationToken cancellationToken = default) =>
        Guard(() => _store.DeleteWorkflowInstanceAsync(instanceId, tombstoneRetention, cancellationToken: cancellationToken),
            $"Deleting workflow instance '{instanceId}'", cancellationToken: cancellationToken);

    private static async Task<T> Guard<T>(Func<Task<T>> call, string operation, bool preserveArtifactUnavailable = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return await call();
        }
        catch (Exception error) when (error is not OperationCanceledException &&
                                      error is not WorkflowEngineException
                                      {
                                          Kind: WorkflowErrorKind.InstanceRemoved or WorkflowErrorKind.ArtifactMismatch
                                          or WorkflowErrorKind.InvalidState or WorkflowErrorKind.InvalidRequest
                                      } &&
                                      !(preserveArtifactUnavailable && error is WorkflowEngineException
                                      {
                                          Kind: WorkflowErrorKind.ArtifactUnavailable or WorkflowErrorKind.ArtifactMismatch
                                      }))
        {
            throw Failure(operation, error);
        }
    }

    private static async Task Guard(Func<Task> call, string operation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await call();
        }
        catch (Exception error) when (error is not OperationCanceledException &&
                                      error is not WorkflowEngineException
                                      {
                                          Kind: WorkflowErrorKind.InstanceRemoved or WorkflowErrorKind.InvalidState
                                          or WorkflowErrorKind.InvalidRequest
                                      })
        {
            throw Failure(operation, error);
        }
    }

    private static WorkflowEngineException Failure(string operation, Exception error) =>
        new(WorkflowErrorKind.Infrastructure, $"{operation} failed: {error.Message}", error);
}