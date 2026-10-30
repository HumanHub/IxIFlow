using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "scopes"), Trait("Spec", "W-06")]
public sealed class CancellationScopeReviewTests
{
    [Fact]
    public async Task ClientSubscriptionFallbackWaitsBeyondWatchdogEvenWhenTheLinkNeverDisconnects()
    {
        var clock = new FakeTimeProvider();
        var store = new InMemoryWorkflowStateRepository(clock);
        var catalog = new WorkflowCatalog();

        ServiceProvider Open() => new ServiceCollection().AddIxIFlow().Services.AddSingleton<IWorkflowStateRepository>(store)
            .AddSingleton<IWorkflowCatalog>(catalog).AddSingleton<TimeProvider>(clock).BuildServiceProvider();

        await using var services = Open();
        await using var writer = Open();
        var client = services.GetRequiredService<IWorkflowClient>();
        var host = services.GetRequiredService<WorkflowHost>();
        var coordination = services.GetRequiredService<CoordinationState>();
        coordination.SubscriptionTimeout = TimeSpan.FromSeconds(25);
        coordination.AddLink();
        var definition = new WorkflowBuilder<Data, object>([], "CancellationSubscriptionBackstop").WaitFor<Go>("done").Build();
        var idle = await client.RunAsync(definition, new Data());
        var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        host.AwaitSubscriptionAsync = async (_, token) =>
        {
            subscribed.TrySetResult();

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                cancelled.TrySetResult();
            }
        };

        var waiting = client.AwaitAsync(idle.InstanceId, WorkflowAwaitCondition.Finished);
        await subscribed.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(WorkflowExecutionStatus.Success,
            (await writer.GetRequiredService<IWorkflowClient>().ResumeAsync(idle.InstanceId, "done", new Go())).Status);

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.False(waiting.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.False(waiting.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(WorkflowExecutionStatus.Success, (await waiting.WaitAsync(TimeSpan.FromSeconds(15))).Status);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(coordination.Connected);
    }

    [Fact]
    public async Task CancellationHandlerForkDeadlineAndWaitsSurviveAStalledCoordinatorSubscription()
    {
        var clock = new FakeTimeProvider();
        var store = new InMemoryWorkflowStateRepository(clock);

        await using var services = new ServiceCollection().AddIxIFlow(o => o.VerifyCheckpointParts = true).Services
            .AddSingleton<IWorkflowStateRepository>(store).AddSingleton<TimeProvider>(clock).BuildServiceProvider();

        var client = services.GetRequiredService<IWorkflowClient>();
        var host = services.GetRequiredService<WorkflowHost>();

        var definition = new WorkflowBuilder<Data, object>([], "CancellationForkSubscription")
            .CancellationScope(b => b.WaitFor<Go>("body"), h => h.Parallel(p =>
                p.Do(b => b.WaitFor<Go>("one")).Do(b => b.Delay(TimeSpan.FromMinutes(1)).WaitFor<Go>("two")))).Build();

        var idle = await client.RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Suspended, (await client.CancelAsync(idle.InstanceId, new() { ReasonCode = "test" })).Status);
        var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var abandoned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriptions = 0;

        host.AwaitSubscriptionAsync = async (_, token) =>
        {
            if (Interlocked.Increment(ref subscriptions) > 1)
                return;

            subscribed.TrySetResult();

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                abandoned.TrySetResult();
            }
        };

        var waiting = client.AwaitAsync(idle.InstanceId);
        await subscribed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(WorkflowExecutionStatus.Suspended, (await waiting.WaitAsync(TimeSpan.FromSeconds(15))).Status);
        await abandoned.Task.WaitAsync(TimeSpan.FromSeconds(15));
        host.AwaitSubscriptionAsync = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        while (!(await client.GetOpenWaitsAsync(new(InstanceId: idle.InstanceId))).Any(w => w.Key == "two"))
            await Task.Delay(10, timeout.Token);

