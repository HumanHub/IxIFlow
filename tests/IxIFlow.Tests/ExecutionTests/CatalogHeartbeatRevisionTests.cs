using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Dapper;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "host")]
public sealed class CatalogHeartbeatRevisionTests
{
    public enum Scenario { Refresh, Demand, RacingPublication, ConcurrentWriters, NoOps, MemoRetention, DraftPublication, UnadvertisableDelta, DemandFailure, InflightDemand, RacingUnchangedRevision, CoalescedChanges, WithoutBundleHosting, RevisionMovedBack, RevisionMovedBackDuringDelta }
    public static TheoryData<Scenario> Scenarios => new(Enum.GetValues<Scenario>());

    [Theory, MemberData(nameof(Scenarios))]
    public Task Memory(Scenario scenario) => VerifyAsync(new InMemoryWorkflowStateRepository(), scenario);

    [Theory, MemberData(nameof(Scenarios)), Trait("Category", "SqliteIntegration")]
    public async Task Sqlite(Scenario scenario)
    {
        var root = Path.Combine(Path.GetTempPath(), "IxIFlow.CatalogRevision", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var store = new SqliteWorkflowStateRepository("Data Source=" + Path.Combine(root, "state.db"));
            await VerifyAsync(store, scenario);
        }
        finally { await SqliteTestFiles.DeleteAsync(root); }
    }

    [SqlServerFact, Trait("Category", "SqlIntegration")]
    public async Task SqlServer()
    {
        foreach (var snapshot in new[] { false, true })
        foreach (var scenario in Enum.GetValues<Scenario>())
        {
            var cs = await SqlServerTestServer.CreateDatabaseAsync("CatalogRevision");
            try
            {
                await using (var connection = new SqlConnection(cs))
                {
                    await connection.OpenAsync();
                    await connection.ExecuteAsync($"ALTER DATABASE [{connection.Database}] SET READ_COMMITTED_SNAPSHOT {(snapshot ? "ON" : "OFF")}");
                    Assert.Equal(snapshot, await connection.ExecuteScalarAsync<bool>(
                        "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = DB_NAME()"));
                }
                await VerifyAsync(new SqlWorkflowStateRepository(cs), scenario);
            }
            finally { await SqlServerTestServer.DropDatabaseAsync(cs); }
        }
    }

