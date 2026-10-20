using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IxIFlow.BundleFixtures;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Tests.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "registration")]
public sealed class BundleRound2Tests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IxIFlow.BundleRound2", Guid.NewGuid().ToString("N"));
    private string Folder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void RuntimeManifestExcludesAnApplicationAssemblyBesideCoreLib()
    {
        var runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var fixture = Folder("runtime");
        foreach (var name in new[] { "Microsoft.NETCore.App.deps.json", "System.Private.CoreLib.dll", "System.Runtime.dll" })
            File.Copy(Path.Combine(runtime, name), Path.Combine(fixture, name));
        File.Copy(typeof(FirstData).Assembly.Location, Path.Combine(fixture, Path.GetFileName(typeof(FirstData).Assembly.Location)));
        var profile = new Dictionary<string, AssemblyName>();
        WorkflowSharedAssemblies.AddRuntimeAssemblies(profile, fixture);
        Assert.Contains("System.Private.CoreLib", profile.Keys);
        Assert.Contains("System.Runtime", profile.Keys);
        Assert.DoesNotContain(typeof(FirstData).Assembly.GetName().Name!, profile.Keys);
    }

    [Fact]
    public void AnUnresolvableSdkReferenceDoesNotPoisonTheRestOfTheProfile()
    {
        var core = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        var missingName = "UnavailableSdkDependency" + Guid.NewGuid().ToString("N");
        var missing = Compile(missingName, "public class Missing { }", core);
        var root = Compile("SdkRoot" + Guid.NewGuid().ToString("N"), "public class Root { public Missing Value; }",
            core, MetadataReference.CreateFromImage(missing));
        var context = new AssemblyLoadContext("sdk-profile-fixture", isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(new MemoryStream(root));
            var profile = new Dictionary<string, AssemblyName>();
            WorkflowSharedAssemblies.AddSdkAssemblies(profile, [assembly, typeof(WorkflowDefinition).Assembly]);
            Assert.Contains(assembly.GetName().Name!, profile.Keys);
            Assert.Contains("IxIFlow", profile.Keys);
            Assert.Contains("IxIFlow.Abstractions", profile.Keys);
            Assert.DoesNotContain(missingName, profile.Keys);
        }
        finally { context.Unload(); }
    }

    private static byte[] Compile(string name, string source, params MetadataReference[] references)
    {
        using var output = new MemoryStream();
        var result = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)).Emit(output);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        return output.ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CacheLockWaitDoesNotConsumeTheFetchBudgetAndRemainsCancellable(bool cancel)
    {
        var store = new InMemoryWorkflowStateRepository();
        var version = await PublishedBundleScenario.PublishAsync(store);
        var artifacts = new PausedArtifacts(store);
        var folder = Folder("cache");
        using var held = new FileStream(Path.Combine(folder, version.ArtifactDigest!.Value.Value[7..] + ".bundle.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var catalog = new WorkflowCatalog(store, artifacts: artifacts,
            bundles: new(folder) { FetchTimeout = TimeSpan.FromMilliseconds(100) });
        using var stop = new CancellationTokenSource();
        var loading = catalog.ResolveWorkflowDefinitionAsync(version.Name, 1, stop.Token);
        await Task.Delay(400);
        Assert.False(loading.IsCompleted);
        Assert.Equal(0, artifacts.Reads);
        if (cancel)
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
        }
        else
        {
            held.Dispose();
            using var lease = await loading.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(version.ArtifactDigest, lease.Definition.BundleDigest);
            Assert.Equal(1, artifacts.Reads);
        }
    }

    [Theory]
    [InlineData("archive")]
    [InlineData("manifest")]
    [InlineData("entries")]
    [InlineData("entry")]
    [InlineData("expanded")]
    public async Task CacheLimitsPreserveTheVerifiedArchiveWithoutRefetching(string bound)
    {
        var store = new InMemoryWorkflowStateRepository();
        var version = await PublishedBundleScenario.PublishAsync(store);
        var artifacts = new PausedArtifacts(store);
        var folder = Folder("cache");
        using (var warm = new WorkflowCatalog(store, artifacts: artifacts, bundles: new(folder)))
        using (await warm.ResolveWorkflowDefinitionAsync(version.Name, 1)) { }
        var path = Path.Combine(folder, version.ArtifactDigest!.Value.Value[7..] + ".bundle");
        var original = await File.ReadAllBytesAsync(path);
        var limits = bound switch
        {
            "archive" => new WorkflowBundleReadLimits { MaxArchiveBytes = 1 },
            "manifest" => new WorkflowBundleReadLimits { MaxManifestBytes = 1 },
            "entries" => new WorkflowBundleReadLimits { MaxEntries = 1 },
            "entry" => new WorkflowBundleReadLimits { MaxEntryBytes = 1 },
            _ => new WorkflowBundleReadLimits { MaxExpandedBytes = 1 }
        };
        using (var restricted = new WorkflowCatalog(store, artifacts: artifacts, bundles: new(folder) { ReadLimits = limits }))
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => restricted.ResolveWorkflowDefinitionAsync(version.Name, 1));
                Assert.Equal(WorkflowErrorKind.ResourceExhausted, error.Kind);
                Assert.True(error.IsInterruption);
                Assert.Equal(original, await File.ReadAllBytesAsync(path));
                Assert.Equal(1, artifacts.Reads);
            }
        }
        using var roomy = new WorkflowCatalog(store, artifacts: artifacts, bundles: new(folder));
        using var recovered = await roomy.ResolveWorkflowDefinitionAsync(version.Name, 1);
        Assert.Equal(version.ArtifactDigest, recovered.Definition.BundleDigest);
        Assert.Equal(1, artifacts.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARemotePublicationRefreshesPlacementOnlyWhenWorkflowDemandsAreTheOnlyMismatch(bool otherDemand)
    {
        var store = new InMemoryWorkflowStateRepository();
        using var catalog = new WorkflowCatalog(store, artifacts: store, bundles: new(Folder("catalog")));
        var counted = DispatchProxy.Create<IWorkflowCatalog, BundleReviewFixTests.CatalogReads>();
        var proxy = (BundleReviewFixTests.CatalogReads)(object)counted;
        proxy.Inner = catalog;
        await using var host = PublishedBundleScenario.Host(store, Folder("host"),
            configure: services => services.AddSingleton(counted));
        var settings = host.GetRequiredService<HostSettings>();
        Assert.DoesNotContain("workflow:BundleLifecycle@1", (await settings.GetCapabilitiesAsync(counted)).Keys);
        var revision = catalog.DefinitionRevision;
        var version = await PublishedBundleScenario.PublishAsync(store);
        Assert.Equal(revision, catalog.DefinitionRevision);
        var client = host.GetRequiredService<IWorkflowClient>();
        var options = new WorkflowOptions { Demands = otherDemand ? [new("unavailable-capability")] : [] };
        var id = await client.StartAsync(version.Name, new FirstData(), options);
        // Acceptance precedes the start's final result. Drain its normal per-instance queue.
        await host.GetRequiredService<WorkflowHost>().RunAsync(id,
            (_, _, _) => Task.FromResult(new WorkflowExecutionResult { InstanceId = id }), CancellationToken.None);
        Assert.Equal(otherDemand ? WorkflowStatus.Running : WorkflowStatus.Suspended,
            (await store.GetWorkflowInstanceAsync(id))!.Status);
        Assert.Equal(otherDemand ? 1 : 2, proxy.Reads);
    }

    [Fact]
    public void CompatibilityValidationIsMemoisedByManifestContent()
    {
        var unique = Guid.NewGuid().ToString("N");
        var json = JsonSerializer.Serialize(new { Name = unique });
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        var cache = (ConcurrentDictionary<string, Lazy<bool>>)typeof(WorkflowHeartbeatService)
            .GetField("Compatibility", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Assert.False(WorkflowHeartbeatService.Compatible(json));
        var first = cache[hash];
        Assert.True(first.IsValueCreated);
        Assert.False(WorkflowHeartbeatService.Compatible(new string(json.ToCharArray())));
        Assert.Same(first, cache[hash]);
        Assert.False(WorkflowHeartbeatService.Compatible(json + " "));
        var changedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json + " ")));
        Assert.NotSame(first, cache[changedHash]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFetchTimeoutKeepsTheCheckpointAndRecoverySucceeds(bool sqlite)
    {
        IWorkflowStateRepository store = sqlite
            ? new SqliteWorkflowStateRepository(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Folder("db"), "state.db"), Pooling = false }.ConnectionString)
            : new InMemoryWorkflowStateRepository();
        try
        {
            var version = await PublishedBundleScenario.PublishAsync(store);
            string id;
            await using (var first = PublishedBundleScenario.Host(store, Folder("first")))
            {
                id = await first.GetRequiredService<IWorkflowClient>().StartAsync(version.Name, new FirstData());
                await PublishedBundleScenario.UntilAsync(store, id, WorkflowStatus.Suspended);
            }
            var checkpoint = (await store.GetWorkflowInstanceAsync(id))!.ExecutionStateJson;
            var artifacts = new PausedArtifacts(store) { Pause = true };
            using var catalog = new WorkflowCatalog(store, artifacts: artifacts,
                bundles: new(Folder("slow")) { FetchTimeout = TimeSpan.FromMilliseconds(100) });
            await using var second = PublishedBundleScenario.Host(store, Folder("second"),
                configure: services => services.AddSingleton<IWorkflowCatalog>(catalog));
            var client = second.GetRequiredService<IWorkflowClient>();
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => client.ResumeAsync(id, new Signal { Value = "recovered" }));
            Assert.Equal(WorkflowErrorKind.ArtifactUnavailable, error.Kind);
            Assert.True(error.IsInterruption);
            var saved = (await store.GetWorkflowInstanceAsync(id))!;
            Assert.Equal(WorkflowStatus.Suspended, saved.Status);
            Assert.Equal(checkpoint, saved.ExecutionStateJson);
            artifacts.Release.TrySetResult();
            Assert.Equal(WorkflowExecutionStatus.Success, (await client.RecoverAsync(id)).Status);
        }
        finally { if (store is IAsyncDisposable disposable) await disposable.DisposeAsync(); }
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("waits")]
    [InlineData("result")]
    [InlineData("pending")]
    [InlineData("existing-start")]
    public async Task HostReadsCancelTheirBundleAcquisition(string operation)
    {
        var store = new InMemoryWorkflowStateRepository();
        var version = await PublishedBundleScenario.PublishAsync(store);
        WorkflowDefinition definition;
        using (var source = new WorkflowCatalog(store, artifacts: store, bundles: new(Folder("source"))))
        using (var lease = await source.ResolveWorkflowDefinitionAsync(version.Name, 1))
        using (lease.EnterTypes())
        {
            definition = lease.Definition;
            var (instance, checkpoint, _) = WorkflowInstances.NewInstance(definition,
                Activator.CreateInstance(definition.WorkflowDataType!)!, new WorkflowOptions { InstanceId = "read-cancellation" }, DateTime.UtcNow);
            instance.Status = WorkflowStatus.NeedsResolution;
            instance.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
            await store.SeedAsync(instance);
        }
        if (operation == "waits")
        {
            await using var starter = PublishedBundleScenario.Host(store, Folder("starter"));
            var id = await starter.GetRequiredService<IWorkflowClient>().StartAsync(version.Name, new FirstData());
            await PublishedBundleScenario.UntilAsync(store, id, WorkflowStatus.Suspended);
        }
        var artifacts = new PausedArtifacts(store) { Pause = true };
        using var catalog = new WorkflowCatalog(store, artifacts: artifacts, bundles: new(Folder("blocked")));
        await using var host = PublishedBundleScenario.Host(store, Folder("host"),
            configure: services => services.AddSingleton<IWorkflowCatalog>(catalog));
        using var stop = new CancellationTokenSource();
        var running = host.GetRequiredService<WorkflowHost>().ReadAsync(async client =>
        {
            var publicClient = host.GetRequiredService<IWorkflowClient>();
            switch (operation)
            {
                case "snapshot": await publicClient.GetSnapshotAsync("read-cancellation", stop.Token); break;
                case "waits": await publicClient.GetOpenWaitsAsync(new(), stop.Token); break;
                case "pending": await publicClient.GetPendingActivitiesAsync("read-cancellation", stop.Token); break;
                case "existing-start": await client.RunAsync(definition, new FirstData(), new WorkflowOptions { InstanceId = "read-cancellation" }, stop.Token); break;
                default: await publicClient.AwaitAsync("read-cancellation", cancellationToken: stop.Token); break;
            }
            return true;
        });
        await artifacts.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task AChildFetchTimeoutInterruptsTheRunningParentWithoutChangingItsCheckpoint()
    {
        var store = new InMemoryWorkflowStateRepository();
        var child = await PublishedBundleScenario.PublishAsync(store);
        var parent = await PublishedBundleScenario.PublishParentAsync(store);
        var artifacts = new PausedArtifacts(store) { Pause = true, PausedDigest = child.ArtifactDigest };
        using var catalog = new WorkflowCatalog(store, artifacts: artifacts,
            bundles: new(Folder("cache")) { FetchTimeout = TimeSpan.FromMilliseconds(300) });
        using var lease = await catalog.ResolveWorkflowDefinitionAsync(parent.Name, 1);
        await using var host = PublishedBundleScenario.Host(store, Folder("host"),
            configure: services => services.AddSingleton<IWorkflowCatalog>(catalog));
        const string id = "child-timeout";
        var client = host.GetRequiredService<IWorkflowClient>();
        var running = client.RunAsync(lease.Definition, Activator.CreateInstance(lease.Definition.WorkflowDataType!)!,
            new WorkflowOptions { InstanceId = id });
        await artifacts.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var checkpoint = (await store.GetWorkflowInstanceAsync(id))!.ExecutionStateJson;
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => running);
        Assert.Equal(WorkflowErrorKind.ArtifactUnavailable, error.Kind);
        var saved = (await store.GetWorkflowInstanceAsync(id))!;
        Assert.Equal(WorkflowStatus.Running, saved.Status);
        Assert.Equal(checkpoint, saved.ExecutionStateJson);
        Assert.Empty((await store.QueryInstancesAsync(new(ParentInstanceId: id))).Items);
        artifacts.Release.TrySetResult();
        Assert.Equal(WorkflowExecutionStatus.Suspended, (await client.RecoverAsync(id)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcquisitionTokenCanBeReadAfterTheSignalsClose(bool cancel)
    {
        var signals = new ExecutionSignals(Guid.NewGuid(), _ => { });
        var token = signals.AcquisitionToken;
        if (cancel) signals.Deliver(new Cancel(signals.AssignmentId, CancelKind.Run));
        signals.Close();
        signals.Close();
        Assert.Equal(token, signals.AcquisitionToken);
        Assert.Equal(cancel, signals.AcquisitionToken.IsCancellationRequested);
    }

    private sealed class PausedArtifacts(IArtifactStore inner) : IArtifactStore
    {
        public bool Pause;
        public ArtifactDigest? PausedDigest;
        public int Reads;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Reads);
            if (Pause && (PausedDigest == null || digest == PausedDigest))
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return await inner.OpenAsync(digest, cancellationToken);
        }
        public ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken cancellationToken = default) => inner.PutAsync(kind, content, cancellationToken);
        public ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) => inner.ExistsAsync(digest, cancellationToken);
        public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) => inner.DeleteAsync(digest, cancellationToken);
        public ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime olderThanUtc, int batchSize, CancellationToken cancellationToken = default) => inner.PurgeUnreferencedAsync(references, olderThanUtc, batchSize, cancellationToken);
    }
}