        Assert.Equal(WorkflowExecutionStatus.Suspended, (await client.ResumeAsync(idle.InstanceId, "one", new Go())).Status);
        Assert.Equal(WorkflowExecutionStatus.Cancelled, (await client.ResumeAsync(idle.InstanceId, "two", new Go())).Status);
        Assert.Single(await client.GetTraceAsync(idle.InstanceId), e => e.EntryType == TraceEntryType.CancellationHandlerEntered);
    }

    [Fact]
    public async Task CancellationWhileFaultHandlerWaitsKeepsItsOccurrenceAndReplayUnwindAcrossRestart()
    {
        var clock = new FakeTimeProvider();
        var store = new RecordingStore(clock);
        var catalog = new WorkflowCatalog();

        var definition = new WorkflowBuilder<Data, object>([], "CancellationPreservedFaultHandler")
            .Try(t => t.CancellationScope(b => b.Throw<InvalidOperationException>(c => new("original")),
                h => h.WaitFor<Go>("cleanup").Assign(c => c.WorkflowData.Handler, c => "finished")))
            .Catch<InvalidOperationException>(c => c.Assign(x => x.WorkflowData.Body, x => "caught"))
            .Finally(f => f.WaitFor<Go>("finally").Assign(c => c.WorkflowData.Completed, c => "finally")).Build();

        store.Definition = definition;
        await catalog.RegisterWorkflowAsync(definition);

        ServiceProvider Services() => new ServiceCollection().AddIxIFlow(o =>
            {
                o.Debugging = true;
                o.VerifyCheckpointParts = true;
            }).Services
            .AddSingleton<IWorkflowStateRepository>(store).AddSingleton<IWorkflowCatalog>(catalog).AddSingleton<TimeProvider>(clock)
            .BuildServiceProvider();

        string id;

        await using (var first = Services())
        {
            var client = first.GetRequiredService<IWorkflowClient>();
            id = (await client.RunAsync(definition, new Data())).InstanceId;
            Assert.Equal(WorkflowExecutionStatus.Suspended, (await client.CancelAsync(id, new() { ReasonCode = "test" })).Status);
        }

        await using (var second = Services())
        {
            var result = await second.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "cleanup", new Go());
            Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
            Assert.Equal("finished", Assert.IsType<Data>(result.WorkflowData).Handler);
            Assert.Empty(Assert.IsType<Data>(result.WorkflowData).Body);
        }

        await using var third = Services();
        var done = await third.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "finally", new Go());
        Assert.Equal(WorkflowExecutionStatus.Cancelled, done.Status);
        Assert.Equal("finally", Assert.IsType<Data>(done.WorkflowData).Completed);

        Assert.Single(await third.GetRequiredService<IWorkflowClient>().GetTraceAsync(id),
            e => e.EntryType == TraceEntryType.CancellationHandlerEntered);

        await VerifyReplay(third, definition, id, store);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task ExecutionTimeoutReplayPreservesTheCommittedUnwindWithOrWithoutAHandler(bool handler)
    {
        var clock = new FakeTimeProvider();
        var store = new RecordingStore(clock);
        var builder = new WorkflowBuilder<Data, object>([], "CancellationTimeoutReplay");

        var definition = handler
            ? builder.CancellationScope(b => b.WaitFor<Go>("body"), h => h.WaitFor<Go>("cleanup")).Build()
            : builder.WaitFor<Go>("body").Build();

        store.Definition = definition;

        await using var services = new ServiceCollection().AddIxIFlow(o => o.Debugging = true).Services
            .AddSingleton<IWorkflowStateRepository>(store).AddSingleton<TimeProvider>(clock).BuildServiceProvider();

        var client = services.GetRequiredService<IWorkflowClient>();
        var idle = await client.RunAsync(definition, new Data(), new() { ExecutionTimeout = TimeSpan.FromMinutes(1) });
        clock.Advance(TimeSpan.FromMinutes(2));
        var timed = await client.RecoverAsync(idle.InstanceId);
        Assert.Equal(handler ? WorkflowExecutionStatus.Suspended : WorkflowExecutionStatus.TimedOut, timed.Status);

        if (handler)
            Assert.Equal(WorkflowExecutionStatus.TimedOut, (await client.ResumeAsync(idle.InstanceId, "cleanup", new Go())).Status);

        Assert.Contains(store.Snapshots.SelectMany(s => s.Threads), t => t.Unwind && t.Cause == CancellationUnwindCause.Cancelled);
        await VerifyReplay(services, definition, idle.InstanceId, store);
    }

    private static async Task VerifyReplay(ServiceProvider services, WorkflowDefinition definition, string id, RecordingStore store)
    {
        var entries = await services.GetRequiredService<IWorkflowOperations>().GetTraceAsync(id);
        var replay = TraceReplay.Load(definition, entries);

        foreach (var snapshot in store.Snapshots)
        {
            var boundary = Assert.Single(entries, e => e.Revision == snapshot.Revision && e.CommitEndSequence == e.Sequence);
            replay.Seek(boundary.Sequence);
            Assert.True(replay.Current.IsComplete);

            Assert.Equal(snapshot.Threads,
                replay.Current.Threads.Select(t => (t.ContinuationId, t.CancellationUnwind, t.UnwindCause)).ToArray());
        }
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task ExecutionDeadlineAndExplicitRecoveryKeepOneSavedHandlerInEitherOrder(bool timerFirst)
    {
        var clock = new GatedTimerClock();
        var store = new InMemoryWorkflowStateRepository(clock);
        var catalog = new WorkflowCatalog();
        var observedStore = new RecoveryReadStore(store);

        var definition = new WorkflowBuilder<Data, object>([], "CancellationDeadlineRecoveryRace")
            .CancellationScope(b => b.WaitFor<Go>("body"), h => h.WaitFor<Go>("cleanup")).Build();

        await catalog.RegisterWorkflowAsync(definition);

        ServiceProvider Services() => new ServiceCollection().AddIxIFlow(o => o.Debugging = true).Services
            .AddSingleton<IWorkflowStateRepository>(observedStore).AddSingleton<IWorkflowCatalog>(catalog).AddSingleton<TimeProvider>(clock)
            .BuildServiceProvider();

        var services = Services();

        try
        {
            var client = services.GetRequiredService<IWorkflowClient>();
            var idle = await client.RunAsync(definition, new Data(), new() { ExecutionTimeout = TimeSpan.FromMinutes(1) });
            var host = services.GetRequiredService<WorkflowHost>();

            // A following item for this instance starts only after the preceding recovery's Finish.
            Task DrainRecovery() => host.RunAsync(idle.InstanceId, (_, _, _) => Task.FromResult(true),
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));

            clock.Gate = !timerFirst;
            clock.Advance(TimeSpan.FromMinutes(2));

            if (!timerFirst)
                await clock.Fired.Task.WaitAsync(TimeSpan.FromSeconds(15));

            if (timerFirst)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

                while (!(await client.GetOpenWaitsAsync(new(InstanceId: idle.InstanceId))).Any(w => w.Key == "cleanup"))
                    await Task.Delay(10, timeout.Token);

                await DrainRecovery();
            }

            var explicitDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var timerDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var timerRuns = 0;

            // Test checkpoint reads use the underlying store; only recoveries read through this probe.
            observedStore.InstanceRead = id =>
            {
                if (id != idle.InstanceId)
                    return;

                if (Interlocked.Increment(ref timerRuns) == 1)
                    explicitDone.TrySetResult();
                else
                    timerDone.TrySetResult();
            };

            var recovery = await client.RecoverAsync(idle.InstanceId);
            await explicitDone.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await DrainRecovery();

            if (timerFirst)
            {
                Assert.Equal(WorkflowExecutionStatus.Faulted, recovery.Status);
                Assert.Equal(WorkflowErrorKind.InvalidState, recovery.ErrorKind);
                Assert.Equal("The workflow instance has no interrupted execution to recover", recovery.ErrorMessage);
            }
            else
                Assert.Equal(WorkflowExecutionStatus.Suspended, recovery.Status);

            var saved = (await store.GetWorkflowInstanceAsync(idle.InstanceId))!;
            Assert.Equal(WorkflowStatus.Suspended, saved.Status);
            var state = ExecutionCheckpoint.Read(StoredCheckpoint.WholeState(saved));
            Assert.True(state.TimedOut);
            Assert.Null(state.UnhandledError);
            Assert.Equal("cleanup", Assert.Single(await client.GetOpenWaitsAsync(new(InstanceId: idle.InstanceId))).Key);
            Assert.Single(await client.GetTraceAsync(idle.InstanceId), e => e.EntryType == TraceEntryType.CancellationHandlerEntered);

            if (!timerFirst)
            {
                clock.Release();
                await timerDone.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await DrainRecovery();
                Assert.Equal(2, Volatile.Read(ref timerRuns));
                var afterTimer = (await store.GetWorkflowInstanceAsync(idle.InstanceId))!;
                Assert.Equal(saved.Revision, afterTimer.Revision);
                Assert.Equal(StoredCheckpoint.WholeState(saved), StoredCheckpoint.WholeState(afterTimer));
                Assert.Single(await client.GetTraceAsync(idle.InstanceId), e => e.EntryType == TraceEntryType.CancellationHandlerEntered);
            }

            Assert.Equal(WorkflowExecutionStatus.TimedOut, (await client.ResumeAsync(idle.InstanceId, "cleanup", new Go())).Status);
        }
        finally
        {
            await services.DisposeAsync();
        }
    }

    private sealed class RecoveryReadStore(IWorkflowStateRepository inner) : IWorkflowStateRepository
    {
        public Action<string>? InstanceRead { get; set; }

        public async Task<WorkflowInstance?> GetWorkflowInstanceAsync(string instanceId, CancellationToken cancellationToken = default)
        {
            var instance = await inner.GetWorkflowInstanceAsync(instanceId, cancellationToken: cancellationToken);
            InstanceRead?.Invoke(instanceId);

            return instance;
        }

        public Task<CatalogPage<CatalogWorkflow>> ListWorkflowsAsync(CatalogWorkflowQuery query,
            CancellationToken cancellationToken = default) => inner.ListWorkflowsAsync(query, cancellationToken: cancellationToken);

        public Task<CatalogPage<CatalogWorkflowVersion>> ListWorkflowVersionsAsync(string name, CatalogVersionQuery query,
            CancellationToken cancellationToken = default) =>
            inner.ListWorkflowVersionsAsync(name, query, cancellationToken: cancellationToken);

        public Task<CatalogWorkflowVersionDetail?> GetWorkflowVersionDetailAsync(string name, int version, CatalogScope scope,
            CancellationToken cancellationToken = default) =>
            inner.GetWorkflowVersionDetailAsync(name, version, scope, cancellationToken: cancellationToken);

        public Task<CatalogPage<CatalogTemplate>> ListTemplatesAsync(CatalogTemplateQuery query,
            CancellationToken cancellationToken = default) => inner.ListTemplatesAsync(query, cancellationToken: cancellationToken);

        public Task<CatalogPage<CatalogTemplate>> ListTemplateVersionsAsync(string key, CatalogVersionQuery query,
            CancellationToken cancellationToken = default) =>
            inner.ListTemplateVersionsAsync(key, query, cancellationToken: cancellationToken);

        public Task<CatalogTemplateDetail?> GetTemplateDetailAsync(string key, int version, CatalogScope scope,
            CancellationToken cancellationToken = default) =>
            inner.GetTemplateDetailAsync(key, version, scope, cancellationToken: cancellationToken);

        public Task<IReadOnlyList<CatalogStepDescriptor>> ListStepDescriptorsAsync(CatalogScope scope,
            CancellationToken cancellationToken = default) => inner.ListStepDescriptorsAsync(scope, cancellationToken: cancellationToken);

        public Task<CatalogPage<CatalogActivity>> ListActivitiesAsync(CatalogActivityQuery query,
            CancellationToken cancellationToken = default) => inner.ListActivitiesAsync(query, cancellationToken: cancellationToken);

        public Task<CatalogPage<CatalogActivity>> ListActivityVersionsAsync(string key, CatalogVersionQuery query,
            CancellationToken cancellationToken = default) =>
            inner.ListActivityVersionsAsync(key, query, cancellationToken: cancellationToken);

        public Task<CatalogActivityDetail?> GetActivityAsync(string key, string version, ActivityProviderSelector provider,
            CatalogScope scope, CancellationToken cancellationToken = default) =>
            inner.GetActivityAsync(key, version, provider, scope, cancellationToken: cancellationToken);

        public Task<CatalogPage<CatalogPackage>>
            ListPackagesAsync(CatalogPackageQuery query, CancellationToken cancellationToken = default) =>
            inner.ListPackagesAsync(query, cancellationToken: cancellationToken);

        public Task<CatalogPage<CatalogPackage>> ListPackageVersionsAsync(string id, CatalogVersionQuery query,
            CancellationToken cancellationToken = default) =>
            inner.ListPackageVersionsAsync(id, query, cancellationToken: cancellationToken);

        public Task<CatalogPackageDetail?> GetPackageDetailAsync(string id, string version, CatalogScope scope,
            CancellationToken cancellationToken = default) =>
            inner.GetPackageDetailAsync(id, version, scope, cancellationToken: cancellationToken);

        public Task<CatalogPage<CatalogActivity>> GetPackageActivitiesAsync(string id, string version, CatalogVersionQuery query,
            CancellationToken cancellationToken = default) =>
            inner.GetPackageActivitiesAsync(id, version, query, cancellationToken: cancellationToken);

        public Task<IReadOnlyList<WorkflowPackage>> GetPendingPackageDependentsAsync(string missingPackageId, string? afterIdentity,
            int limit, CancellationToken cancellationToken = default) =>
            inner.GetPendingPackageDependentsAsync(missingPackageId, afterIdentity, limit, cancellationToken: cancellationToken);

        public Task<IxIFlow.Templates.TemplateVersionEntry> RegisterTemplateVersionAsync(IxIFlow.Templates.TemplateVersion version,
            IArtifactStore artifacts, string? actor = null, CancellationToken cancellationToken = default) =>
            inner.RegisterTemplateVersionAsync(version, artifacts, actor, cancellationToken: cancellationToken);

        public Task<IxIFlow.Templates.TemplateVersionEntry?> GetTemplateVersionAsync(string key, int version,
            CancellationToken cancellationToken = default) =>
            inner.GetTemplateVersionAsync(key, version, cancellationToken: cancellationToken);

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

        public Task<PackageRegistration> RegisterPackageAsync(WorkflowPackage package, PackageActivities activities,
            IArtifactStore artifacts, string? actor = null, CancellationToken cancellationToken = default) =>
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

        public Task<WorkflowDraft?>
            GetDefinitionAsync(string kind, string name, int version, CancellationToken cancellationToken = default) =>
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

        public Task<WorkflowCatalogChanges> GetWorkflowVersionChangesAsync(long afterRevision,
            CancellationToken cancellationToken = default) =>
            inner.GetWorkflowVersionChangesAsync(afterRevision, cancellationToken: cancellationToken);

        public Task<IReadOnlyList<WorkflowVersion>> GetWorkflowVersionsAsync(string? name = null,
            CancellationToken cancellationToken = default) => inner.GetWorkflowVersionsAsync(name, cancellationToken: cancellationToken);

        public Task<WorkflowVersion> SetWorkflowVersionStateAsync(string name, int version, WorkflowVersionState state,
            CatalogMutationToken expected, IArtifactStore? artifacts = null, string? actor = null,
            CancellationToken cancellationToken = default) =>
            inner.SetWorkflowVersionStateAsync(name, version, state, expected, artifacts, actor, cancellationToken: cancellationToken);

        public Task<WorkflowVersion> PublishDefinitionAsync(WorkflowDraftId draftId, WorkflowVersion version, IArtifactStore artifacts,
            string? actor = null, CancellationToken cancellationToken = default) =>
            inner.PublishDefinitionAsync(draftId, version, artifacts, actor, cancellationToken: cancellationToken);

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

        public Task<WorkflowPublication?> GetLatestPublicationAsync(string kind, string name, int version, Guid generation,
            CatalogScope scope, CancellationToken cancellationToken = default) =>
            inner.GetLatestPublicationAsync(kind, name, version, generation, scope, cancellationToken);

        public Task<CatalogPage<WorkflowPublication>> GetPublicationsAsync(WorkflowPublicationQuery query,
            CancellationToken cancellationToken = default) => inner.GetPublicationsAsync(query, cancellationToken: cancellationToken);

        public Task<int> PurgePublicationsAsync(TimeSpan retention, int batchSize, CancellationToken cancellationToken = default) =>
            inner.PurgePublicationsAsync(retention, batchSize, cancellationToken);

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

        public Task<UnschedulablePage> GetUnschedulableAsync(int limit = 100, UnschedulableCursor? cursor = null,
            CancellationToken cancellationToken = default) =>
            inner.GetUnschedulableAsync(limit, cursor, cancellationToken);

        public Task<bool> CanScheduleAsync(IReadOnlyList<WorkflowDemand> demands, CancellationToken cancellationToken = default) =>
            inner.CanScheduleAsync(demands, cancellationToken);

        public Task<IReadOnlyList<WorkflowDemand>>
            GetWorkflowDemandsAsync(string instanceId, CancellationToken cancellationToken = default) =>
            inner.GetWorkflowDemandsAsync(instanceId, cancellationToken: cancellationToken);

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

        public Task<WorkflowTombstone?> GetTombstoneAsync(string instanceId, CancellationToken cancellationToken = default) =>
            inner.GetTombstoneAsync(instanceId, cancellationToken: cancellationToken);

        public Task<WorkflowCommitResult> AdmitWorkflowInstanceAsync(CheckpointCommit commit, WorkflowAdmission admission) =>
            inner.AdmitWorkflowInstanceAsync(commit, admission);

        public Task<WorkflowOwnership?> GetWorkflowOwnershipAsync(string instanceId) => inner.GetWorkflowOwnershipAsync(instanceId);

        public Task<WorkflowAdmissionRecord?> GetWorkflowAdmissionAsync(string sender, string trigger, string key) =>
            inner.GetWorkflowAdmissionAsync(sender, trigger, key);

        public Task<EventAppendResult> AppendEventAsync(WorkflowEvent workflowEvent, CancellationToken cancellationToken = default) =>
            inner.AppendEventAsync(workflowEvent, cancellationToken: cancellationToken);

        public Task<InboxPage> GetPendingEventsAsync(string instanceId, long afterSequence, int limit,
            CancellationToken cancellationToken = default) =>
            inner.GetPendingEventsAsync(instanceId, afterSequence, limit, cancellationToken: cancellationToken);

        public Task<StoredWorkflowEvent?> GetEventAsync(string instanceId, string eventId, CancellationToken cancellationToken = default) =>
            inner.GetEventAsync(instanceId, eventId, cancellationToken: cancellationToken);

        public Task<StoredWait?> GetWaitAsync(string instanceId, string waitId, CancellationToken cancellationToken = default) =>
            inner.GetWaitAsync(instanceId, waitId, cancellationToken: cancellationToken);

        public Task<IReadOnlyDictionary<string, long>> GetInboxSequencesAsync(IReadOnlyCollection<string> instanceIds,
            CancellationToken cancellationToken = default) =>
            inner.GetInboxSequencesAsync(instanceIds, cancellationToken: cancellationToken);

        public Task<IReadOnlyList<StoredWait>> GetOpenWaitsAsync(WaitQuery query, CancellationToken cancellationToken = default) =>
            inner.GetOpenWaitsAsync(query, cancellationToken: cancellationToken);

        public Task<WorkflowInstance?> GetWorkflowInstanceSummaryAsync(string instanceId, CancellationToken cancellationToken = default) =>
            inner.GetWorkflowInstanceSummaryAsync(instanceId, cancellationToken: cancellationToken);

        public Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit,
            CancellationToken cancellationToken = default) =>
            inner.CommitWorkflowInstanceAsync(commit, cancellationToken: cancellationToken);

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

        public Task<IReadOnlyList<RunnableInstance>> GetRunnableInstancesAsync(ClaimQuery query,
            CancellationToken cancellationToken = default) => inner.GetRunnableInstancesAsync(query, cancellationToken: cancellationToken);

        public Task<ExecutionLeaseGrant?> TryAcquireExecutionLeaseAsync(string instanceId, string token, TimeSpan duration,
            ClaimQuery query, ExecutionClaimKind kind = ExecutionClaimKind.Runnable, CancellationToken cancellationToken = default) =>
            inner.TryAcquireExecutionLeaseAsync(instanceId, token, duration, query, kind);

        public Task<IReadOnlyList<ExecutionLeaseRenewalResult>> RenewExecutionLeasesAsync(IReadOnlyList<ExecutionLeaseRenewal> leases,
            CancellationToken cancellationToken = default) => inner.RenewExecutionLeasesAsync(leases, cancellationToken: cancellationToken);

        public Task<ExecutionLeaseReleaseResult> ReleaseExecutionLeaseAsync(string instanceId, string token,
            CancellationToken cancellationToken = default) => inner.ReleaseExecutionLeaseAsync(instanceId, token);

        public Task<InstancePage> QueryInstancesAsync(InstanceQuery query, CancellationToken cancellationToken = default) =>
            inner.QueryInstancesAsync(query, cancellationToken: cancellationToken);

        public Task<WorkflowControlReceipt> RequestTerminationAsync(string instanceId, CancellationReason reason,
            Guid? expectedIncarnation = null, CancellationToken cancellationToken = default) =>
            inner.RequestTerminationAsync(instanceId, reason, expectedIncarnation, cancellationToken: cancellationToken);

        public Task<IReadOnlyList<WorkflowDiagnostic>> GetDiagnosticsForIncarnationAsync(string instanceId, Guid incarnation,
            CancellationToken cancellationToken = default) =>
            inner.GetDiagnosticsForIncarnationAsync(instanceId, incarnation, cancellationToken: cancellationToken);

        public Task<IReadOnlyList<StoredTraceEntry>> GetTraceAsync(string instanceId, long afterSequence = 0, int limit = 1000,
            CancellationToken cancellationToken = default) =>
            inner.GetTraceAsync(instanceId, afterSequence, limit, cancellationToken: cancellationToken);

        public Task DeleteWorkflowInstanceAsync(string instanceId, TimeSpan? tombstoneRetention = null,
            CancellationToken cancellationToken = default) =>
            inner.DeleteWorkflowInstanceAsync(instanceId, tombstoneRetention, cancellationToken: cancellationToken);

        public Task RegisterPublisherAsync(string name, IReadOnlyCollection<string> eventTypes, bool replayRetained = false,
            CancellationToken cancellationToken = default) =>
            inner.RegisterPublisherAsync(name, eventTypes, replayRetained, cancellationToken: cancellationToken);

        public Task UnregisterPublisherAsync(string name, string actor = "host", CancellationToken cancellationToken = default) =>
            inner.UnregisterPublisherAsync(name, actor, cancellationToken: cancellationToken);

        public Task<DeliveryPage> ListDeliveriesAsync(DeliveryQuery query, CancellationToken cancellationToken = default) =>
            inner.ListDeliveriesAsync(query, cancellationToken: cancellationToken);

        public Task<bool> RetryDeliveryAsync(string eventId, string publisherName, string actor,
            CancellationToken cancellationToken = default) =>
            inner.RetryDeliveryAsync(eventId, publisherName, actor, cancellationToken: cancellationToken);

        public Task<IReadOnlyList<OutboxPublisherState>> ListPublishersAsync(CancellationToken cancellationToken = default) =>
            inner.ListPublishersAsync(cancellationToken: cancellationToken);

        public Task<bool> EnablePublisherAsync(string publisherName, string actor, CancellationToken cancellationToken = default) =>
            inner.EnablePublisherAsync(publisherName, actor, cancellationToken: cancellationToken);

        public Task<IReadOnlyList<OutboxDelivery>> ClaimDeliveriesAsync(string publisherName, string token, TimeSpan leaseDuration,
            int limit, CancellationToken cancellationToken = default) =>
            inner.ClaimDeliveriesAsync(publisherName, token, leaseDuration, limit);

        public Task<bool> RenewDeliveryLeaseAsync(string eventId, string publisherName, string token, TimeSpan leaseDuration,
            CancellationToken cancellationToken = default) =>
            inner.RenewDeliveryLeaseAsync(eventId, publisherName, token, leaseDuration);

        public Task<bool> SettleDeliveryAsync(string eventId, string publisherName, string token, OutboxPublishResult result,
            CancellationToken cancellationToken = default) =>
            inner.SettleDeliveryAsync(eventId, publisherName, token, result);

        public Task<OutboxDelivery?>
            GetDeliveryAsync(string eventId, string publisherName, CancellationToken cancellationToken = default) =>
            inner.GetDeliveryAsync(eventId, publisherName, cancellationToken: cancellationToken);

        public Task<OutboxPublisherState?> GetPublisherAsync(string publisherName, CancellationToken cancellationToken = default) =>
            inner.GetPublisherAsync(publisherName, cancellationToken: cancellationToken);
    }

    // Hold the original host's already scheduled callback, without disposing its timer/host.
    private sealed class GatedTimerClock : TimeProvider
    {
        private readonly FakeTimeProvider _clock = new();
        private Action? _dispatch;
        public bool Gate { get; set; }
        public TaskCompletionSource Fired { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow();
        public override long GetTimestamp() => _clock.GetTimestamp();
        public override long TimestampFrequency => _clock.TimestampFrequency;
        public void Advance(TimeSpan amount) => _clock.Advance(amount);
        public void Release() => Interlocked.Exchange(ref _dispatch, null)!();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            _clock.CreateTimer(s =>
            {
                if (Gate && dueTime == Timeout.InfiniteTimeSpan && period == Timeout.InfiniteTimeSpan)
                {
                    _dispatch = () => callback(s);
                    Fired.TrySetResult();
                }
                else
                    callback(s);
            }, state, dueTime, period);
    }

    [Fact]
    public async Task NestedFaultHandlerReturnsToItsLocalCatchDuringCancellation()
    {
        var store = new InMemoryWorkflowStateRepository();
        var catalog = new WorkflowCatalog();
        var effects = new Effects();

        var definition = new WorkflowBuilder<Data, object>([], "CancellationNestedFaultHandler")
            .CancellationScope(b => b.WaitFor<Go>("body"), h => h.Try(t => t.CancellationScope(
                    b => b.Throw<InvalidOperationException>(c => new("local")), h => h.WaitFor<Go>("nested")))
                .Catch<InvalidOperationException>(c => c.Assign(x => x.WorkflowData.Handler, x => "caught"))
                .Step<FinishCleanup>()).Build();

        await catalog.RegisterWorkflowAsync(definition);
        string id;

        await using (var first = Open(store, catalog, effects))
        {
            var client = first.GetRequiredService<IWorkflowClient>();
            id = (await client.RunAsync(definition, new Data())).InstanceId;
            Assert.Equal(WorkflowExecutionStatus.Suspended, (await client.CancelAsync(id, new() { ReasonCode = "test" })).Status);
        }

        await using var second = Open(store, catalog, effects);
        var done = await second.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "nested", new Go());
        Assert.Equal(WorkflowExecutionStatus.Cancelled, done.Status);
        Assert.Equal("caught", Assert.IsType<Data>(done.WorkflowData).Handler);
        Assert.Equal(1, effects.Finished);
    }

    [Theory]
    [InlineData(false, false), InlineData(false, true), InlineData(true, false), InlineData(true, true)]
    public async Task NormalNestedBodyReturnsToItsContainingCleanup(bool finallyBody, bool restart)
    {
        var store = new InMemoryWorkflowStateRepository();
        var catalog = new WorkflowCatalog();
        var effects = new Effects();
        var builder = new WorkflowBuilder<Data, object>([], "CancellationNestedCleanup");

        var definition = finallyBody
            ? builder.Try(b => b.WaitFor<Go>("body")).Finally(h => h.WaitFor<Go>("cleanup")
                .CancellationScope(b =>
                {
                }, h => h.Step<UnexpectedHandler>()).Step<FinishCleanup>()).Build()
            : builder.CancellationScope(b => b.WaitFor<Go>("body"), h => h.WaitFor<Go>("cleanup")
                .CancellationScope(b =>
                {
                }, h => h.Step<UnexpectedHandler>()).Step<FinishCleanup>()).Build();

        await catalog.RegisterWorkflowAsync(definition);
        var services = Open(store, catalog, effects);

        try
        {
            var client = services.GetRequiredService<IWorkflowClient>();
            var idle = await client.RunAsync(definition, new Data());

            Assert.Equal(WorkflowExecutionStatus.Suspended,
                (await client.CancelAsync(idle.InstanceId, new() { ReasonCode = "test" })).Status);

            if (restart)
            {
                await services.DisposeAsync();
                services = Open(store, catalog, effects);
                client = services.GetRequiredService<IWorkflowClient>();
            }

            var done = await client.ResumeAsync(idle.InstanceId, "cleanup", new Go());
            Assert.Equal(WorkflowExecutionStatus.Cancelled, done.Status);
            Assert.Equal(1, effects.Finished);
        }
        finally
        {
            await services.DisposeAsync();
        }
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task EventPreviousSurvivesEveryBodyAndHandlerCheckpoint(bool cancel)
    {
        var store = new InMemoryWorkflowStateRepository();
        var catalog = new WorkflowCatalog();

        var definition = new WorkflowBuilder<Data, object>([], "CancellationEventPrevious").WaitFor<Entry>("entry")
            .CancellationScope(b => b.Assign(c => c.WorkflowData.Body, c => c.PreviousStep.Text).WaitFor<Go>("body"),
                h => h.Assign(c => c.WorkflowData.Handler, c => c.PreviousStep.Text)
                    .CancellationScope(b => b.WaitFor<Go>("handler"), h =>
                    {
                    })
                    .Assign(c => c.WorkflowData.AfterHandler, c => c.PreviousStep.Text))
            .Assign(c => c.WorkflowData.Completed, c => c.PreviousStep.Text).Build();

        await catalog.RegisterWorkflowAsync(definition);
        string id;

        await using (var first = Open(store, catalog))
            id = (await first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data())).InstanceId;

        var text = new string('p', 8192);

        await using (var second = Open(store, catalog))
        {
            var body = await second.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "entry", new Entry { Text = text });
            Assert.Equal(WorkflowExecutionStatus.Suspended, body.Status);
            Assert.Equal(text, Assert.IsType<Data>(body.WorkflowData).Body);
        }

        await using (var third = Open(store, catalog))
        {
            var client = third.GetRequiredService<IWorkflowClient>();

            var result = cancel
                ? await client.CancelAsync(id, new() { ReasonCode = "test" })
                : await client.ResumeAsync(id, "body", new Go());

            Assert.Equal(cancel ? WorkflowExecutionStatus.Suspended : WorkflowExecutionStatus.Success, result.Status);
            var data = Assert.IsType<Data>(result.WorkflowData);
            Assert.Equal(text, cancel ? data.Handler : data.Completed);
        }

        if (cancel)
        {
            await using var fourth = Open(store, catalog);
            var done = await fourth.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "handler", new Go());
            Assert.Equal(WorkflowExecutionStatus.Cancelled, done.Status);
            Assert.Equal(text, Assert.IsType<Data>(done.WorkflowData).AfterHandler);
        }
    }

    private static ServiceProvider Open(IWorkflowStateRepository repository, IWorkflowCatalog catalog, Effects? effects = null)
    {
        var services = new ServiceCollection();

        services.AddIxIFlow(o =>
        {
            o.VerifyCheckpointParts = true;
            o.PayloadThresholdBytes = 1;
        });

        services.AddSingleton(repository);
        services.AddSingleton(catalog);
        services.AddSingleton(effects ?? new Effects());

        return services.BuildServiceProvider();
    }

    public sealed class Effects
    {
        public int Finished;
    }

    public sealed class Data
    {
        public string Body { get; set; } = "";
        public string Handler { get; set; } = "";
        public string AfterHandler { get; set; } = "";
        public string Completed { get; set; } = "";
    }

    public sealed class Entry
    {
        public string Text { get; set; } = "";
    }

    public sealed class Go;

    public sealed class FinishCleanup(Effects effects) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            effects.Finished++;

            return Task.CompletedTask;
        }
    }

    public sealed class UnexpectedHandler : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A completed nested body must not be cancelled");
    }

    private sealed class RecordingStore : TestWorkflowStateRepository
    {
        private readonly TimeProvider _clock;
        public RecordingStore(TimeProvider clock) : base(clock) => _clock = clock;
        public WorkflowDefinition Definition { get; set; } = null!;
        public List<(long Revision, (string Id, bool Unwind, CancellationUnwindCause? Cause)[] Threads)> Snapshots { get; } = [];

        public override async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit,
            CancellationToken cancellationToken = default)
        {
            var result = await base.CommitWorkflowInstanceAsync(commit, cancellationToken: cancellationToken);

            if (commit.TraceEntries.Count > 0)
            {
                var instance = (await GetWorkflowInstanceAsync(commit.InstanceId))!;
                var state = ExecutionCheckpoint.Read(StoredCheckpoint.WholeState(instance));

                var expected = state.Continuations.Where(c => c.Stack.Count > 0 &&
                        c.Status is not (ContinuationStatus.Completed or ContinuationStatus.Cancelled))
                    .Select(c => (c.Id, c.CancellationUnwind, c.UnwindCause)).ToArray();

                var snapshot = ExecutionSnapshotFactory.Create(instance, state, WorkflowDefinitionValidator.ForExecution(Definition),
                    _clock.GetUtcNow().UtcDateTime);

                Assert.Equal(expected, snapshot.Pointers.Select(p => (p.PointerId, p.CancellationUnwind, p.UnwindCause)).ToArray());
                Snapshots.Add((instance.Revision, expected));
            }

            return result;
        }
    }
}