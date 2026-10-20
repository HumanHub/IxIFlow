using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using IxIFlow.BundleFixtures;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Spec", "F-02")]
[Trait("Spec", "F-04")]
[Trait("Spec", "F-13")]
[Trait("Spec", "C-06")]
[Trait("Feature", "registration")]
public sealed class BundleLoadingTests
{
    private static string Folder() => Path.Combine(Path.GetTempPath(), "IxIFlow.BundleTests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishStartSuspendRecoverOnAnotherHostComplete(bool sqlite)
    {
        var root = Folder();
        Directory.CreateDirectory(root);
        var memory = new InMemoryWorkflowStateRepository();
        var connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "state.db") }.ConnectionString;
        await PublishedBundleScenario.CrossHostAsync(() => sqlite ? new SqliteWorkflowStateRepository(connection) : memory, root);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FailedFetchKeepsCheckpointAndRecoversWhenArtifactReturns(bool sqlite, bool corrupt)
    {
        var root = Folder();
        Directory.CreateDirectory(root);
        IWorkflowStateRepository store = sqlite
            ? new SqliteWorkflowStateRepository(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "state.db") }.ConnectionString)
            : new InMemoryWorkflowStateRepository();
        try
        {
            var version = await PublishedBundleScenario.PublishAsync(store);
            string id;
            await using (var first = PublishedBundleScenario.Host(store, Path.Combine(root, "first")))
            {
                id = await first.GetRequiredService<IWorkflowClient>().StartAsync(version.Name, 1, new FirstData());
                await PublishedBundleScenario.UntilAsync(store, id, WorkflowStatus.Suspended);
            }
            var checkpoint = (await store.GetWorkflowInstanceAsync(id))!.ExecutionStateJson;
            var artifacts = new FetchFaults(store) { Missing = !corrupt, Corrupt = corrupt };
            await using var second = PublishedBundleScenario.Host(store, Path.Combine(root, "second"), artifacts: artifacts);
            var client = second.GetRequiredService<IWorkflowClient>();
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => client.ResumeAsync(id, new Signal { Value = "recovered" }));
            Assert.Equal(corrupt ? WorkflowErrorKind.ArtifactMismatch : WorkflowErrorKind.ArtifactUnavailable, error.Kind);
            Assert.True(error.IsInterruption);
            var saved = (await store.GetWorkflowInstanceAsync(id))!;
            Assert.Equal(WorkflowStatus.Suspended, saved.Status);
            Assert.Equal(checkpoint, saved.ExecutionStateJson);
            artifacts.Missing = artifacts.Corrupt = false;
            Assert.Equal(WorkflowExecutionStatus.Success, (await client.RecoverAsync(id)).Status);
        }
        finally { if (store is IAsyncDisposable disposable) await disposable.DisposeAsync(); }
    }

    [Fact]
    public async Task WarmCacheIsReverifiedAndCorruptionIsRejectedBeforeExecution()
    {
        var store = new InMemoryWorkflowStateRepository();
        var version = await PublishedBundleScenario.PublishAsync(store);
        var folder = Folder();
        await using var host = PublishedBundleScenario.Host(store, folder);
        var client = host.GetRequiredService<IWorkflowClient>();
        var id = await client.StartAsync(version.Name, new FirstData());
        await PublishedBundleScenario.UntilAsync(store, id, WorkflowStatus.Suspended);
        var checkpoint = (await store.GetWorkflowInstanceAsync(id))!.ExecutionStateJson;
        var path = Path.Combine(folder, version.ArtifactDigest!.Value.Value[7..] + ".bundle");
        await File.WriteAllTextAsync(path, "corrupt cache");
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => client.ResumeAsync(id, new Signal()));
        Assert.Equal(WorkflowErrorKind.ArtifactMismatch, error.Kind);
        Assert.False(File.Exists(path));
        Assert.Equal(checkpoint, (await store.GetWorkflowInstanceAsync(id))!.ExecutionStateJson);
        Assert.Equal(WorkflowExecutionStatus.Success, (await client.RecoverAsync(id)).Status);
    }

    [Fact]
    public async Task VersionsHaveIsolatedDataTypesAndPinsAndLatestStarts()
    {
        var store = new InMemoryWorkflowStateRepository();
        var one = await PublishedBundleScenario.PublishAsync(store);
        var two = await PublishedBundleScenario.PublishAsync(store, 2);
        await using var host = PublishedBundleScenario.Host(store, Folder());
        var client = host.GetRequiredService<IWorkflowClient>();
        var first = await client.StartAsync(one.Name, 1, new FirstData { Value = "one" });
        var second = await client.StartAsync(two.Name, new SecondData { Value = 2 });
        await PublishedBundleScenario.UntilAsync(store, first, WorkflowStatus.Suspended);
        await PublishedBundleScenario.UntilAsync(store, second, WorkflowStatus.Suspended);
        Assert.Equal(one.ArtifactDigest, (await store.GetWorkflowInstanceAsync(first))!.BundleDigest);
        Assert.Equal(two.ArtifactDigest, (await store.GetWorkflowInstanceAsync(second))!.BundleDigest);
        var firstResult = await client.ResumeAsync(first, new Signal { Value = "first" });
        var secondResult = await client.ResumeAsync(second, new Signal { Value = "second" });
        Assert.Equal(WorkflowExecutionStatus.Success, firstResult.Status);
        Assert.Equal(WorkflowExecutionStatus.Success, secondResult.Status);
        Assert.NotSame(firstResult.WorkflowData!.GetType().Assembly, secondResult.WorkflowData!.GetType().Assembly);
        Assert.NotSame(AssemblyLoadContext.GetLoadContext(firstResult.WorkflowData.GetType().Assembly),
            AssemblyLoadContext.GetLoadContext(secondResult.WorkflowData.GetType().Assembly));
        Assert.Equal("first", firstResult.WorkflowData.GetType().GetProperty("Result")!.GetValue(firstResult.WorkflowData));
        Assert.Equal("second", secondResult.WorkflowData.GetType().GetProperty("Result")!.GetValue(secondResult.WorkflowData));
        var demands = WorkflowDemands.Saved((await store.GetWorkflowInstanceAsync(first))!);
        Assert.Contains(demands, demand => demand.Key == "bundle-host");
        Assert.Contains(demands, demand => demand.Key == "workflow:BundleLifecycle@1" && demand.Value == one.StructureHash);
        Assert.NotNull(await client.GetSnapshotAsync(first));
    }

    [Fact]
    public async Task PublishedChildKeepsItsOwnPinAcrossParentRecovery()
    {
        var store = new InMemoryWorkflowStateRepository();
        var childVersion = await PublishedBundleScenario.PublishAsync(store);
        var parentVersion = await PublishedBundleScenario.PublishParentAsync(store);
        string parentId;
        WorkflowInstance child;
        await using (var first = PublishedBundleScenario.Host(store, Folder()))
        {
            parentId = await first.GetRequiredService<IWorkflowClient>().StartAsync(parentVersion.Name, new FirstData { Value = "child input" });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                var children = await store.QueryInstancesAsync(new InstanceQuery(ParentInstanceId: parentId));
                if (children.Items.Count == 1 && (await store.GetWorkflowInstanceAsync(children.Items[0].InstanceId)) is { Status: WorkflowStatus.Suspended } saved)
                { child = saved; break; }
                await Task.Delay(10, timeout.Token);
            }
            Assert.Equal(childVersion.ArtifactDigest, child.BundleDigest);
            Assert.NotEqual(parentVersion.ArtifactDigest, child.BundleDigest);
        }
        await using var second = PublishedBundleScenario.Host(store, Folder());
        var client = second.GetRequiredService<IWorkflowClient>();
        Assert.Equal(WorkflowExecutionStatus.Success, (await client.ResumeAsync(child.InstanceId, new Signal { Value = "child output" })).Status);
        var result = await client.RecoverAsync(parentId);
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("child output", result.WorkflowData!.GetType().GetProperty("Result")!.GetValue(result.WorkflowData));
        Assert.Equal(parentVersion.ArtifactDigest, (await client.GetSnapshotAsync(parentId))!.BundleDigest);
    }

    [Fact]
    public async Task OnlyConfiguredHostsAdvertiseBundlePlacement()
    {
        var store = new InMemoryWorkflowStateRepository();
        var version = await PublishedBundleScenario.PublishAsync(store);
        await using var bundles = PublishedBundleScenario.Host(store, Folder());
        var settings = bundles.GetRequiredService<HostSettings>();
        var capabilities = await settings.GetCapabilitiesAsync(bundles.GetRequiredService<IWorkflowCatalog>());
        Assert.True(capabilities.ContainsKey("bundle-host"));
        Assert.Equal(version.StructureHash, capabilities["workflow:BundleLifecycle@1"]);
        var services = new ServiceCollection();
        services.AddIxIFlow();
        services.AddSingleton<IWorkflowStateRepository>(store);
        await using var code = services.BuildServiceProvider();
        var codeCapabilities = await code.GetRequiredService<HostSettings>().GetCapabilitiesAsync(code.GetRequiredService<IWorkflowCatalog>());
        Assert.False(codeCapabilities.ContainsKey("bundle-host"));
        Assert.False(codeCapabilities.ContainsKey("workflow:BundleLifecycle@1"));
    }

    [Fact]
    public void RecursiveTypeIdentifiersRoundTripArraysAndGenerics()
    {
        var types = new[] { typeof(int[]), typeof(int[,]), typeof(int).MakeArrayType(1),
            typeof(Dictionary<string, List<FirstData[]>>[,][]), typeof(Nullable<int>), typeof(Dictionary<,>) };
        foreach (var type in types)
        {
            var name = WorkflowTypeIdentity.SavedName(type);
            Assert.StartsWith("ixi-type:1:", name);
            Assert.Equal(type, WorkflowTypeIdentity.Resolve(name));
        }
        Assert.Null(WorkflowTypeIdentity.Resolve("ixi-type:2:{}"));
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("private")]
    [InlineData("packages")]
    public async Task InvalidBundleLoadIsRefused(string defect)
    {
        var store = new InMemoryWorkflowStateRepository();
        var version = await PublishedBundleScenario.PublishAsync(store);
        await using var archive = (await store.OpenAsync(version.ArtifactDigest!.Value))!;
        var bundle = await WorkflowBundleArchive.ReadAsync(archive, version.ArtifactDigest.Value);
        var entries = bundle.Entries.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        var manifest = bundle.Manifest;
        if (defect == "profile")
        {
            var shared = manifest.SharedAssemblies.ToDictionary();
            var key = shared.Keys.Single(key => key.StartsWith("IxIFlow,", StringComparison.Ordinal));
            shared[key] = "99.0.0.0";
            manifest = manifest with { SharedAssemblies = shared };
        }
        if (defect == "private") entries.Remove("private/IxIFlow.BundleFixtures.dll");
        if (defect == "packages") manifest = manifest with { ActivityPackages = [version.ArtifactDigest.Value] };
        var bytes = WorkflowBundleArchive.Create(manifest, entries);
        var digest = await store.PutAsync(ArtifactKind.Bundle, new MemoryStream(bytes));
        using var catalog = new WorkflowCatalog(store, artifacts: store, bundles: new(Folder()));
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => catalog.AcquireWorkflowDefinitionAsync(version.Name, 1, digest));
        Assert.Equal(WorkflowErrorKind.ArtifactUnavailable, error.Kind);
        Assert.Contains(defect == "profile" ? "profile" : defect == "private" ? "dependency" : "imports", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LastOperationReleasesCollectibleContextAndRunningLeasePreventsEviction()
    {
        var store = new InMemoryWorkflowStateRepository();
        var first = await PublishedBundleScenario.PublishAsync(store);
        var second = await PublishedBundleScenario.PublishAsync(store, 2);
        using var catalog = new WorkflowCatalog(store, artifacts: store, bundles: new(Folder(), 0));
        var weak = UseLeasedBundles(catalog, first, second);
        for (var attempt = 0; attempt < 150 && weak.IsAlive; attempt++)
        {
            AdvanceRuntimeMetadataCache();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            await Task.Delay(20);
        }
        Assert.False(weak.IsAlive);
    }

    [Fact]
    public async Task ContextCollectsAfterTheLastCompletedInstanceAndItsResultAreReleased()
    {
        var store = new InMemoryWorkflowStateRepository();
        await PublishedBundleScenario.PublishAsync(store);
        var weak = CompleteAndRelease(store, Folder());
        for (var attempt = 0; attempt < 150 && weak.IsAlive; attempt++)
        { AdvanceRuntimeMetadataCache(); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Delay(20); }
        Assert.False(weak.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CompleteAndRelease(IWorkflowStateRepository store, string cache)
    {
        var host = PublishedBundleScenario.Host(store, cache, 0);
        try
        {
            var client = host.GetRequiredService<IWorkflowClient>();
            var id = client.StartAsync("BundleLifecycle", new FirstData()).GetAwaiter().GetResult();
            PublishedBundleScenario.UntilAsync(store, id, WorkflowStatus.Suspended).GetAwaiter().GetResult();
            var result = client.ResumeAsync(id, new Signal()).GetAwaiter().GetResult();
            Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
            return new WeakReference(AssemblyLoadContext.GetLoadContext(result.WorkflowData!.GetType().Assembly)!);
        }
        finally { host.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference UseLeasedBundles(WorkflowCatalog catalog, WorkflowVersion first, WorkflowVersion second)
    {
        using var lease = catalog.AcquireWorkflowDefinitionAsync(first.Name, first.Version, first.ArtifactDigest).GetAwaiter().GetResult();
        var context = AssemblyLoadContext.GetLoadContext(lease.Definition.WorkflowDataType!.Assembly)!;
        var unloaded = false;
        context.Unloading += _ => unloaded = true;
        using (var other = catalog.AcquireWorkflowDefinitionAsync(second.Name, second.Version, second.ArtifactDigest).GetAwaiter().GetResult())
            Assert.NotSame(context, AssemblyLoadContext.GetLoadContext(other.Definition.WorkflowDataType!.Assembly));
        Assert.False(unloaded);
        return new WeakReference(context);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task BundleContextsAreCollectibleAcrossPreparationStages(int stage)
    {
        var store = new InMemoryWorkflowStateRepository();
        var version = await PublishedBundleScenario.PublishAsync(store);
        await using var archive = (await store.OpenAsync(version.ArtifactDigest!.Value))!;
        var bundle = await WorkflowBundleArchive.ReadAsync(archive, version.ArtifactDigest.Value);
        var weak = PrepareAndRelease(bundle, version.ArtifactDigest.Value, stage);
        for (var attempt = 0; attempt < 150 && weak.IsAlive; attempt++)
        { AdvanceRuntimeMetadataCache(); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Delay(20); }
        Assert.False(weak.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference PrepareAndRelease(WorkflowBundle bundle, ArtifactDigest digest, int stage)
    {
        var context = new BundleLoadContext(bundle, digest);
        using (WorkflowTypeIdentity.Enter(context))
        {
            var assembly = context.LoadFromStream(new MemoryStream(bundle.Entries[WorkflowBundleArchive.AssemblyPath].ToArray()));
            if (stage > 0)
            {
                var definition = (WorkflowDefinition)assembly.GetType(bundle.Manifest.EntryType)!.GetMethod("Build")!.Invoke(null, null)!;
                if (stage > 1) _ = WorkflowDefinitionValidator.Inspect(definition);
                if (stage > 2) _ = BranchData.Copy(Activator.CreateInstance(definition.WorkflowDataType!)!);
                if (stage > 3) _ = FaultProjection.For(definition.WorkflowDataType!.Assembly.GetType(typeof(BundleException).FullName!)!, typeof(EmptyFault));
            }
        }
        WorkflowJson.Release(context);
        context.Unload();
        return new WeakReference(context);
    }

    // System.Text.Json's runtime member-accessor cache expires idle entries after one second,
    // and sweeps when more metadata is requested. Exercise that public path while awaiting GC.
    private static void AdvanceRuntimeMetadataCache() =>
        _ = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
            .GetTypeInfo(typeof(FirstData), new JsonSerializerOptions());

    private sealed class FetchFaults(IArtifactStore inner) : IArtifactStore
    {
        public bool Missing { get; set; }
        public bool Corrupt { get; set; }
        public ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) =>
            Missing ? ValueTask.FromResult<Stream?>(null) : Corrupt ? ValueTask.FromResult<Stream?>(new MemoryStream([1, 2, 3])) : inner.OpenAsync(digest, cancellationToken);
        public ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken cancellationToken = default) => inner.PutAsync(kind, content, cancellationToken);
        public ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) => inner.ExistsAsync(digest, cancellationToken);
        public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) => inner.DeleteAsync(digest, cancellationToken);
        public ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime olderThanUtc, int batchSize, CancellationToken cancellationToken = default) =>
            inner.PurgeUnreferencedAsync(references, olderThanUtc, batchSize, cancellationToken);
    }
}
