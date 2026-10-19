using System.Text.Json;
using Dapper;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "registration")]
[Trait("Spec", "F-10")]
[Trait("Spec", "F-11")]
[Trait("Spec", "F-15")]
[Trait("Spec", "H-05")]
public sealed class CatalogArtifactReviewRegressionTests
{
    public enum Scenario { Registration, DraftRevisions, StartWins, RetirementSelection, Names, Retry, Reupload, MissingArtifacts, ArtifactLock, Pages, DecoratedRetention }
    public static TheoryData<Scenario> Scenarios => new(Enum.GetValues<Scenario>());

    [Theory, MemberData(nameof(Scenarios))]
    public Task Memory(Scenario scenario)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        return VerifyAsync(new InMemoryWorkflowStateRepository(time), scenario,
            () => { time.Advance(TimeSpan.FromDays(91)); return Task.CompletedTask; });
    }

    [Theory, MemberData(nameof(Scenarios)), Trait("Category", "SqliteIntegration")]
    public async Task Sqlite(Scenario scenario)
    {
        var folder = TemporaryFolder();
        try
        {
            var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
            await using var store = new SqliteWorkflowStateRepository("Data Source=" + Path.Combine(folder, "catalog.db"), time);
            await VerifyAsync(store, scenario, () => { time.Advance(TimeSpan.FromDays(91)); return Task.CompletedTask; });
        }
        finally { await SqliteTestFiles.DeleteAsync(folder); }
    }

    [Theory, MemberData(nameof(Scenarios)), Trait("Category", "SqlIntegration")]
    public async Task SqlServer(Scenario scenario)
    {
        var cs = SqlTestDatabase<CatalogArtifactReviewRegressionTests>.ConnectionString;
        await VerifyAsync(new SqlWorkflowStateRepository(cs), scenario, async () =>
        {
            await using var connection = new SqlConnection(cs);
            await connection.ExecuteAsync("UPDATE dbo.IxIFlowArtifacts SET CreatedAtUtc=DATEADD(day,-91,CreatedAtUtc)");
        });
    }

    [Theory, MemberData(nameof(Scenarios)), Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSql(Scenario scenario)
    {
        var cs = PostgreSqlTestDatabase<CatalogArtifactReviewRegressionTests>.ConnectionString;
        await VerifyAsync(new PostgreSqlWorkflowStateRepository(cs), scenario, async () =>
        {
            await using var connection = new NpgsqlConnection(cs);
            await connection.ExecuteAsync("UPDATE ixiflow_artifacts SET created_at_utc=created_at_utc - INTERVAL '91 days'");
        });
    }

    private static async Task VerifyAsync(IWorkflowStateRepository store, Scenario scenario, Func<Task> age)
    {
        var name = "Review-" + Guid.NewGuid().ToString("N");
        switch (scenario)
        {
            case Scenario.Registration:
            {
                var recording = new RecordingCatalog(store);
                var catalog = new WorkflowCatalog(recording);
                var definition = Definition(name);
                await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => catalog.RegisterWorkflowAsync(definition)));
                await using var provider = Provider(store, catalog);
                var client = provider.GetRequiredService<IWorkflowClient>();
                for (var i = 0; i < 3; i++) await client.RunAsync(definition, new Data());
                Assert.Equal(1, recording.Registrations);
                Assert.True(recording.VersionReads >= 3);
                await catalog.RetireWorkflowVersionAsync(name, 1);
                await Refused(() => client.RunAsync(definition, new Data()), WorkflowErrorKind.ArtifactUnavailable);
                Assert.Equal(1, recording.Registrations);
                await Refused(() => catalog.RegisterWorkflowAsync(Workflow.Create<Data>(name).Step<Noop>().Step<Noop>().Build()),
                    WorkflowErrorKind.InvalidRequest);
                break;
            }
            case Scenario.DraftRevisions:
            {
                var initial = await store.SaveDefinitionAsync(new(name, 1, "workflow", "initial"), 0);
                var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(async i =>
                {
                    try { return await store.SaveDefinitionAsync(initial with { Source = i.ToString() }, initial.Revision); }
                    catch (WorkflowEngineException error) when (error.Kind == WorkflowErrorKind.InvalidState) { return null; }
                }));
                var winner = Assert.Single(results.OfType<WorkflowDraft>());
                Assert.Equal(2, winner.Revision);
                Assert.Equal(winner, await store.GetDefinitionAsync(name, 1));
                var inserts = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
                {
                    try { await store.SaveDefinitionAsync(new(name, 2, "workflow", "insert"), 0); return true; }
                    catch (WorkflowEngineException error) when (error.Kind == WorkflowErrorKind.InvalidState) { return false; }
                }));
                Assert.Single(inserts, value => value);
                break;
            }
            case Scenario.StartWins:
            {
                var clock = new StartWinsClock();
                var folder = store is SqliteWorkflowStateRepository ? TemporaryFolder() : null;
                try
                {
                    // Only this scenario needs a clock that can stop a commit after it takes its lock.
                    await using var sqlite = folder == null ? null : new SqliteWorkflowStateRepository(
                        "Data Source=" + Path.Combine(folder, "start-wins.db"), clock);
                    store = sqlite ?? (store is InMemoryWorkflowStateRepository
                        ? new InMemoryWorkflowStateRepository(clock) : store);
                    await store.RegisterWorkflowVersionAsync(new(name, 1, "child"));
                    var caller = name + "-caller";
                    await store.RegisterWorkflowVersionAsync(new(caller, 1, "parent", Dependencies: [new(name, 1)]));
                    var parent = Instance(caller);
                    var lease = await StoreTestSupport.LeaseAsync(store, parent.InstanceId);
                    WorkflowEngineException error;
                    if (store is InMemoryWorkflowStateRepository or SqliteWorkflowStateRepository)
                    {
                        // Memory reads the clock under its catalog/claim locks; SQLite reads it after
                        // BEGIN IMMEDIATE, while holding its writer gate (also its catalog lock).
                        clock.Pause = true;
                        error = await RaceAsync(() => clock.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)),
                            _ => Task.CompletedTask, () => { clock.Release.TrySetResult(); return Task.CompletedTask; });
                    }
                    else
                    {
                        var sqlServer = store is SqlWorkflowStateRepository;
                        var connectionString = sqlServer
                            ? SqlTestDatabase<CatalogArtifactReviewRegressionTests>.ConnectionString
                            : PostgreSqlTestDatabase<CatalogArtifactReviewRegressionTests>.ConnectionString;
                        await using System.Data.Common.DbConnection blocker = sqlServer
                            ? new SqlConnection(connectionString) : new NpgsqlConnection(connectionString);
                        await blocker.OpenAsync();
                        var blockerId = await blocker.ExecuteScalarAsync<int>(sqlServer ? "SELECT @@SPID" : "SELECT pg_backend_pid()");
                        await using var transaction = await blocker.BeginTransactionAsync();
                        // The start takes its shared catalog lock before reading/locking this lease.
                        await blocker.ExecuteAsync(sqlServer
                            ? "UPDATE dbo.IxIFlowWorkflowLeases SET Epoch=Epoch WHERE InstanceId=@InstanceId"
                            : "UPDATE ixiflow_workflow_leases SET epoch=epoch WHERE instance_id=@InstanceId",
                            new { parent.InstanceId }, transaction);
                        await using System.Data.Common.DbConnection observer = sqlServer
                            ? new SqlConnection(connectionString) : new NpgsqlConnection(connectionString);
                        await observer.OpenAsync();
                        error = await RaceAsync(
                            () => UntilAsync(async () => await observer.ExecuteScalarAsync<int>(sqlServer
                                ? """
                                  SELECT COUNT(*) FROM sys.dm_exec_requests r
                                  JOIN sys.dm_tran_locks l ON l.request_session_id=r.session_id
                                  WHERE r.blocking_session_id=@BlockerId AND l.resource_database_id=DB_ID()
                                    AND l.resource_type='APPLICATION' AND l.resource_description LIKE '%IxIFlow.Catalog%'
                                    AND l.request_mode='S' AND l.request_status='GRANT'
                                  """
                                : """
                                  SELECT COUNT(*)::int FROM pg_locks l
                                  WHERE @BlockerId=ANY(pg_blocking_pids(l.pid)) AND l.locktype='advisory'
                                    AND l.objid=(hashtext('IxIFlow.Catalog')::bigint & 4294967295)::oid
                                    AND l.mode='ShareLock' AND l.granted
                                  """, new { BlockerId = blockerId }) == 1),
                            count => UntilAsync(async () => await observer.ExecuteScalarAsync<int>(sqlServer
                                ? """
                                  SELECT COUNT(*) FROM sys.dm_tran_locks
                                  WHERE resource_database_id=DB_ID() AND resource_type='APPLICATION'
                                    AND resource_description LIKE '%IxIFlow.Catalog%'
                                    AND request_mode='X' AND request_status='WAIT'
                                  """
                                : """
                                  SELECT COUNT(*)::int FROM pg_locks
                                  WHERE database=(SELECT oid FROM pg_database WHERE datname=current_database())
                                    AND locktype='advisory'
                                    AND objid=(hashtext('IxIFlow.Catalog')::bigint & 4294967295)::oid
                                    AND mode='ExclusiveLock' AND NOT granted
                                  """) == count),
                            () => transaction.RollbackAsync());
                    }
                    Assert.Equal(WorkflowErrorKind.InvalidState, error.Kind);
                    Assert.Contains("has unfinished parent instances of", error.Message);
                    Assert.DoesNotContain("required by caller", error.Message);
                    Assert.Contains(name, error.Message);
                    parent = (await store.GetWorkflowInstanceAsync(parent.InstanceId))!;
                    parent.Status = WorkflowStatus.Completed;
                    await store.SeedAsync(parent);
                    await store.SetWorkflowVersionStateAsync(name, 1, WorkflowVersionState.Retired);
                    Assert.Equal(WorkflowVersionState.Retired, (await store.GetWorkflowVersionAsync(name, 1))!.State);

                    async Task<WorkflowEngineException> RaceAsync(Func<Task> held, Func<int, Task> queued, Func<Task> release)
                    {
                        var starting = Task.Run(() => store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(parent, lease.Epoch, 0)));
                        Task? retiringCaller = null;
                        Task? retiringChild = null;
                        try
                        {
                            await held();
                            // Queue both requests before releasing the start. Awaiting caller retirement
                            // here would let the child take its snapshot only after the start committed.
                            retiringCaller = store.SetWorkflowVersionStateAsync(caller, 1, WorkflowVersionState.Retired);
                            await queued(1);
                            retiringChild = store.SetWorkflowVersionStateAsync(name, 1, WorkflowVersionState.Retired);
                            await queued(2);
                            await Task.WhenAny(retiringChild, Task.Delay(50)); // Only a bounded blocked assertion.
                            Assert.False(starting.IsCompleted);
                            Assert.False(retiringCaller.IsCompleted);
                            Assert.False(retiringChild.IsCompleted);
                        }
                        finally { await release(); }
                        Assert.Equal(WorkflowCommitStatus.Applied, (await starting.WaitAsync(TimeSpan.FromSeconds(10))).Status);
                        await retiringCaller!.WaitAsync(TimeSpan.FromSeconds(10));
                        Assert.Equal(WorkflowVersionState.Retired, (await store.GetWorkflowVersionAsync(caller, 1))!.State);
                        return await Assert.ThrowsAsync<WorkflowEngineException>(() => retiringChild!.WaitAsync(TimeSpan.FromSeconds(10)));
                    }

                    static async Task UntilAsync(Func<Task<bool>> reached)
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        while (!await reached())
                        {
                            timeout.Token.ThrowIfCancellationRequested();
                            await Task.Yield();
                        }
                    }
                }
                finally { if (folder != null) await SqliteTestFiles.DeleteAsync(folder); }
                break;
            }
            case Scenario.RetirementSelection:
            {
                await store.RegisterWorkflowVersionAsync(new(name, 1, "child"));
                var caller = name + "-caller";
                await store.RegisterWorkflowVersionAsync(new(caller, 1, "caller", Dependencies: [new(name, 1)]));
                await store.RegisterWorkflowVersionAsync(new(caller, 2, "unrelated-version"));
                var terminal = Instance(caller);
                terminal.Status = WorkflowStatus.Terminated;
                await store.SeedAsync(terminal);
                var unrelatedVersion = Instance(caller);
                unrelatedVersion.WorkflowVersion = 2;
                await store.SeedAsync(unrelatedVersion);
                await store.SeedAsync(Instance(name + "-unrelated"));
                await store.SetWorkflowVersionStateAsync(caller, 1, WorkflowVersionState.Retired);
                await store.SetWorkflowVersionStateAsync(name, 1, WorkflowVersionState.Retired);
                Assert.Equal(WorkflowVersionState.Retired, (await store.GetWorkflowVersionAsync(name, 1))!.State);
                break;
            }
            case Scenario.Names:
            {
                await store.SaveDefinitionAsync(new(name, 1, "workflow", "original"), 0);
                foreach (var invalid in new[] { name + " ", " " + name, name + "\t", "\n" + name })
                {
                    await Assert.ThrowsAsync<ArgumentException>(() => store.SaveDefinitionAsync(new(invalid, 1, "workflow", "bad"), 1));
                    await Assert.ThrowsAsync<ArgumentException>(() => store.GetDefinitionAsync(invalid, 1));
                    await Assert.ThrowsAsync<ArgumentException>(() => store.RegisterWorkflowVersionAsync(new(invalid, 1, "bad")));
                    await Assert.ThrowsAsync<ArgumentException>(() => store.GetWorkflowVersionAsync(invalid, 1));
                    await Assert.ThrowsAsync<ArgumentException>(() => store.GetWorkflowVersionsAsync(invalid));
                    await Assert.ThrowsAsync<ArgumentException>(() => store.SetWorkflowVersionStateAsync(invalid, 1, WorkflowVersionState.Retired));
                    await Assert.ThrowsAsync<ArgumentException>(() => store.RegisterWorkflowVersionAsync(new(name, 2, "bad", Dependencies: [new(invalid, 1)])));
                    Assert.Throws<WorkflowEngineException>(() => Definition(invalid));
                    await using var provider = Provider(store, new WorkflowCatalog(store));
                    await Assert.ThrowsAsync<ArgumentException>(() => provider.GetRequiredService<IWorkflowClient>().StartAsync(invalid, new Data()));
                    await Assert.ThrowsAsync<ArgumentException>(() => store.SeedAsync(Instance(invalid)));
                }
                Assert.Equal("original", (await store.GetDefinitionAsync(name, 1))!.Source);
                break;
            }
            case Scenario.Retry:
            {
                var catalog = new WorkflowCatalog(store, [Definition(name)]);
                await using var provider = Provider(store, catalog);
                var client = provider.GetRequiredService<IWorkflowClient>();
                var options = new WorkflowOptions { InstanceId = Guid.NewGuid().ToString("N") };
                var id = await client.StartAsync(name, new Data(), options);
                await client.AwaitAsync(id);
                await catalog.RegisterWorkflowAsync(Definition(name, 2));
                Assert.Equal(id, await client.StartAsync(name, new Data(), options));
                await Refused(() => client.StartAsync(name, 2, new Data(), options), WorkflowErrorKind.InvalidRequest);
                await catalog.SetWorkflowVersionStateAsync(name, 1, WorkflowVersionState.Deprecated);
                Assert.Equal(id, await client.StartAsync(name, new Data(), options));
                await catalog.RetireWorkflowVersionAsync(name, 1);
                await catalog.RetireWorkflowVersionAsync(name, 2);
                Assert.Equal(id, await client.StartAsync(name, new Data(), options));
                Assert.Equal(1, (await store.GetWorkflowInstanceSummaryAsync(id))!.WorkflowVersion);
                await Refused(() => client.StartAsync(name + "-other", new Data(), options), WorkflowErrorKind.InvalidRequest);
                break;
            }
            case Scenario.Reupload:
                await ReuploadAsync(store, store, age, name);
                break;
            case Scenario.MissingArtifacts:
                await MissingArtifactsAsync(store, store, name);
                break;
            case Scenario.ArtifactLock:
            {
                var digest = await store.PutAsync(ArtifactKind.Bundle, new MemoryStream([67, 1, 5]));
                await age();
                var paused = new PausedArtifactRead(store);
                var registering = Task.Run(() => store.RegisterWorkflowVersionAsync(new(name, 1, "locked", ArtifactDigest: digest), paused));
                await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var collecting = Task.Run(() => store.PurgeArtifactsAsync(store, TimeSpan.FromDays(90), 1));
                try
                {
                    await Task.Delay(50);
                    Assert.False(collecting.IsCompleted);
                }
                finally { paused.Release.TrySetResult(); }
                await registering;
                Assert.Equal(0, await collecting);
                Assert.True(await store.ExistsAsync(digest));
                break;
            }
            case Scenario.Pages:
            {
                var references = new List<ArtifactDigest>();
                for (var i = 0; i < 128; i++)
                    references.Add(await store.PutAsync(ArtifactKind.Template, new MemoryStream(BitConverter.GetBytes(i))));
                await store.RegisterWorkflowVersionAsync(new(name, 1, "referenced", ArtifactReferences: references));
                await age();
                var orphans = new List<ArtifactDigest>();
                for (var i = 128; i < 133; i++)
                    orphans.Add(await store.PutAsync(ArtifactKind.Template, new MemoryStream(BitConverter.GetBytes(i))));
                await age();
                Assert.Equal(2, await store.PurgeArtifactsAsync(store, TimeSpan.FromDays(90), 2));
                Assert.Equal(2, await store.PurgeArtifactsAsync(store, TimeSpan.FromDays(90), 2));
                Assert.Equal(1, await store.PurgeArtifactsAsync(store, TimeSpan.FromDays(90), 2));
                Assert.Equal(0, await store.PurgeArtifactsAsync(store, TimeSpan.FromDays(90), 2));
                foreach (var digest in references) Assert.True(await store.ExistsAsync(digest));
                foreach (var digest in orphans) Assert.False(await store.ExistsAsync(digest));
                break;
            }
            case Scenario.DecoratedRetention:
            {
                var decorated = new FaultInjectingRepository(store);
                var services = new ServiceCollection().AddLogging();
                services.AddIxIFlow();
                services.AddSingleton<IWorkflowStateRepository>(decorated);
                await using var provider = services.BuildServiceProvider();
                var artifacts = provider.GetRequiredService<IArtifactStore>();
                var digest = await artifacts.PutAsync(ArtifactKind.Bundle, new MemoryStream([19, 67]));
                await age();
                var retention = provider.GetServices<IHostedService>().OfType<WorkflowRetentionService>().Single();
                await retention.PurgeOnceAsync(CancellationToken.None);
                Assert.False(await artifacts.ExistsAsync(digest));
                break;
            }
        }
    }

    private static async Task ReuploadAsync(IWorkflowStateRepository store, IArtifactStore artifacts, Func<Task> age, string name)
    {
        byte[] bytes = [67, 5, 19];
        var digest = await artifacts.PutAsync(ArtifactKind.Bundle, new MemoryStream(bytes));
        await age();
        Assert.Equal(digest, await artifacts.PutAsync(ArtifactKind.Package, new MemoryStream(bytes)));
        Assert.Equal(0, await store.PurgeArtifactsAsync(artifacts, TimeSpan.FromDays(90), 1));
        await store.RegisterWorkflowVersionAsync(new(name, 1, "reupload", ArtifactDigest: digest), artifacts);
        Assert.True(await artifacts.ExistsAsync(digest));
    }

    private static async Task MissingArtifactsAsync(IWorkflowStateRepository store, IArtifactStore artifacts, string name)
    {
        var missing = ArtifactDigest.Of([67, 0, 0]);
        await Refused(() => store.RegisterWorkflowVersionAsync(new(name, 1, "missing", ArtifactDigest: missing), artifacts),
            WorkflowErrorKind.ArtifactUnavailable);
        await Refused(() => store.RegisterWorkflowVersionAsync(new(name, 2, "missing", ArtifactReferences: [missing]), artifacts),
            WorkflowErrorKind.ArtifactUnavailable);
        Assert.Null(await store.GetWorkflowVersionAsync(name, 1));
        var digest = await artifacts.PutAsync(ArtifactKind.Bundle, new MemoryStream([67, 1, 0]));
        await store.RegisterWorkflowVersionAsync(new(name, 3, "publish", WorkflowVersionState.Draft, ArtifactDigest: digest), artifacts);
        await artifacts.DeleteAsync(digest);
        await Refused(() => store.SetWorkflowVersionStateAsync(name, 3, WorkflowVersionState.Published, artifacts),
            WorkflowErrorKind.ArtifactUnavailable);
        Assert.Equal(WorkflowVersionState.Draft, (await store.GetWorkflowVersionAsync(name, 3))!.State);
    }

    [Fact]
    public async Task MemoryOrdinaryCommitsAndDraftEditsDoNotTakeTheCatalogGate()
    {
        var store = new InMemoryWorkflowStateRepository();
        var parent = Instance("Memory-lock-scope");
        await store.SeedAsync(parent);
        var lease = await StoreTestSupport.LeaseAsync(store, parent.InstanceId);
        var barrier = new BarrierArtifacts();
        var holding = store.PurgeArtifactsAsync(barrier, TimeSpan.FromDays(90), 1);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var commit = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(parent, lease.Epoch, parent.Revision))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(WorkflowCommitStatus.Applied, commit.Status);
            var draft = await store.SaveDefinitionAsync(new(parent.WorkflowName, 1, "workflow", "draft"), 0)
                .WaitAsync(TimeSpan.FromSeconds(5));
            await store.SaveDefinitionAsync(draft with { Source = "edit" }, 1).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { barrier.Release.TrySetResult(); }
        await holding;
    }

    [Fact]
    public async Task FileReuploadAndReferenceVerification()
    {
        var folder = TemporaryFolder();
        try
        {
            var store = new InMemoryWorkflowStateRepository();
            var artifacts = new FileSystemArtifactStore(folder);
            await ReuploadAsync(store, artifacts, () =>
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*.artifact"))
                    File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-91));
                return Task.CompletedTask;
            }, "File-reupload");
            await MissingArtifactsAsync(store, artifacts, "File-missing");
            var services = new ServiceCollection().AddLogging();
            services.AddIxIFlow().UseFileSystemArtifacts(folder);
            services.AddSingleton<IWorkflowStateRepository>(store);
            await using var provider = services.BuildServiceProvider();
            var catalog = provider.GetRequiredService<IWorkflowCatalog>();
            var digest = await artifacts.PutAsync(ArtifactKind.Bundle, new MemoryStream([67, 9, 0]));
            await catalog.RegisterWorkflowVersionAsync(new("DI-files", 1, "file", ArtifactDigest: digest));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task FileRetentionPagesPastALargeReferencedPrefix()
    {
        var folder = TemporaryFolder();
        try
        {
            var artifacts = new FileSystemArtifactStore(folder);
            var references = new HashSet<ArtifactDigest>();
            for (var i = 0; i < 128; i++)
            {
                var digest = await artifacts.PutAsync(ArtifactKind.Bundle, new MemoryStream(BitConverter.GetBytes(i)));
                references.Add(digest);
                File.SetLastWriteTimeUtc(Path.Combine(folder, digest.Value[7..] + ".artifact"), DateTime.UtcNow.AddDays(-182));
            }
            var orphans = new List<ArtifactDigest>();
            for (var i = 128; i < 133; i++)
            {
                var digest = await artifacts.PutAsync(ArtifactKind.Bundle, new MemoryStream(BitConverter.GetBytes(i)));
                orphans.Add(digest);
                File.SetLastWriteTimeUtc(Path.Combine(folder, digest.Value[7..] + ".artifact"), DateTime.UtcNow.AddDays(-91));
            }
            Assert.Equal(2, await artifacts.PurgeUnreferencedAsync(references, DateTime.UtcNow.AddDays(-90), 2));
            Assert.Equal(2, await artifacts.PurgeUnreferencedAsync(references, DateTime.UtcNow.AddDays(-90), 2));
            Assert.Equal(1, await artifacts.PurgeUnreferencedAsync(references, DateTime.UtcNow.AddDays(-90), 2));
            Assert.Equal(0, await artifacts.PurgeUnreferencedAsync(references, DateTime.UtcNow.AddDays(-90), 2));
            foreach (var digest in references) Assert.True(await artifacts.ExistsAsync(digest));
            foreach (var digest in orphans) Assert.False(await artifacts.ExistsAsync(digest));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task FileRetentionSkipsStraysAndLiveUploadsAndReclaimsStaleStaging()
    {
        var folder = TemporaryFolder();
        try
        {
            var artifacts = new FileSystemArtifactStore(folder);
            var stale = Path.Combine(folder, "abandoned.tmp");
            var live = Path.Combine(folder, "active.tmp");
            var recent = Path.Combine(folder, "recent.tmp");
            var stray = Path.Combine(folder, "README.artifact");
            var uppercase = Path.Combine(folder, new string('A', 64) + ".artifact");
            await File.WriteAllTextAsync(stale, "stale");
            await File.WriteAllTextAsync(recent, "fresh");
            await File.WriteAllTextAsync(stray, "stray");
            await File.WriteAllTextAsync(uppercase, "uppercase");
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));
            await using (var writer = new FileStream(live, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                File.SetLastWriteTimeUtc(writer.SafeFileHandle, DateTime.UtcNow.AddDays(-2));
                var orphan = await artifacts.PutAsync(ArtifactKind.Bundle, new MemoryStream([9, 67]));
                File.SetLastWriteTimeUtc(Path.Combine(folder, orphan.Value[7..] + ".artifact"), DateTime.UtcNow.AddDays(-91));
                Assert.Equal(1, await artifacts.PurgeUnreferencedAsync(new HashSet<ArtifactDigest>(), DateTime.UtcNow.AddDays(-90), 10));
                Assert.True(File.Exists(live));
                await writer.WriteAsync(new byte[] { 1 });
                Assert.False(await artifacts.ExistsAsync(orphan));
            }
            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(recent));
            Assert.True(File.Exists(stray));
            Assert.True(File.Exists(uppercase));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static ServiceProvider Provider(IWorkflowStateRepository store, IWorkflowCatalog catalog)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow();
        services.AddSingleton<IWorkflowStateRepository>(store);
        services.AddSingleton(catalog);
        return services.BuildServiceProvider();
    }

    private static string TemporaryFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.PR67Review", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static async Task Refused(Func<Task> action, WorkflowErrorKind kind) =>
        Assert.Equal(kind, (await Assert.ThrowsAsync<WorkflowEngineException>(action)).Kind);

    private static WorkflowDefinition Definition(string name, int version = 1) => Workflow.Create<Data>(name, version).Step<Noop>().Build();
    private static WorkflowInstance Instance(string name) => new()
    {
        InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = name, WorkflowVersion = 1,
        Status = WorkflowStatus.Suspended, ExecutionStateJson = StoreTestSupport.CheckpointJson,
        WorkflowDataJson = "{}", WorkflowDataType = typeof(Data).AssemblyQualifiedName!
    };
    public sealed class Data;
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class RecordingCatalog(IWorkflowCatalogStore inner) : IWorkflowCatalogStore
    {
        public int Registrations;
        public int VersionReads;
        public Task<WorkflowDraft?> GetDefinitionAsync(string name, int version) => inner.GetDefinitionAsync(name, version);
        public Task<WorkflowDraft> SaveDefinitionAsync(WorkflowDraft definition, long expectedRevision) => inner.SaveDefinitionAsync(definition, expectedRevision);
        public Task RegisterWorkflowVersionAsync(WorkflowVersion version, IArtifactStore? artifacts = null)
        {
            Interlocked.Increment(ref Registrations);
            return inner.RegisterWorkflowVersionAsync(version, artifacts);
        }
        public Task<WorkflowVersion?> GetWorkflowVersionAsync(string name, int version)
        {
            Interlocked.Increment(ref VersionReads);
            return inner.GetWorkflowVersionAsync(name, version);
        }
        public Task<IReadOnlyList<WorkflowVersion>> GetWorkflowVersionsAsync(string? name = null) => inner.GetWorkflowVersionsAsync(name);
        public Task SetWorkflowVersionStateAsync(string name, int version, WorkflowVersionState state, IArtifactStore? artifacts = null) => inner.SetWorkflowVersionStateAsync(name, version, state, artifacts);
        public Task<WorkflowVersion> PublishDefinitionAsync(WorkflowDraftId draftId, WorkflowVersion version, IArtifactStore? artifacts = null) => inner.PublishDefinitionAsync(draftId, version, artifacts);
        public Task<int> PurgeArtifactsAsync(IArtifactStore artifacts, TimeSpan retention, int batchSize, CancellationToken cancellationToken = default) => inner.PurgeArtifactsAsync(artifacts, retention, batchSize, cancellationToken);
    }

    // Used only by StartWins: no production hook, and no clock advances while a commit is held.
    private sealed class StartWinsClock : TimeProvider
    {
        public bool Pause { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override DateTimeOffset GetUtcNow()
        {
            if (Pause)
            {
                Entered.TrySetResult();
                Release.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            }
            return base.GetUtcNow();
        }
    }

    private sealed class BarrierArtifacts : IArtifactStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime olderThanUtc, int batchSize, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return 0;
        }
        public ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class PausedArtifactRead(IArtifactStore inner) : IArtifactStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return await inner.ExistsAsync(digest, cancellationToken);
        }
        public ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken cancellationToken = default) => inner.PutAsync(kind, content, cancellationToken);
        public ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) => inner.OpenAsync(digest, cancellationToken);
        public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) => inner.DeleteAsync(digest, cancellationToken);
        public ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime olderThanUtc, int batchSize, CancellationToken cancellationToken = default) => inner.PurgeUnreferencedAsync(references, olderThanUtc, batchSize, cancellationToken);
    }
}
