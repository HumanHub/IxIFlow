using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Dapper;
using IxIFlow.Authoring;
using IxIFlow.BundleFixtures;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "registration")]
public sealed class BundleReviewFixTests
{
    private static string Folder() => Path.Combine(Path.GetTempPath(), "IxIFlow.BundleReview", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SharedProfileContainsOnlyReferencedRuntimeAndSdkIdentities()
    {
        var store = new InMemoryWorkflowStateRepository();
        var version = await PublishedBundleScenario.PublishAsync(store);
        var bundle = await ReadAsync(store, version);
        Assert.False(WorkflowSharedAssemblies.IsShared(typeof(FirstData).Assembly.GetName()));
        Assert.False(WorkflowSharedAssemblies.IsShared(new AssemblyName("SystemOfRecord.Contracts")));
        Assert.False(WorkflowSharedAssemblies.IsShared(new AssemblyName("Microsoft.Extensions.Application")));
        Assert.Contains("private/System.Payments.dll", bundle.Entries.Keys);
        var referenced = new HashSet<string>(StringComparer.Ordinal) { "System.Private.CoreLib", "System.Runtime" };
        foreach (var entry in bundle.Entries.Where(pair => pair.Key.EndsWith(".dll", StringComparison.Ordinal)))
        {
            using var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(entry.Value.ToArray()));
            var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            foreach (var handle in metadata.AssemblyReferences)
                referenced.Add(metadata.GetString(metadata.GetAssemblyReference(handle).Name));
        }
        Assert.All(bundle.Manifest.SharedAssemblies.Keys, key => Assert.Contains(new AssemblyName(key).Name!, referenced));
        Assert.DoesNotContain(bundle.Manifest.SharedAssemblies.Keys, key => key.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal));
        Assert.True(WorkflowSharedAssemblies.IsCompatible(bundle.Manifest));
    }

    [Theory]
    [InlineData("archive")]
    [InlineData("manifest")]
    [InlineData("entries")]
    [InlineData("entry")]
    [InlineData("expanded")]
    public async Task ArchiveReadsEnforceEveryResourceBound(string bound)
    {
        var store = new InMemoryWorkflowStateRepository();
        var version = await PublishedBundleScenario.PublishAsync(store);
        var limits = bound switch
        {
            "archive" => new WorkflowBundleReadLimits { MaxArchiveBytes = 1 },
            "manifest" => new WorkflowBundleReadLimits { MaxManifestBytes = 1 },
            "entries" => new WorkflowBundleReadLimits { MaxEntries = 1 },
            "entry" => new WorkflowBundleReadLimits { MaxEntryBytes = 1 },
            _ => new WorkflowBundleReadLimits { MaxExpandedBytes = 1 }
        };
        await using var content = (await store.OpenAsync(version.ArtifactDigest!.Value))!;
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => WorkflowBundleArchive.ReadAsync(
            new NonSeekable(content), version.ArtifactDigest.Value, limits: limits));
        Assert.Equal(WorkflowErrorKind.ResourceExhausted, error.Kind);
    }

    [Fact]
    public async Task LoadedContextSurvivesCacheCorruptionAndAnArtifactOutage()
    {
        var store = new InMemoryWorkflowStateRepository();
        var version = await PublishedBundleScenario.PublishAsync(store);
        var folder = Folder();
        using var catalog = new WorkflowCatalog(store, artifacts: store, bundles: new(folder));
        using var first = await catalog.ResolveWorkflowDefinitionAsync(version.Name, 1);
        await File.WriteAllTextAsync(Path.Combine(folder, version.ArtifactDigest!.Value.Value[7..] + ".bundle"), "corrupt");
        await store.DeleteAsync(version.ArtifactDigest.Value);
        using var second = await catalog.ResolveWorkflowDefinitionAsync(version.Name, 1);
        Assert.Same(first.Definition, second.Definition);
        var privateType = first.Definition.WorkflowDataType!;
        using var types = first.EnterTypes();
        var composite = typeof(Dictionary<,>).MakeGenericType(typeof(string), privateType.MakeArrayType()).MakeArrayType(2);
        Assert.Equal(composite, WorkflowTypeIdentity.Resolve(WorkflowTypeIdentity.SavedName(composite)));
        Assert.Same(first.Definition.BundleContext, AssemblyLoadContext.GetLoadContext(composite.GetElementType()!.GetGenericArguments()[1].GetElementType()!.Assembly));
    }

    [Fact]
    public async Task AnUnwritableCacheStillRunsAVerifiedDownload()
    {
        var store = new InMemoryWorkflowStateRepository();
        var version = await PublishedBundleScenario.PublishAsync(store);
        var folder = Folder();
        Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
        await File.WriteAllTextAsync(folder, "this is a file, not a cache directory");
        using var catalog = new WorkflowCatalog(store, artifacts: store, bundles: new(folder));
        using var lease = await catalog.ResolveWorkflowDefinitionAsync(version.Name, 1);
        Assert.Equal(version.ArtifactDigest, lease.Definition.BundleDigest);
    }

    [Fact]
    public async Task DifferentDigestsLoadIndependentlyAndSameDigestWaitersShareTheFetch()
    {
        var store = new InMemoryWorkflowStateRepository();
        var first = await PublishedBundleScenario.PublishAsync(store);
        var second = await PublishedBundleScenario.PublishAsync(store, 2);
        var artifacts = new ControlledArtifacts(store) { BlockedDigest = first.ArtifactDigest };
        using var catalog = new WorkflowCatalog(store, artifacts: artifacts, bundles: new(Folder()));
        using var cancel = new CancellationTokenSource();
        var cancelled = catalog.ResolveWorkflowDefinitionAsync(first.Name, 1, cancel.Token);
        await artifacts.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var waiting = catalog.ResolveWorkflowDefinitionAsync(first.Name, 1);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        using (var independent = await catalog.ResolveWorkflowDefinitionAsync(second.Name, 2).WaitAsync(TimeSpan.FromSeconds(10)))
            Assert.Equal(second.ArtifactDigest, independent.Definition.BundleDigest);
        artifacts.Release.TrySetResult();
        using var surviving = await waiting;
        Assert.Equal(2, artifacts.Reads);
        using var warm = await catalog.ResolveWorkflowDefinitionAsync(first.Name, 1);
        Assert.Same(surviving.Definition, warm.Definition);
        Assert.Equal(2, artifacts.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledOrTimedOutFetchKeepsCancellationDistinctAndReleasesTheLoad(bool timeout)
    {
        var store = new InMemoryWorkflowStateRepository();
        var version = await PublishedBundleScenario.PublishAsync(store);
        var artifacts = new ControlledArtifacts(store) { BlockedDigest = version.ArtifactDigest };
        using var catalog = new WorkflowCatalog(store, artifacts: artifacts,
            bundles: new(Folder()) { FetchTimeout = timeout ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(30) });
        using var cancel = new CancellationTokenSource();
        var loading = catalog.ResolveWorkflowDefinitionAsync(version.Name, 1, cancel.Token);
        await artifacts.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (!timeout) cancel.Cancel();
        if (timeout)
        {
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => loading);
            Assert.Equal(WorkflowErrorKind.ArtifactUnavailable, error.Kind);
            Assert.True(error.IsInterruption);
            Assert.False(cancel.IsCancellationRequested);
        }
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
        await artifacts.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        artifacts.Release.TrySetResult();
        using var retry = await catalog.ResolveWorkflowDefinitionAsync(version.Name, 1);
        Assert.Equal(version.ArtifactDigest, retry.Definition.BundleDigest);
    }

    [Fact]
    public async Task CancellingAQueuedStartsAcceptanceWaitKeepsItsOriginalContextLeased()
    {
        var store = new InMemoryWorkflowStateRepository();
        var version = await PublishedBundleScenario.PublishAsync(store);
        var services = new ServiceCollection();
        services.AddIxIFlow(options => options.Slots = 1).UseWorkflowBundles(Folder(), 0);
        services.AddSingleton<IWorkflowStateRepository>(store);
        await using var provider = services.BuildServiceProvider();
        var host = provider.GetRequiredService<WorkflowHost>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var occupying = host.RunAsync("occupant", async (_, _, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return new WorkflowExecutionResult { InstanceId = "occupant", Status = WorkflowExecutionStatus.Success };
        }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var catalog = provider.GetRequiredService<IWorkflowCatalog>();
            var lease = await catalog.ResolveWorkflowDefinitionAsync(version.Name, 1);
            var unloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lease.Definition.BundleContext!.Unloading += _ => unloaded.TrySetResult();
            using var cancel = new CancellationTokenSource();
            var client = provider.GetRequiredService<IWorkflowClient>();
            var start = client.StartAsync(version.Name, new FirstData(), new WorkflowOptions { InstanceId = "queued-bundle" }, cancel.Token);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
            lease.Dispose();
            Assert.False(unloaded.Task.IsCompleted);
            release.TrySetResult();
            await occupying;
            await PublishedBundleScenario.UntilAsync(store, "queued-bundle", WorkflowStatus.Suspended);
            await unloaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { release.TrySetResult(); await occupying; }
    }

    [Fact]
    public async Task NamedStartRejectsWrongShapedDataAsInvalidRequest()
    {
        var store = new InMemoryWorkflowStateRepository();
        var version = await PublishedBundleScenario.PublishAsync(store, 2);
        await using var host = PublishedBundleScenario.Host(store, Folder());
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => host.GetRequiredService<IWorkflowClient>()
            .StartAsync(version.Name, new { Value = new { Nested = "not an integer" } }));
        Assert.Equal(WorkflowErrorKind.InvalidRequest, error.Kind);
        Assert.Empty((await store.QueryInstancesAsync(new())).Items);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(33)]
    public async Task InvalidArrayRanksAreNotMatchedAndLeaveTheInstanceSuspended(int rank)
    {
        var store = new InMemoryWorkflowStateRepository();
        var version = await PublishedBundleScenario.PublishAsync(store);
        await using var host = PublishedBundleScenario.Host(store, Folder());
        var client = host.GetRequiredService<IWorkflowClient>();
        var id = await client.StartAsync(version.Name, new FirstData());
        await PublishedBundleScenario.UntilAsync(store, id, WorkflowStatus.Suspended);
        var invalid = "ixi-type:1:A" + rank + ":" + WorkflowTypeIdentity.SavedName(typeof(Signal))[11..];
        Assert.Null(WorkflowTypeIdentity.Resolve(invalid));
        Assert.Equal(EventAppendStatus.Stored, (await store.AppendEventAsync(new(id, "bad-array", invalid, "[]"))).Status);
        var result = await client.RecoverAsync(id);
        Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
        Assert.Equal(EventRejection.NotMatched, (await store.GetEventAsync(id, "bad-array"))!.Rejection);
    }

    [Fact]
    public async Task PublishedParentCatchesTheChildsExceptionInItsOwnContract()
    {
        var store = new InMemoryWorkflowStateRepository();
        await PublishYamlAsync(store, "BundleFailure", """
            workflow: {name: BundleFailure, dataType: IxIFlow.BundleFixtures.FirstData}
            definitions: [{kind: activity, activity: IxIFlow.BundleFixtures.Fail}]
            """);
        await PublishYamlAsync(store, "BundleCatch", """
            workflow: {name: BundleCatch, dataType: IxIFlow.BundleFixtures.FirstData}
            definitions:
              - kind: try
                steps: [{kind: invokeWorkflow, workflow: {name: BundleFailure, version: 1}}]
                catches:
                  - exception: IxIFlow.BundleFixtures.ChildFailure
                    faultType: IxIFlow.BundleFixtures.MessageFault
                    steps:
                      - kind: activity
                        activity: IxIFlow.BundleFixtures.Echo
                        input: [{target: Value, from: {kind: path, path: fault.Message}}]
                        output: [{source: Value, to: {kind: path, path: workflow.Result}}]
            """, options => options.SourceOptions.Workflows["BundleFailure@1"] = new("global::IxIFlow.BundleFixtures.FirstData", 1));
        await using var host = PublishedBundleScenario.Host(store, Folder());
        var client = host.GetRequiredService<IWorkflowClient>();
        var id = await client.StartAsync("BundleCatch", new FirstData());
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var result = await client.AwaitAsync(id, WorkflowAwaitCondition.Finished, stop.Token);
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("published child failed", result.WorkflowData!.GetType().GetProperty("Result")!.GetValue(result.WorkflowData));
    }

    [Fact]
    public async Task RecoveredParentRestoresPreviousBeforeEnteringTheChildsTypeScope()
    {
        var store = new InMemoryWorkflowStateRepository();
        await PublishedBundleScenario.PublishAsync(store);
        var version = await PublishYamlAsync(store, "PreviousParent", """
            workflow: {name: PreviousParent, dataType: IxIFlow.BundleFixtures.FirstData}
            definitions:
              - {kind: activity, activity: IxIFlow.BundleFixtures.Echo}
              - kind: invokeWorkflow
                workflow: {name: BundleLifecycle, version: 1}
                input: [{target: Value, from: {kind: path, path: previousStep.Value}}]
            """, options => options.SourceOptions.Workflows["BundleLifecycle@1"] = new("global::IxIFlow.BundleFixtures.FirstData", 1));
        string id;
        using (var catalog = new WorkflowCatalog(store, artifacts: store, bundles: new(Folder())))
        using (var lease = await catalog.ResolveWorkflowDefinitionAsync(version.Name, 1))
        using (lease.EnterTypes())
        {
            var definition = lease.Definition;
            var (instance, checkpoint, _) = WorkflowInstances.NewInstance(definition,
                Activator.CreateInstance(definition.WorkflowDataType!)!, new WorkflowOptions(), DateTime.UtcNow);
            var continuation = Assert.Single(checkpoint.Continuations);
            continuation.Stack[^1].NextStepIndex = 1;
            continuation.Previous = new SerializedValue
            {
                Type = WorkflowTypeIdentity.SavedName(definition.WorkflowDataType!.Assembly.GetType(typeof(Echo).FullName!)!),
                Json = "{\"Value\":\"recovered parent previous\"}"
            };
            instance.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
            await store.SeedAsync(instance, wakeAfter: TimeSpan.Zero);
            id = instance.InstanceId;
        }
        await using var host = PublishedBundleScenario.Host(store, Folder());
        await host.GetRequiredService<IWorkflowClient>().RecoverAsync(id);
        var child = Assert.Single((await store.QueryInstancesAsync(new(ParentInstanceId: id))).Items);
        var saved = (await store.GetWorkflowInstanceAsync(child.InstanceId))!;
        Assert.Equal("recovered parent previous", JsonDocument.Parse(saved.WorkflowDataJson).RootElement.GetProperty("Value").GetString());
    }

    [Fact]
    public async Task BundleDigestIndexCoversPurgeAndIsRequiredBySchemaValidation()
    {
        var folder = Folder();
        Directory.CreateDirectory(folder);
        var cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(folder, "state.db") }.ConnectionString;
        await using (var store = new SqliteWorkflowStateRepository(cs))
            Assert.Null(await store.GetWorkflowInstanceAsync("missing"));
        await using (var connection = new SqliteConnection(cs))
        {
            var plan = await connection.QueryAsync("EXPLAIN QUERY PLAN SELECT DISTINCT bundle_digest FROM ixiflow_workflow_instances WHERE bundle_digest IS NOT NULL");
            Assert.Contains(plan, row => ((string)row.detail).Contains("COVERING INDEX ix_ixiflow_instances_bundle_digest", StringComparison.Ordinal));
            await connection.ExecuteAsync("DROP INDEX ix_ixiflow_instances_bundle_digest");
        }
        await using var broken = new SqliteWorkflowStateRepository(cs);
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => broken.GetWorkflowInstanceAsync("missing"));
        Assert.Equal(WorkflowErrorKind.InvalidState, error.Kind);
        Assert.Contains("must be recreated", error.Message);
    }

    [Fact]
    public void FaultProjectionsStayCachedWhileTheirTypeKeysAreAlive()
    {
        var weak = CachedProjection();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.True(weak.TryGetTarget(out var projection));
        Assert.Same(projection, FaultProjection.For(typeof(InvalidOperationException), typeof(EmptyFault)));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<FaultProjection> CachedProjection() =>
        new(FaultProjection.For(typeof(InvalidOperationException), typeof(EmptyFault)));

    [Fact]
    public async Task WaitListingAcquiresOncePerDistinctDigest()
    {
        var store = new InMemoryWorkflowStateRepository();
        var version = await PublishedBundleScenario.PublishAsync(store);
        var folder = Folder();
        using var catalog = new WorkflowCatalog(store, artifacts: store, bundles: new(folder, 0));
        var counted = DispatchProxy.Create<IWorkflowCatalog, CatalogReads>();
        var proxy = (CatalogReads)(object)counted;
        proxy.Inner = catalog;
        await using var provider = PublishedBundleScenario.Host(store, folder, 0,
            configure: services => services.AddSingleton(counted));
        var client = provider.GetRequiredService<IWorkflowClient>();
        var first = await client.StartAsync(version.Name, new FirstData());
        var second = await client.StartAsync(version.Name, new FirstData());
        await PublishedBundleScenario.UntilAsync(store, first, WorkflowStatus.Suspended);
        await PublishedBundleScenario.UntilAsync(store, second, WorkflowStatus.Suspended);
        // A terminal run's result read can follow its suspended commit. Drain each instance's
        // existing work through its normal queue before counting the listing's acquisitions.
        var host = provider.GetRequiredService<WorkflowHost>();
        foreach (var id in new[] { first, second })
            await host.RunAsync(id, (_, _, _) => Task.FromResult(new WorkflowExecutionResult { InstanceId = id }), CancellationToken.None);
        proxy.Acquisitions = 0;
        var waits = await client.GetOpenWaitsAsync(new());
        Assert.Equal(2, waits.Count);
        Assert.All(waits, wait => Assert.NotNull(wait.EventSchema));
        Assert.Equal(1, proxy.Acquisitions);
    }

    [Fact]
    public async Task CapabilitySnapshotsRefreshOnlyOnHeartbeatOrCatalogRevisionAndExcludeIncompatibleProfiles()
    {
        var store = new InMemoryWorkflowStateRepository();
        var version = await PublishedBundleScenario.PublishAsync(store);
        var bundle = await ReadAsync(store, version);
        foreach (var defect in new[] { "engine", "checkpoint", "shared" })
        {
            var manifest = defect switch
            {
                "engine" => bundle.Manifest with { EngineVersion = "99.0.0.0" },
                "checkpoint" => bundle.Manifest with { CheckpointContractVersion = 999 },
                _ => bundle.Manifest with { SharedAssemblies = new Dictionary<string, string> { ["System.Payments"] = "1.0.0.0" } }
            };
            await store.RegisterWorkflowVersionAsync(version with { Name = defect, ManifestJson = JsonSerializer.Serialize(manifest) });
        }
        using var catalog = new WorkflowCatalog(store, artifacts: store, bundles: new(Folder()));
        var counted = DispatchProxy.Create<IWorkflowCatalog, CatalogReads>();
        var proxy = (CatalogReads)(object)counted;
        proxy.Inner = catalog;
        var settings = HostSettings.Default with { BundleHostingEnabled = true };
        var first = await settings.GetCapabilitiesAsync(counted);
        Assert.Equal(version.StructureHash, first["workflow:BundleLifecycle@1"]);
        foreach (var defect in new[] { "engine", "checkpoint", "shared" })
            Assert.False(first.ContainsKey($"workflow:{defect}@1"));
        Assert.Same(first, await settings.GetCapabilitiesAsync(counted));
        Assert.Equal(1, proxy.Reads);
        await settings.RefreshCapabilitiesAsync(counted);
        Assert.Equal(2, proxy.Reads);
        await counted.RegisterWorkflowVersionAsync(version);
        await settings.GetCapabilitiesAsync(counted);
        Assert.Equal(3, proxy.Reads);
    }

    public class CatalogReads : DispatchProxy
    {
        public IWorkflowCatalog Inner = null!;
        public int Reads;
        public int Acquisitions;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IWorkflowCatalog.GetWorkflowVersionsAsync)) Reads++;
            if (targetMethod.Name == nameof(IWorkflowCatalog.AcquireWorkflowDefinitionAsync)) Acquisitions++;
            return targetMethod.Invoke(Inner, args);
        }
    }

    private static async Task<WorkflowBundle> ReadAsync(IArtifactStore artifacts, WorkflowVersion version)
    {
        await using var stream = (await artifacts.OpenAsync(version.ArtifactDigest!.Value))!;
        return await WorkflowBundleArchive.ReadAsync(stream, version.ArtifactDigest.Value);
    }

    private static async Task<WorkflowVersion> PublishYamlAsync(IWorkflowStateRepository store, string name, string yaml,
        Action<WorkflowPublisherOptions>? configure = null)
    {
        var options = new WorkflowPublisherOptions();
        configure?.Invoke(options);
        var draft = await store.SaveDefinitionAsync(new(name, 1, "workflow", yaml), 0);
        var published = await new WorkflowPublisher(store, store, options).PublishAsync(draft.Id);
        Assert.True(published.Success, string.Join("; ", published.Diagnostics));
        return published.Version!;
    }

    [Theory]
    [InlineData(CancelKind.Run)]
    [InlineData(CancelKind.Ownership)]
    public async Task AssignmentCancellationInterruptsAcquisitionWithoutWritingState(CancelKind kind)
    {
        var store = new InMemoryWorkflowStateRepository();
        var version = await PublishedBundleScenario.PublishAsync(store);
        var artifacts = new ControlledArtifacts(store) { BlockedDigest = version.ArtifactDigest };
        using var catalog = new WorkflowCatalog(store, artifacts: artifacts, bundles: new(Folder()));
        var services = new ServiceCollection();
        services.AddIxIFlow();
        services.AddSingleton<IWorkflowCatalog>(catalog);
        await using var provider = services.BuildServiceProvider();
        var executor = new WorkflowExecutor(provider, new UnusedExecutionHost());
        var identity = Guid.NewGuid();
        var instance = new WorkflowInstance { WorkflowName = version.Name, WorkflowVersion = 1, BundleDigest = version.ArtifactDigest };
        var run = executor.ExecuteAsync(new Assign(identity, instance, null, version.StructureHash, 1, 0,
            ExecutionLimits.Default, false, []), default);
        await artifacts.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        executor.Deliver(new Cancel(identity, kind));
        var ready = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(kind == CancelKind.Ownership ? AssignmentOutcomeKind.OwnershipLost : AssignmentOutcomeKind.RunCancelled,
            ready.Outcome!.Kind);
        await artifacts.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty((await store.QueryInstancesAsync(new())).Items);
    }

    private sealed class UnusedExecutionHost : IExecutionHost
    {
        public Task<CheckpointReply> CheckpointAsync(Checkpoint request) => throw new InvalidOperationException("Acquisition must not checkpoint");
        public Task<StartChildReply> StartChildAsync(StartChild request) => throw new InvalidOperationException("Acquisition must not start a child");
        public Task<ReadInboxReply> ReadInboxAsync(ReadInbox request) => throw new InvalidOperationException("Acquisition must not read the inbox");
        public Task<ReadWaitReply> ReadWaitAsync(ReadWait request) => throw new InvalidOperationException("Acquisition must not read waits");
    }

    private sealed class ControlledArtifacts(IArtifactStore inner) : IArtifactStore
    {
        public ArtifactDigest? BlockedDigest { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Reads;
        public async ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Reads);
            if (digest == BlockedDigest)
            {
                Entered.TrySetResult();
                try { await Release.Task.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            }
            return await inner.OpenAsync(digest, cancellationToken);
        }
        public ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken cancellationToken = default) => inner.PutAsync(kind, content, cancellationToken);
        public ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) => inner.ExistsAsync(digest, cancellationToken);
        public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) => inner.DeleteAsync(digest, cancellationToken);
        public ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime olderThanUtc, int batchSize, CancellationToken cancellationToken = default) => inner.PurgeUnreferencedAsync(references, olderThanUtc, batchSize, cancellationToken);
    }

    private sealed class NonSeekable(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