    [PostgreSqlFact, Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSql()
    {
        foreach (var scenario in Enum.GetValues<Scenario>())
        {
            var cs = await PostgreSqlTestServer.CreateDatabaseAsync("catalog_revision");
            try { await VerifyAsync(new PostgreSqlWorkflowStateRepository(cs), scenario); }
            finally { await PostgreSqlTestServer.DropDatabaseAsync(cs); }
        }
    }

    private static async Task VerifyAsync(IWorkflowStateRepository store, Scenario scenario)
    {
        var prefix = Guid.NewGuid().ToString("N");
        using var catalog = new WorkflowCatalog(store);
        var observed = DispatchProxy.Create<IWorkflowCatalog, CatalogReads>();
        var reads = (CatalogReads)(object)observed;
        reads.Inner = catalog;
        var logger = new HeartbeatLogger();
        var settings = HostSettings.Default with { BundleHostingEnabled = true, CapabilityLogger = logger };
        var time = new FakeTimeProvider();
        Task<IReadOnlyDictionary<string, string>> Refresh() => settings.RefreshCapabilitiesAsync(observed);
        async Task<IReadOnlyDictionary<string, string>> Demand()
        {
            time.Advance(settings.HeartbeatInterval);
            return Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(
                await settings.RefreshDemandCapabilitiesAsync(observed, time));
        }
        Task<IReadOnlyDictionary<string, string>> Read() => scenario == Scenario.Demand ? Demand() : Refresh();
        string Key(WorkflowVersion row) => $"workflow:{row.Name}@{row.Version}";
        // Exercise publication's transaction without compiling another bundle for each scenario.
        // The catalog accepts artifact/manifest metadata; compatibility uses the actual host profile.
        var manifest = CompatibleManifest(prefix);
        Assert.True(WorkflowSharedAssemblies.IsCompatible(System.Text.Json.JsonSerializer.Deserialize<WorkflowBundleManifest>(manifest)!));
        var digest = await store.PutAsync(ArtifactKind.Bundle, new MemoryStream([1, 2, 3]));
        async Task<WorkflowVersion> Publish(string name)
        {
            var draft = await store.SaveDefinitionAsync(new(name, 1, "workflow", "source"), 0);
            return await store.PublishDefinitionAsync(draft.Id, new(name, 1, "hash", ArtifactDigest: digest,
                PublishedDraftRevision: draft.Revision, ManifestJson: manifest));
        }
        var initial = await Read();
        reads.Reset();
        Assert.Same(initial, await Read());
        Assert.Equal(0, reads.VersionReads);
        Assert.Empty(reads.Rows);
        switch (scenario)
        {
            case Scenario.Refresh:
            case Scenario.Demand:
            {
                var row = await Publish(prefix);
                Assert.Equal(row.StructureHash, (await Read())[Key(row)]);
                reads.AssertSingle(row.Name);
                reads.Reset();
                var snapshot = await Read();
                Assert.Same(snapshot, await Read());
                Assert.Equal(0, reads.VersionReads);
                await store.SetWorkflowVersionStateAsync(row.Name, 1, WorkflowVersionState.Deprecated);
                Assert.Contains(Key(row), (await Read()).Keys);
                reads.AssertSingle(row.Name);
                reads.Reset();
                await store.SetWorkflowVersionStateAsync(row.Name, 1, WorkflowVersionState.Retired);
                Assert.Contains(Key(row), (await Read()).Keys);
                reads.AssertSingle(row.Name);
                reads.Reset();
                var definition = Workflow.Create<CatalogArtifactContractTests.Data>(prefix + "-code")
                    .Step<CatalogArtifactContractTests.Noop>().Build();
                await catalog.RegisterWorkflowAsync(definition);
                // Ordinary local lookups still follow DefinitionRevision before the next heartbeat.
                Assert.Contains($"workflow:{definition.Name}@1", (await settings.GetCapabilitiesAsync(observed)).Keys);
                reads.AssertSingle(definition.Name);
                reads.Reset();
                await Read();
                Assert.Equal(0, reads.VersionReads);
                var heartbeatDefinition = Workflow.Create<CatalogArtifactContractTests.Data>(prefix + "-heartbeat-code")
                    .Step<CatalogArtifactContractTests.Noop>().Build();
                await catalog.RegisterWorkflowAsync(heartbeatDefinition);
                Assert.Contains($"workflow:{heartbeatDefinition.Name}@1", (await Read()).Keys);
                reads.AssertSingle(heartbeatDefinition.Name);
                break;
            }
            case Scenario.RacingUnchangedRevision:
            {
                var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                reads.AfterRevision = async () => { captured.SetResult(); await release.Task; };
                var refresh = Refresh();
                await captured.Task.WaitAsync(TimeSpan.FromSeconds(10));
                WorkflowVersion row;
                try { row = await Publish(prefix); }
                finally { reads.AfterRevision = null; release.SetResult(); }
                Assert.Same(initial, await refresh);
                Assert.Equal(0, reads.VersionReads);
                Assert.Contains(Key(row), (await Refresh()).Keys);
                reads.AssertSingle(row.Name);
                break;
            }
            case Scenario.RacingPublication:
            {
                var first = await Publish(prefix);
                var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                reads.AfterDelta = async () => { captured.SetResult(); await release.Task; };
                var refresh = Refresh();
                await captured.Task.WaitAsync(TimeSpan.FromSeconds(10));
                WorkflowVersion second;
                try { second = await Publish(prefix + "-racing"); }
                finally { reads.AfterDelta = null; release.SetResult(); }
                Assert.Contains(Key(first), (await refresh).Keys);
                reads.Reset();
                Assert.Contains(Key(second), (await Refresh()).Keys);
                reads.AssertSingle(second.Name);
                break;
            }
            case Scenario.ConcurrentWriters:
            {
                var start = await store.GetCatalogRevisionAsync();
                var done = 0;
                var observedRevisions = new ConcurrentBag<long>();
                var reader = Task.Run(async () =>
                {
                    var cursor = start;
                    var seen = new HashSet<string>();
                    while (Volatile.Read(ref done) == 0)
                    {
                        var delta = await store.GetWorkflowVersionChangesAsync(cursor);
                        Assert.True(delta.Revision >= cursor);
                        foreach (var row in delta.Versions) seen.Add(row.Name);
                        cursor = delta.Revision;
                        await Task.Yield();
                    }
                    var final = await store.GetWorkflowVersionChangesAsync(cursor);
                    foreach (var row in final.Versions) seen.Add(row.Name);
                    return seen;
                });
                try
                {
                    await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(async () =>
                    {
                        var before = await store.GetCatalogRevisionAsync();
                        await Publish(prefix + i);
                        var after = await store.GetCatalogRevisionAsync();
                        Assert.True(after > before);
                        observedRevisions.Add(after);
                    })));
                }
                finally { Volatile.Write(ref done, 1); }
                Assert.Equal(12, (await reader).Count);
                Assert.Equal(start + 12, await store.GetCatalogRevisionAsync());
                var all = await store.GetWorkflowVersionChangesAsync(start);
                Assert.Equal(12, all.Versions.Count);
                Assert.All(observedRevisions, revision => Assert.InRange(revision, start + 1, all.Revision));
                Assert.Empty((await store.GetWorkflowVersionChangesAsync(all.Revision)).Versions);
                break;
            }
            case Scenario.CoalescedChanges:
            {
                var start = await store.GetCatalogRevisionAsync();
                var row = await Publish(prefix);
                Assert.Equal(start + 1, await store.GetCatalogRevisionAsync());
                await store.SetWorkflowVersionStateAsync(row.Name, 1, WorkflowVersionState.Deprecated);
                Assert.Equal(start + 2, await store.GetCatalogRevisionAsync());
                await store.SetWorkflowVersionStateAsync(row.Name, 1, WorkflowVersionState.Retired);
                var delta = await store.GetWorkflowVersionChangesAsync(start);
                Assert.Equal(start + 3, delta.Revision);
                Assert.Equal(WorkflowVersionState.Retired, Assert.Single(delta.Versions).State);
                Assert.Contains(Key(row), (await Refresh()).Keys);
                reads.AssertSingle(row.Name);
                break;
            }
            case Scenario.NoOps:
            {
                var beforeDraft = await store.GetCatalogRevisionAsync();
                var draft = await store.SaveDefinitionAsync(new(prefix, 1, "workflow", "source"), 0);
                Assert.Equal(beforeDraft, await store.GetCatalogRevisionAsync());
                var row = new WorkflowVersion(prefix, 1, "hash", ArtifactDigest: digest,
                    PublishedDraftRevision: draft.Revision, ManifestJson: manifest);
                await store.PublishDefinitionAsync(draft.Id, row);
                var revision = await store.GetCatalogRevisionAsync();
                await store.PublishDefinitionAsync(draft.Id, row);
                await store.RegisterWorkflowVersionAsync(row);
                await store.SetWorkflowVersionStateAsync(row.Name, 1, row.State);
                Assert.Equal(revision, await store.GetCatalogRevisionAsync());
                await Assert.ThrowsAsync<WorkflowEngineException>(() => store.RegisterWorkflowVersionAsync(row with { StructureHash = "other" }));
                Assert.Equal(revision, await store.GetCatalogRevisionAsync());
                Assert.Empty((await store.GetWorkflowVersionChangesAsync(revision)).Versions);
                await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.GetWorkflowVersionChangesAsync(-1));
                break;
            }
            case Scenario.DemandFailure:
            {
                var row = await Publish(prefix);
                reads.BeforeDelta = () => throw new IOException("catalog unavailable");
                Assert.Null(await settings.RefreshDemandCapabilitiesAsync(observed, time));
                Assert.Equal(1, reads.VersionReads);
                Assert.Null(await settings.RefreshDemandCapabilitiesAsync(observed, time));
                Assert.Equal(1, reads.VersionReads);
                Assert.Same(initial, await settings.GetCapabilitiesAsync(observed));
                reads.BeforeDelta = null;
                reads.Reset();
                Assert.Contains(Key(row), (await Demand()).Keys);
                reads.AssertSingle(row.Name);
                break;
            }
            case Scenario.InflightDemand:
            {
                var row = await Publish(prefix);
                var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                reads.BeforeDelta = async () => { entered.SetResult(); await release.Task; };
                var refresh = settings.RefreshDemandCapabilitiesAsync(observed, time);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                try
                {
                    Assert.Null(await settings.RefreshDemandCapabilitiesAsync(observed, time).WaitAsync(TimeSpan.FromSeconds(2)));
                    Assert.Same(initial, await settings.GetCapabilitiesAsync(observed).WaitAsync(TimeSpan.FromSeconds(2)));
                }
                finally { reads.BeforeDelta = null; release.SetResult(); }
                Assert.Contains(Key(row), (await refresh)!.Keys);
                reads.AssertSingle(row.Name);
                break;
            }
            case Scenario.WithoutBundleHosting:
            {
                // A host that does not host bundles advertises only its own code: a populated,
                // changing catalog must cost it no catalog reads at all.
                var row = await Publish(prefix);
                var local = HostSettings.Default with { BundleHostingEnabled = false };
                reads.Reset();
                var capabilities = await local.RefreshCapabilitiesAsync(observed);
                Assert.DoesNotContain(Key(row), capabilities.Keys);
                Assert.DoesNotContain("bundle-host", capabilities.Keys);
                await store.SetWorkflowVersionStateAsync(row.Name, 1, WorkflowVersionState.Deprecated);
                Assert.Same(capabilities, await local.RefreshCapabilitiesAsync(observed));
                time.Advance(local.HeartbeatInterval);
                await local.RefreshDemandCapabilitiesAsync(observed, time);
                Assert.Equal(0, reads.VersionReads);
                Assert.Equal(0, reads.RevisionReads);
                break;
            }
            case Scenario.RevisionMovedBack:
            {
                // A store restored from a backup reports a revision below the host's cursor; the host
                // must reread every version rather than wait for the counter to pass its old cursor.
                var row = await Publish(prefix);
                Assert.Contains(Key(row), (await Refresh()).Keys);
                reads.Reset();
                reads.RevisionMap = revision => revision - 1;
                Assert.Contains(Key(row), (await Refresh()).Keys);
                Assert.Equal(1, reads.VersionReads);
                Assert.Equal(0, reads.LastAfter);
                Assert.Contains(row.Name, reads.Rows.Select(version => version.Name));
                reads.Reset();
                await Refresh();
                Assert.Equal(0, reads.VersionReads);
                break;
            }
            case Scenario.RevisionMovedBackDuringDelta:
            {
                var retained = await Publish(prefix);
                var removed = await Publish(prefix + "-removed");
                var snapshot = await Refresh();
                Assert.Contains(Key(retained), snapshot.Keys);
                Assert.Contains(Key(removed), snapshot.Keys);
                var cursor = await store.GetCatalogRevisionAsync();
                await store.SetWorkflowVersionStateAsync(retained.Name, 1, WorkflowVersionState.Deprecated);
                reads.Reset();
                // The scalar sees a forward revision, then the store is restored before the delta.
                reads.BeforeDelta = () =>
                {
                    Assert.Equal(cursor, reads.LastAfter);
                    reads.BeforeDelta = null;
                    reads.RevisionMap = _ => cursor - 1;
                    reads.Filter = row => row.Name == retained.Name;
                    return Task.CompletedTask;
                };
                var restored = await Refresh();
                Assert.Contains(Key(retained), restored.Keys);
                Assert.DoesNotContain(Key(removed), restored.Keys);
                Assert.Equal(2, reads.VersionReads);
                Assert.Equal(0, reads.LastAfter);
                Assert.Equal(retained.Name, Assert.Single(reads.Rows).Name);
                Assert.Equal($"The catalog revision moved back from {cursor} to {cursor - 1}; rereading every workflow version",
                    Assert.Single(logger.Messages));
                reads.Reset();
                Assert.Same(restored, await Refresh());
                Assert.Equal(0, reads.VersionReads);
                break;
            }
            case Scenario.DraftPublication:
            {
                var row = new WorkflowVersion(prefix, 1, "hash", WorkflowVersionState.Draft,
                    ArtifactDigest: digest, ManifestJson: manifest);
                await store.RegisterWorkflowVersionAsync(row);
                Assert.DoesNotContain(Key(row), (await Refresh()).Keys);
                reads.AssertSingle(row.Name);
                reads.Reset();
                await store.SetWorkflowVersionStateAsync(row.Name, 1, WorkflowVersionState.Published);
                Assert.Contains(Key(row), (await Refresh()).Keys);
                reads.AssertSingle(row.Name);
                break;
            }
            case Scenario.UnadvertisableDelta:
            {
                var row = await Publish(prefix);
                Assert.Contains(Key(row), (await Refresh()).Keys);
                reads.Reset();
                // A catalog view can apply host policy to changed rows. Ensure a negative
                // result replaces an earlier positive result, including on demand refresh.
                reads.Transform = version => version.Name == row.Name ? version with { ManifestJson = null } : version;
                await store.SetWorkflowVersionStateAsync(row.Name, 1, WorkflowVersionState.Deprecated);
                Assert.DoesNotContain(Key(row), (await Demand()).Keys);
                reads.AssertSingle(row.Name);
                break;
            }
            case Scenario.MemoRetention:
            {
                var first = await Publish(prefix);
                var second = await Publish(prefix + "-same-manifest");
                await Refresh();
                var memo = (ConcurrentDictionary<string, Lazy<bool>>)typeof(WorkflowHeartbeatService.CompatibilityMemo)
                    .GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(settings.Compatibility)!;
                Assert.Single(memo);
                // Model a catalog policy that withdraws retired bundles from this host's view.
                reads.Transform = row => row.State == WorkflowVersionState.Retired ? row with { ManifestJson = null } : row;
                await store.SetWorkflowVersionStateAsync(first.Name, 1, WorkflowVersionState.Retired);
                await Refresh();
                Assert.Single(memo);
                await store.SetWorkflowVersionStateAsync(second.Name, 1, WorkflowVersionState.Retired);
                await Refresh();
                Assert.Empty(memo);
                break;
            }
        }
    }

    [Theory, InlineData(false, false), InlineData(true, false), InlineData(false, true), InlineData(true, true)]
    public async Task HeartbeatsKeepTheirSnapshotWhileOneRefreshIsBlockedOrFails(bool fail, bool synchronous)
    {
        var time = new HeartbeatTime();
        var inner = new InMemoryWorkflowStateRepository(time);
        var store = DispatchProxy.Create<IWorkflowStateRepository, HeartbeatReads>();
        var beats = (HeartbeatReads)(object)store;
        beats.Inner = inner;
        using var catalog = new WorkflowCatalog(inner);
        await catalog.RegisterWorkflowAsync(Workflow.Create<CatalogArtifactContractTests.Data>("HeartbeatLocal")
            .Step<CatalogArtifactContractTests.Noop>().Build());
        var observed = DispatchProxy.Create<IWorkflowCatalog, CatalogReads>();
        var reads = (CatalogReads)(object)observed;
        reads.Inner = catalog;
        var settings = HostSettings.Default with { BundleHostingEnabled = true };
        var logger = new HeartbeatLogger();
        using var heartbeat = new WorkflowHeartbeatService(store, observed, settings, time, logger);
        await heartbeat.StartAsync(CancellationToken.None);
        var initial = await settings.GetCapabilitiesAsync(observed);
        Assert.Contains("workflow:HeartbeatLocal@1", initial.Keys);
        Assert.Contains("bundle-host", initial.Keys);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reads.Reset();
        if (synchronous) reads.BeforeRevisionTask = () =>
        {
            // Block in Invoke itself, before the catalog returns any task to its caller.
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            if (fail) throw new IOException("catalog unavailable");
        };
        else reads.BeforeRevision = async () =>
        {
            entered.TrySetResult();
            await release.Task;
            if (fail) throw new IOException("catalog unavailable");
        };
        try
        {
            // Start one refresh, then expire its per-tick wait using only injected time.
            await UntilAsync(() => Volatile.Read(ref time.PeriodicTimers) == 1);
            time.Advance(settings.HeartbeatInterval);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await UntilAsync(() => Volatile.Read(ref time.RefreshWaits) == 1);
            Assert.Empty(beats.Snapshots);
            for (var tick = 1; tick <= 3; tick++)
            {
                time.Advance(settings.HeartbeatInterval);
                await UntilAsync(() => beats.Snapshots.Count == tick && Volatile.Read(ref time.RefreshWaits) == tick + 1);
                Assert.All(beats.Snapshots, values => Assert.Same(initial, values));
                Assert.Same(initial, await settings.GetCapabilitiesAsync(observed));
                Assert.Single(await inner.GetLiveMembersAsync());
                Assert.Equal(1, reads.RevisionReads);
                Assert.Equal(0, reads.VersionReads);
            }
            var digest = await inner.PutAsync(ArtifactKind.Bundle, new MemoryStream([1, 2, 3]));
            var version = new WorkflowVersion("HeartbeatPublished", 1, "hash", ArtifactDigest: digest,
                ManifestJson: CompatibleManifest("HeartbeatPublished"));
            await inner.RegisterWorkflowVersionAsync(version);
            reads.BeforeRevision = null;
            reads.BeforeRevisionTask = null;
            release.SetResult();
            await UntilAsync(() => beats.Snapshots.Count == 4);
            if (fail)
            {
                Assert.Same(initial, beats.Snapshots.Last());
                Assert.Same(initial, await settings.GetCapabilitiesAsync(observed));
                Assert.Equal(1, logger.Warnings);
                time.Advance(settings.HeartbeatInterval);
                await UntilAsync(() => beats.Snapshots.Count == 5);
            }
            else Assert.Equal(0, logger.Warnings);
            Assert.Equal(version.StructureHash, beats.Snapshots.Last()["workflow:HeartbeatPublished@1"]);
            // Catch refreshes queued behind the capability gate while the first one was blocked.
            Assert.Equal(fail ? 2 : 1, reads.RevisionReads);
            Assert.Equal(1, reads.VersionReads);
        }
        finally
        {
            reads.BeforeRevision = null;
            reads.BeforeRevisionTask = null;
            release.TrySetResult();
            await heartbeat.StopAsync(CancellationToken.None);
        }
    }

    [Theory, InlineData(false, false), InlineData(false, true), InlineData(true, false), InlineData(true, true)]
    public async Task HeartbeatFallbackBelongsToItsCatalog(bool sqlite, bool fail)
    {
        await using var fixture = await HeartbeatFixture.StartAsync(sqlite);
        using var other = new WorkflowCatalog(fixture.Inner);
        await other.RegisterWorkflowAsync(Workflow.Create<CatalogArtifactContractTests.Data>("OtherLocal")
            .Step<CatalogArtifactContractTests.Noop>().Build());
        var own = await fixture.Settings.GetCapabilitiesAsync(fixture.Observed);
        var foreign = await fixture.Settings.GetCapabilitiesAsync(other);
        Assert.Contains("workflow:OtherLocal@1", foreign.Keys);
        Assert.DoesNotContain("workflow:HeartbeatLocal@1", foreign.Keys);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Reads.BeforeRevision = async () =>
        {
            entered.TrySetResult();
            await release.Task;
            if (fail) throw new IOException("catalog unavailable");
        };
        try
        {
            fixture.Time.Advance(fixture.Settings.HeartbeatInterval);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await UntilAsync(() => fixture.Time.RefreshWaits == 1);
            fixture.Time.Advance(fixture.Settings.HeartbeatInterval);
            await UntilAsync(() => fixture.Beats.Snapshots.Count == 1 && fixture.Time.RefreshWaits == 2);
            Assert.Same(own, fixture.Beats.Snapshots.Last());
            if (fail)
            {
                release.SetResult();
                await UntilAsync(() => fixture.Beats.Snapshots.Count == 2);
                Assert.Equal(1, fixture.Logger.Warnings);
                Assert.Same(own, fixture.Beats.Snapshots.Last());
            }
            var member = Assert.Single(await fixture.Inner.GetLiveMembersAsync());
            Assert.Contains("workflow:HeartbeatLocal@1", member.Capabilities.Keys);
            Assert.DoesNotContain("workflow:OtherLocal@1", member.Capabilities.Keys);
            Assert.All(fixture.Beats.Snapshots, snapshot => Assert.Same(own, snapshot));
            // On success, adopt A's returned result even though the shared cache previously held B.
            fixture.Reads.BeforeRevision = null;
            release.TrySetResult();
            await UntilAsync(() => fixture.Beats.Snapshots.Count >= 2);
            fixture.Time.Advance(fixture.Settings.HeartbeatInterval);
            await UntilAsync(() => fixture.Beats.Snapshots.Count >= 3);
            Assert.Contains("workflow:HeartbeatLocal@1", fixture.Beats.Snapshots.Last().Keys);
            Assert.DoesNotContain("workflow:OtherLocal@1", fixture.Beats.Snapshots.Last().Keys);
        }
        finally { fixture.Reads.BeforeRevision = null; release.TrySetResult(); }
    }

    [Theory, InlineData(false, false), InlineData(false, true), InlineData(true, false), InlineData(true, true)]
    public async Task StoppedHeartbeatObservesLateRefreshCompletion(bool sqlite, bool fail)
    {
        var marker = "late-heartbeat-" + Guid.NewGuid().ToString("N");
        var unobserved = new ConcurrentQueue<string>();
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            foreach (var error in args.Exception.Flatten().InnerExceptions)
                if (error.Message == marker) unobserved.Enqueue(error.Message);
            args.SetObserved();
        }
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            var tasks = await CompleteRefreshAfterStopAsync(sqlite, fail, marker);
            // The helper has returned, so no live test frame roots the service or refresh tasks.
            for (var attempt = 0; attempt < 20; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Delay(10);
            }
            Assert.All(tasks, task => Assert.False(task.IsAlive));
            Assert.Empty(unobserved);
        }
        finally { TaskScheduler.UnobservedTaskException -= OnUnobserved; }
    }

    private static async Task<WeakReference[]> CompleteRefreshAfterStopAsync(bool sqlite, bool fail, string marker)
    {
        await using var fixture = await HeartbeatFixture.StartAsync(sqlite);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = new ConcurrentQueue<WeakReference>();
        fixture.Reads.RevisionTaskStarted = task => tasks.Enqueue(new(task));
        fixture.Reads.BeforeRevision = async () =>
        {
            entered.TrySetResult();
            await release.Task;
            if (fail) throw new IOException(marker);
        };
        try
        {
            fixture.Time.Advance(fixture.Settings.HeartbeatInterval);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await UntilAsync(() => fixture.Time.RefreshWaits == 1);
            await UntilAsync(() => tasks.Count == 1);
            await fixture.Heartbeat.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Empty(await fixture.Inner.GetLiveMembersAsync());
            Assert.All(tasks, task => Assert.False(Assert.IsAssignableFrom<Task>(task.Target).IsCompleted));
            release.SetResult();
            await UntilAsync(() => tasks.All(task => task.Target is not Task pending || pending.IsCompleted));
            var gate = (SemaphoreSlim)typeof(HostSettings).GetField("_capabilityGate", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(fixture.Settings)!;
            await UntilAsync(() => gate.CurrentCount == 1);
            if (fail)
            {
                await UntilAsync(() => fixture.Logger.Warnings == 1);
                Assert.StartsWith($"Could not refresh capabilities for host {fixture.Settings.Id}; heartbeating with its last snapshot",
                    Assert.Single(fixture.Logger.Messages));
            }
            else Assert.Equal(0, fixture.Logger.Warnings);
            Assert.Equal(1, fixture.Reads.RevisionReads);
            Assert.Empty(fixture.Beats.Snapshots);
            return tasks.ToArray();
        }
        finally { release.TrySetResult(); }
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task StoppingCancelsHeartbeatQueuedBehindDemandWithoutReadingCatalog(bool sqlite)
    {
        await using var fixture = await HeartbeatFixture.StartAsync(sqlite);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Reads.BeforeRevision = async () => { entered.TrySetResult(); await release.Task; };
        var demand = fixture.Settings.RefreshDemandCapabilitiesAsync(fixture.Observed, fixture.Time);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            fixture.Time.Advance(fixture.Settings.HeartbeatInterval);
            await UntilAsync(() => fixture.Time.RefreshWaits == 1);
            // Observe the actual semaphore queue, rather than race shutdown against Task.Run dispatch.
            var gate = (SemaphoreSlim)typeof(HostSettings).GetField("_capabilityGate", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(fixture.Settings)!;
            var head = typeof(SemaphoreSlim).GetField("m_asyncHead", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await UntilAsync(() => head.GetValue(gate) != null);
            await fixture.Heartbeat.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            await UntilAsync(() => head.GetValue(gate) == null);
            Assert.Empty(await fixture.Inner.GetLiveMembersAsync());
            release.SetResult();
            Assert.NotNull(await demand.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, fixture.Reads.RevisionReads);
            Assert.Equal(0, fixture.Reads.VersionReads);
            Assert.Equal(0, fixture.Logger.Warnings);
        }
        finally { release.TrySetResult(); await demand; }
    }

    private sealed class HeartbeatFixture : IAsyncDisposable
    {
        private readonly string? _root;
        internal readonly HeartbeatTime Time = new();
        internal readonly IWorkflowStateRepository Inner;
        internal readonly WorkflowCatalog Catalog;
        internal readonly IWorkflowCatalog Observed;
        internal readonly CatalogReads Reads;
        internal readonly HeartbeatReads Beats;
        internal readonly HostSettings Settings = HostSettings.Default with { BundleHostingEnabled = true };
        internal readonly HeartbeatLogger Logger = new();
        internal readonly WorkflowHeartbeatService Heartbeat;
        private HeartbeatFixture(bool sqlite)
        {
            if (sqlite)
            {
                _root = Path.Combine(Path.GetTempPath(), "IxIFlow.HeartbeatIsolation", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_root);
                Inner = new SqliteWorkflowStateRepository("Data Source=" + Path.Combine(_root, "state.db"), Time);
            }
            else Inner = new InMemoryWorkflowStateRepository(Time);
            Catalog = new(Inner);
            Observed = DispatchProxy.Create<IWorkflowCatalog, CatalogReads>();
            Reads = (CatalogReads)(object)Observed;
            Reads.Inner = Catalog;
            var store = DispatchProxy.Create<IWorkflowStateRepository, HeartbeatReads>();
            Beats = (HeartbeatReads)(object)store;
            Beats.Inner = Inner;
            Heartbeat = new(store, Observed, Settings, Time, Logger);
        }
        internal static async Task<HeartbeatFixture> StartAsync(bool sqlite)
        {
            var fixture = new HeartbeatFixture(sqlite);
            await fixture.Catalog.RegisterWorkflowAsync(Workflow.Create<CatalogArtifactContractTests.Data>("HeartbeatLocal")
                .Step<CatalogArtifactContractTests.Noop>().Build());
            await fixture.Heartbeat.StartAsync(CancellationToken.None);
            await UntilAsync(() => fixture.Time.PeriodicTimers == 1);
            fixture.Reads.Reset();
            return fixture;
        }
        public async ValueTask DisposeAsync()
        {
            await Heartbeat.StopAsync(CancellationToken.None);
            Heartbeat.Dispose();
            Catalog.Dispose();
            if (Inner is IAsyncDisposable disposable) await disposable.DisposeAsync();
            if (_root != null) await SqliteTestFiles.DeleteAsync(_root);
        }
    }

    [SqlServerFact, Trait("Category", "SqlIntegration")]
    public async Task SqlServerHeldWriterKeepsHeartbeatsLiveAcrossCommitAndRollback()
    {
        foreach (var snapshot in new[] { false, true })
        foreach (var commit in new[] { false, true })
        {
            var cs = await SqlServerTestServer.CreateDatabaseAsync("CatalogHeldWriter");
            try { await VerifyHeldSqlWriterAsync(cs, snapshot, commit); }
            finally { await SqlServerTestServer.DropDatabaseAsync(cs); }
        }
    }

    private static async Task VerifyHeldSqlWriterAsync(string cs, bool snapshot, bool commit)
    {
        await using var holder = new SqlConnection(cs);
        await holder.OpenAsync();
        await holder.ExecuteAsync($"ALTER DATABASE [{holder.Database}] SET READ_COMMITTED_SNAPSHOT {(snapshot ? "ON" : "OFF")}");
        Assert.Equal(snapshot, await holder.ExecuteScalarAsync<bool>(
            "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = DB_NAME()"));
        var inner = new SqlWorkflowStateRepository(cs);
        using var catalog = new WorkflowCatalog(inner);
        var observed = DispatchProxy.Create<IWorkflowCatalog, CatalogReads>();
        var reads = (CatalogReads)(object)observed;
        reads.Inner = catalog;
        var store = DispatchProxy.Create<IWorkflowStateRepository, HeartbeatReads>();
        var beats = (HeartbeatReads)(object)store;
        beats.Inner = inner;
        var settings = HostSettings.Default with { BundleHostingEnabled = true, HeartbeatInterval = TimeSpan.FromSeconds(1) };
        var logger = new HeartbeatLogger();
        using var heartbeat = new WorkflowHeartbeatService(store, observed, settings, TimeProvider.System, logger);
        var digest = await inner.PutAsync(ArtifactKind.Bundle, new MemoryStream([1, 2, 3]));
        var held = new WorkflowVersion("HeldPublication", 1, "held-hash", ArtifactDigest: digest,
            ManifestJson: CompatibleManifest("HeldPublication"));
        var before = await inner.GetCatalogRevisionAsync();
        await heartbeat.StartAsync(CancellationToken.None);
        var registered = Assert.Single(await inner.GetLiveMembersAsync());
        reads.Reset();
        await using var transaction = await holder.BeginTransactionAsync();
        var finished = false;
        try
        {
            // Match CatalogWriteAsync, CatalogRevisionAdvance and CatalogVersionInsert exactly:
            // the application lock, counter increment/read and stamped insert share one transaction.
            await holder.ExecuteAsync("""
                DECLARE @catalogLock INT; EXEC @catalogLock = sys.sp_getapplock @Resource=N'IxIFlow.Catalog', @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=30000; IF @catalogLock < 0 THROW 51000, 'Could not lock the workflow catalog', 1;
                UPDATE dbo.IxIFlowCatalogRevision SET Revision=Revision+1 WHERE Id=1;
                """, transaction: transaction);
            var revision = await holder.ExecuteScalarAsync<long>(
                "SELECT Revision FROM dbo.IxIFlowCatalogRevision WHERE Id=1", transaction: transaction);
            Assert.Equal(before + 1, revision);
            await holder.ExecuteAsync("""
                INSERT INTO dbo.IxIFlowWorkflowVersions (Name, Version, StructureHash, State, ArtifactDigest, MetadataJson, CatalogRevision) VALUES (@Name, @Version, @StructureHash, @State, @ArtifactDigest, @MetadataJson, @CatalogRevision)
                """, new
            {
                held.Name, held.Version, held.StructureHash, State = held.State.ToString(),
                ArtifactDigest = held.ArtifactDigest!.Value.Value,
                MetadataJson = System.Text.Json.JsonSerializer.Serialize(held), CatalogRevision = revision
            }, transaction);
            await UntilAsync(() => Volatile.Read(ref reads.RevisionReads) >= 1);
            if (!snapshot)
            {
                // Verify the heartbeat really reached the held SQL lock, rather than merely
                // taking no refresh during the transaction.
                var session = await holder.ExecuteScalarAsync<int>("SELECT @@SPID", transaction: transaction);
                await using var observer = new SqlConnection(cs);
                await observer.OpenAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (!await observer.ExecuteScalarAsync<bool>(new CommandDefinition("""
                    SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.dm_exec_requests WHERE blocking_session_id=@Session)
                    THEN 1 ELSE 0 END AS bit)
                    """, new { Session = session }, cancellationToken: timeout.Token))) await Task.Delay(10, timeout.Token);
            }
            var previous = registered.HeartbeatAtUtc;
            // Hold longer than the three-interval liveness window, observing actual persisted
            // renewals against SQL Server's clock, under both locking RC and RCSI.
            var observedBeats = 0;
            for (var count = 1; count <= 4; count++)
            {
                await UntilAsync(() => beats.Snapshots.Count > observedBeats);
                var member = Assert.Single(await inner.GetLiveMembersAsync());
                Assert.True(member.HeartbeatAtUtc > previous);
                Assert.True(member.HeartbeatAtUtc - previous < settings.HeartbeatInterval * 3);
                Assert.DoesNotContain("workflow:HeldPublication@1", member.Capabilities.Keys);
                previous = member.HeartbeatAtUtc;
                observedBeats = beats.Snapshots.Count;
            }
            Assert.True(previous - registered.HeartbeatAtUtc >= settings.HeartbeatInterval * 3);
            Assert.All(beats.Snapshots, values => Assert.DoesNotContain("workflow:HeldPublication@1", values.Keys));
            if (commit) await transaction.CommitAsync();
            else await transaction.RollbackAsync();
            finished = true;
            var countAtRelease = beats.Snapshots.Count;
            await UntilAsync(() => beats.Snapshots.Count > countAtRelease &&
                (!commit || beats.Snapshots.Last().ContainsKey("workflow:HeldPublication@1")));
            Assert.Equal(commit ? before + 1 : before, await inner.GetCatalogRevisionAsync());
            Assert.Equal(commit, (await inner.GetWorkflowVersionAsync(held.Name, held.Version)) != null);
            if (commit) Assert.Equal(held.StructureHash, beats.Snapshots.Last()["workflow:HeldPublication@1"]);
            else Assert.All(beats.Snapshots, values => Assert.DoesNotContain("workflow:HeldPublication@1", values.Keys));
            var later = held with { Name = "LaterPublication", StructureHash = "later-hash" };
            await inner.RegisterWorkflowVersionAsync(later);
            await UntilAsync(() => beats.Snapshots.Last().ContainsKey("workflow:LaterPublication@1"));
            var final = Assert.Single(await inner.GetLiveMembersAsync());
            Assert.Equal(later.StructureHash, final.Capabilities["workflow:LaterPublication@1"]);
            Assert.Equal(commit, final.Capabilities.ContainsKey("workflow:HeldPublication@1"));
            Assert.Equal(0, logger.Warnings);
        }
        finally
        {
            if (!finished) await transaction.RollbackAsync();
            await heartbeat.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static string CompatibleManifest(string name)
    {
        var shared = new[] { typeof(WorkflowDefinition).Assembly, typeof(object).Assembly, Assembly.Load("System.Runtime") }
            .Select(assembly => assembly.GetName()).ToDictionary(assembly => assembly.FullName, assembly => assembly.Version!.ToString());
        return System.Text.Json.JsonSerializer.Serialize(new WorkflowBundleManifest(1, name, 1, "hash", "data", "entry",
            typeof(WorkflowDefinition).Assembly.GetName().Version!.ToString(), WorkflowDefinitionValidator.CheckpointContractVersion, "",
            new("test", "latest", "release", true, "portable"), shared, new[] { WorkflowBundleArchive.AssemblyPath, WorkflowBundleArchive.PdbPath,
                WorkflowBundleArchive.YamlPath, WorkflowBundleArchive.SourcePath }
                .ToDictionary(path => path, _ => ArtifactDigest.Of(new byte[] { 1 }))));
    }

    private sealed class HeartbeatTime : TimeProvider
    {
        private readonly FakeTimeProvider _clock = new();
        public int RefreshWaits;
        public int PeriodicTimers;
        public void Advance(TimeSpan amount) => _clock.Advance(amount);
        public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow();
        public override long GetTimestamp() => _clock.GetTimestamp();
        public override long TimestampFrequency => _clock.TimestampFrequency;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _clock.CreateTimer(callback, state, dueTime, period);
            // Observe timeout installation so advancing fake time never races the awaiting loop.
            if (period == Timeout.InfiniteTimeSpan) Interlocked.Increment(ref RefreshWaits);
            else Interlocked.Increment(ref PeriodicTimers);
            return timer;
        }
    }

    private sealed class HeartbeatLogger : ILogger<WorkflowHeartbeatService>
    {
        public int Warnings;
        public readonly ConcurrentQueue<string> Messages = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            if (level != LogLevel.Warning) return;
            Messages.Enqueue(formatter(state, error));
            Interlocked.Increment(ref Warnings);
        }
    }

    public class HeartbeatReads : DispatchProxy
    {
        public IWorkflowStateRepository Inner = null!;
        public readonly ConcurrentQueue<IReadOnlyDictionary<string, string>> Snapshots = new();
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IWorkflowStateRepository.HeartbeatAsync))
                return BeatAsync((Guid)args![0]!, (IReadOnlyDictionary<string, string>)args[1]!, (CancellationToken)args[2]!);
            try { return method.Invoke(Inner, args); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
        private async Task<bool> BeatAsync(Guid id, IReadOnlyDictionary<string, string> values, CancellationToken token)
        {
            var result = await Inner.HeartbeatAsync(id, values, token);
            Snapshots.Enqueue(values);
            return result;
        }
    }

    public class CatalogReads : DispatchProxy
    {
        public IWorkflowCatalog Inner = null!;
        public int VersionReads;
        public int RevisionReads;
        public long LastAfter = -1;
        public Func<long, long>? RevisionMap;
        public IReadOnlyList<WorkflowVersion> Rows = [];
        public Func<Task>? AfterDelta;
        public Func<Task>? BeforeDelta;
        public Func<Task>? AfterRevision;
        public Func<Task>? BeforeRevision;
        public Action? BeforeRevisionTask;
        public Action<Task>? RevisionTaskStarted;
        public Func<WorkflowVersion, WorkflowVersion>? Transform;
        public Func<WorkflowVersion, bool>? Filter;
        public void Reset() { VersionReads = 0; RevisionReads = 0; Rows = []; }
        public void AssertSingle(string name)
        {
            Assert.Equal(1, VersionReads);
            Assert.Equal(name, Assert.Single(Rows).Name);
        }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IWorkflowCatalog.GetCatalogRevisionAsync))
            {
                Interlocked.Increment(ref RevisionReads);
                BeforeRevisionTask?.Invoke();
                var task = ReadRevision();
                RevisionTaskStarted?.Invoke(task);
                return task;
            }
            if (method.Name == nameof(IWorkflowCatalog.GetWorkflowVersionChangesAsync))
                return ReadDelta((long)args![0]!);
            if (method.Name == nameof(IWorkflowCatalog.GetWorkflowVersionsAsync))
                throw new InvalidOperationException("Capability refresh must never enumerate the full catalog");
            try { return method.Invoke(Inner, args); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
        private async Task<long> ReadRevision()
        {
            if (BeforeRevision != null) await BeforeRevision();
            var revision = await Inner.GetCatalogRevisionAsync();
            if (RevisionMap != null) revision = RevisionMap(revision);
            if (AfterRevision != null) await AfterRevision();
            return revision;
        }
        private async Task<WorkflowCatalogChanges> ReadDelta(long after)
        {
            VersionReads++;
            LastAfter = after;
            if (BeforeDelta != null) await BeforeDelta();
            var changes = await Inner.GetWorkflowVersionChangesAsync(after);
            if (RevisionMap != null) changes = changes with { Revision = RevisionMap(changes.Revision) };
            if (Transform != null) changes = changes with { Versions = changes.Versions.Select(Transform).ToArray() };
            if (Filter != null) changes = changes with { Versions = changes.Versions.Where(Filter).ToArray() };
            Rows = changes.Versions;
            if (AfterDelta != null) await AfterDelta();
            return changes;
        }
    }
}
