using System.Text;
using System.Text.Json;
using Dapper;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "registration")]
[Trait("Spec", "F-10")]
[Trait("Spec", "F-11")]
[Trait("Spec", "F-15")]
public sealed class CatalogArtifactContractTests
{
    public enum Scenario { States, Drafts, Conflicts, Callers, UnfinishedParents, RecursiveParent, Starts, DeprecatedChild, RetiredRecovery, Startup, ConcurrentChild, ConcurrentRoot, Artifacts }
    public static TheoryData<Scenario> Scenarios => new(Enum.GetValues<Scenario>());

    [Theory, MemberData(nameof(Scenarios))]
    public Task MemoryContract(Scenario scenario)
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryWorkflowStateRepository(time);
        return VerifyAsync(store, scenario, () => { time.Advance(TimeSpan.FromDays(91)); return Task.CompletedTask; });
    }

    [Theory, MemberData(nameof(Scenarios)), Trait("Category", "SqliteIntegration")]
    public async Task SqliteContract(Scenario scenario)
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.Catalog", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var cs = "Data Source=" + Path.Combine(folder, "catalog.db");
        try
        {
            var time = new FakeTimeProvider();
            await using var store = new SqliteWorkflowStateRepository(cs, time);
            await VerifyAsync(store, scenario, () => { time.Advance(TimeSpan.FromDays(91)); return Task.CompletedTask; });
            // A fresh catalog reads durable metadata without keeping the original definition delegates.
            if (scenario is not (Scenario.Startup or Scenario.Starts or Scenario.DeprecatedChild or Scenario.RetiredRecovery))
            {
                var reopened = new WorkflowCatalog(store);
                Assert.NotEmpty(await reopened.GetWorkflowVersionsAsync());
            }
        }
        finally { await SqliteTestFiles.DeleteAsync(folder); }
    }

    [SqlServerFact, Trait("Category", "SqlIntegration")]
    public async Task SqlServerContract()
    {
        var cs = SqlTestDatabase<CatalogArtifactContractTests>.ConnectionString;
        var store = new SqlWorkflowStateRepository(cs);
        foreach (var scenario in Enum.GetValues<Scenario>())
            await VerifyAsync(store, scenario, async () =>
            {
                await using var connection = new SqlConnection(cs);
                await connection.ExecuteAsync("UPDATE dbo.IxIFlowArtifacts SET CreatedAtUtc='2000-01-01'");
            });
    }

    [PostgreSqlFact, Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSqlContract()
    {
        var cs = PostgreSqlTestDatabase<CatalogArtifactContractTests>.ConnectionString;
        var store = new PostgreSqlWorkflowStateRepository(cs);
        foreach (var scenario in Enum.GetValues<Scenario>())
            await VerifyAsync(store, scenario, async () =>
            {
                await using var connection = new NpgsqlConnection(cs);
                await connection.ExecuteAsync("UPDATE ixiflow_artifacts SET created_at_utc='2000-01-01'");
            });
    }

    private static async Task VerifyAsync(IWorkflowStateRepository store, Scenario scenario, Func<Task> age)
    {
        var name = "Catalog-" + Guid.NewGuid().ToString("N");
        var catalog = new WorkflowCatalog(store);
        WorkflowVersion Row(int version = 1, WorkflowVersionState state = WorkflowVersionState.Published) =>
            new(name, version, "structure", state);
        switch (scenario)
        {
            case Scenario.States:
                await store.RegisterWorkflowVersionAsync(Row(state: WorkflowVersionState.Draft));
                Assert.Null(await catalog.GetLatestWorkflowVersionAsync(name));
                await Refused(() => store.SetWorkflowVersionStateAsync(name, 1, WorkflowVersionState.Deprecated), WorkflowErrorKind.InvalidState);
                await store.SetWorkflowVersionStateAsync(name, 1, WorkflowVersionState.Published);
                await store.RegisterWorkflowVersionAsync(Row(3));
                await store.RegisterWorkflowVersionAsync(Row(4, WorkflowVersionState.Draft));
                Assert.Equal(3, (await catalog.GetLatestWorkflowVersionAsync(name))!.Version);
                await store.SetWorkflowVersionStateAsync(name, 3, WorkflowVersionState.Deprecated);
                Assert.Equal(1, (await catalog.GetLatestWorkflowVersionAsync(name))!.Version);
                await store.RegisterWorkflowVersionAsync(Row(3));
                Assert.Equal(WorkflowVersionState.Deprecated, (await store.GetWorkflowVersionAsync(name, 3))!.State);
                await catalog.RetireWorkflowVersionAsync(name, 3);
                await catalog.RetireWorkflowVersionAsync(name, 3);
                await Refused(() => store.SetWorkflowVersionStateAsync(name, 3, WorkflowVersionState.Published), WorkflowErrorKind.InvalidState);
                Assert.Equal(WorkflowVersionState.Retired, (await store.GetWorkflowVersionAsync(name, 3))!.State);
                break;
            case Scenario.Drafts:
                var draft = await store.SaveDefinitionAsync(new(name, 1, "workflow", "source"), 0);
                Assert.Equal(1, draft.Revision);
                Assert.Equal(draft, await store.GetDefinitionAsync(name, 1));
                var revised = await store.SaveDefinitionAsync(draft with { Source = "changed" }, 1);
                Assert.Equal(2, revised.Revision);
                await Refused(() => store.SaveDefinitionAsync(draft, 1), WorkflowErrorKind.InvalidState);
                Assert.Equal(revised, await store.GetDefinitionAsync(name, 1));
                await store.RegisterWorkflowVersionAsync(Row());
                break;
            case Scenario.Conflicts:
                var definition = Definition(name);
                await catalog.RegisterWorkflowAsync(definition);
                var original = await store.GetWorkflowVersionAsync(name, 1);
                var other = new WorkflowCatalog(store);
                await other.RegisterWorkflowAsync(Definition(name));
                await Refused(() => other.RegisterWorkflowAsync(Workflow.Create<Data>(name).Step<Noop>().Step<Noop>().Build()), WorkflowErrorKind.InvalidRequest);
                Assert.Equal(original!.StructureHash, (await store.GetWorkflowVersionAsync(name, 1))!.StructureHash);
                Assert.NotNull(await other.GetWorkflowDefinitionAsync(name, 1));
                break;
            case Scenario.Callers:
            case Scenario.UnfinishedParents:
                await store.RegisterWorkflowVersionAsync(Row());
                var caller = new WorkflowVersion(name + "-caller", 1, "caller", Dependencies: [new(name, 1)]);
                await store.RegisterWorkflowVersionAsync(caller);
                await Refused(() => catalog.RetireWorkflowVersionAsync(name, 1), WorkflowErrorKind.InvalidState);
                await store.SetWorkflowVersionStateAsync(caller.Name, 1, WorkflowVersionState.Deprecated);
                await Refused(() => catalog.RetireWorkflowVersionAsync(name, 1), WorkflowErrorKind.InvalidState);
                WorkflowInstance? parent = null;
                if (scenario == Scenario.UnfinishedParents)
                {
                    parent = Instance(caller.Name);
                    await store.SeedAsync(parent);
                }
                await store.SetWorkflowVersionStateAsync(caller.Name, 1, WorkflowVersionState.Retired);
                if (parent != null)
                {
                    await Refused(() => catalog.RetireWorkflowVersionAsync(name, 1), WorkflowErrorKind.InvalidState);
                    parent.Status = WorkflowStatus.Completed;
                    await store.SeedAsync(parent);
                }
                await catalog.RetireWorkflowVersionAsync(name, 1);
                break;
            case Scenario.Starts:
                var services = new ServiceCollection().AddLogging();
                services.AddSingleton(store);
                services.AddSingleton(Definition(name));
                services.AddSingleton(Definition(name, 2));
                services.AddIxIFlow();
                await using (var provider = services.BuildServiceProvider())
                {
                    var registered = provider.GetRequiredService<IWorkflowCatalog>();
                    var client = provider.GetRequiredService<IWorkflowClient>();
                    var latest = await client.StartAsync(name, new Data());
                    Assert.Equal(2, (await store.GetWorkflowInstanceAsync(latest))!.WorkflowVersion);
                    await registered.SetWorkflowVersionStateAsync(name, 2, WorkflowVersionState.Deprecated);
                    var explicitStart = await client.StartAsync(name, 2, new Data());
                    Assert.Equal(2, (await store.GetWorkflowInstanceAsync(explicitStart))!.WorkflowVersion);
                    var selected = await client.StartAsync(name, null, new Data());
                    Assert.Equal(1, (await store.GetWorkflowInstanceAsync(selected))!.WorkflowVersion);
                    await registered.RetireWorkflowVersionAsync(name, 2);
                    await Refused(() => client.StartAsync(name, 2, new Data()), WorkflowErrorKind.ArtifactUnavailable);
                    Assert.Equal(explicitStart, await client.StartAsync(name, 2, new Data(),
                        new WorkflowOptions { InstanceId = explicitStart }));
                    // An existing instance still recovers from a retired code-defined version.
                    Assert.NotNull(await client.GetStatusAsync(explicitStart));
                }
                break;
            case Scenario.RecursiveParent:
                await store.RegisterWorkflowVersionAsync(Row() with { Dependencies = [new(name, 1)] });
                var recursive = Instance(name);
                await store.SeedAsync(recursive);
                await Refused(() => catalog.RetireWorkflowVersionAsync(name, 1), WorkflowErrorKind.InvalidState);
                recursive.Status = WorkflowStatus.Completed;
                await store.SeedAsync(recursive);
                await catalog.RetireWorkflowVersionAsync(name, 1);
                break;
            case Scenario.Startup:
                var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
                builder.Services.AddLogging();
                builder.Services.AddSingleton(store);
                builder.Services.AddIxIFlow();
                builder.Services.RegisterWorkflow<StartupWorkflow>();
                using (var host = builder.Build())
                {
                    await host.StartAsync();
                    var startup = (await store.GetWorkflowVersionAsync("Catalog startup", 7))!;
                    Assert.Equal(WorkflowVersionState.Published, startup.State);
                    Assert.Equal(new WorkflowScopeCatalog(Definition("Catalog startup", 7)).StructureHash, startup.StructureHash);
                    var capabilities = Assert.Single(await store.GetLiveMembersAsync()).Capabilities;
                    Assert.Equal(startup.StructureHash, capabilities["workflow:Catalog startup@7"]);
                    await host.StopAsync();
                }
                break;
            case Scenario.DeprecatedChild:
            case Scenario.RetiredRecovery:
                var registrations = new ServiceCollection().AddLogging();
                registrations.AddSingleton(store);
                var childDefinition = scenario == Scenario.DeprecatedChild ? Definition(name)
                    : Workflow.Create<Data>(name).Step<Noop>().WaitFor<Answer>("answer").Build();
                registrations.AddSingleton(childDefinition);
                registrations.AddIxIFlow();
                await using (var provider = registrations.BuildServiceProvider())
                {
                    var registered = provider.GetRequiredService<IWorkflowCatalog>();
                    var client = provider.GetRequiredService<IWorkflowClient>();
                    if (scenario == Scenario.DeprecatedChild)
                    {
                        await registered.SetWorkflowVersionStateAsync(name, 1, WorkflowVersionState.Deprecated);
                        var parentDefinition = Workflow.Create<Data>(name + "-parent").Step<Noop>().Invoke<Data>(name, 1).Build();
                        var result = await client.RunAsync(parentDefinition, new Data());
                        var finished = await client.AwaitAsync(result.InstanceId, WorkflowAwaitCondition.Finished);
                        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
                        Assert.Single(await store.GetWorkflowInstancesByNameAsync(name));
                    }
                    else
                    {
                        var result = await client.RunAsync(childDefinition, new Data());
                        Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
                        await registered.RetireWorkflowVersionAsync(name, 1);
                        var resumed = await client.ResumeAsync(result.InstanceId, "answer", new Answer());
                        Assert.Equal(WorkflowExecutionStatus.Success, resumed.Status);
                        await Refused(() => client.StartAsync(name, 1, new Data()), WorkflowErrorKind.ArtifactUnavailable);
                    }
                }
                break;
            case Scenario.ConcurrentChild:
            case Scenario.ConcurrentRoot:
                await store.RegisterWorkflowVersionAsync(Row());
                await ConcurrentStartAsync(store, name, scenario == Scenario.ConcurrentChild);
                break;
            case Scenario.Artifacts:
                await ArtifactContractAsync(store, store, age, name);
                break;
        }
    }

    [Fact]
    public async Task EngineStartRechecksRetirementAtTheCommit()
    {
        var store = new PausedStartStore();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IWorkflowStateRepository>(store);
        services.AddSingleton(Definition("Retirement race"));
        services.AddIxIFlow();
        await using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<IWorkflowCatalog>();
        var start = provider.GetRequiredService<IWorkflowClient>().StartAsync("Retirement race", 1, new Data());
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { await catalog.RetireWorkflowVersionAsync("Retirement race", 1); }
        finally { store.Release.TrySetResult(); }
        await Refused(async () => await start, WorkflowErrorKind.ArtifactUnavailable);
        Assert.Empty(await store.GetWorkflowInstancesByNameAsync("Retirement race"));
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task RetentionServiceRunsTheArtifactPass(bool files)
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.ArtifactService", Guid.NewGuid().ToString("N"));
        try
        {
            var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
            var store = new InMemoryWorkflowStateRepository(time);
            IArtifactStore artifacts = files ? new FileSystemArtifactStore(folder) : store;
            var digest = await artifacts.PutAsync(ArtifactKind.Bundle, new MemoryStream([1]));
            time.Advance(TimeSpan.FromDays(91));
            using var service = new WorkflowRetentionService(store, RetentionSettings.Default, time,
                NullLogger<WorkflowRetentionService>.Instance, artifacts);
            await service.PurgeOnceAsync(CancellationToken.None);
            Assert.False(await artifacts.ExistsAsync(digest));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }

    private sealed class PausedStartStore : FaultInjectingRepository
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return await base.CommitWorkflowInstanceAsync(commit);
        }
    }

    [Fact]
    public async Task SqliteCatalogAndArtifactsSurviveReopen()
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.CatalogReopen", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var cs = "Data Source=" + Path.Combine(folder, "catalog.db");
        try
        {
            ArtifactDigest digest;
            WorkflowDraft draft;
            await using (var first = new SqliteWorkflowStateRepository(cs))
            {
                var catalog = new WorkflowCatalog(first);
                await catalog.RegisterWorkflowAsync(Definition("Persistent catalog", 9));
                await catalog.SetWorkflowVersionStateAsync("Persistent catalog", 9, WorkflowVersionState.Deprecated);
                draft = await catalog.SaveDefinitionAsync(new("Draft", 2, "workflow", "yaml"), 0);
                digest = await first.PutAsync(ArtifactKind.Template, new MemoryStream([42]));
            }
            await using var second = new SqliteWorkflowStateRepository(cs);
            var reopened = new WorkflowCatalog(second);
            Assert.Equal(draft, await reopened.GetDefinitionAsync("Draft", 2));
            Assert.Equal(WorkflowVersionState.Deprecated, (await reopened.GetWorkflowVersionAsync("Persistent catalog", 9))!.State);
            Assert.Null(await reopened.GetWorkflowDefinitionAsync("Persistent catalog", 9));
            await reopened.RegisterWorkflowAsync(Definition("Persistent catalog", 9));
            Assert.Equal(WorkflowVersionState.Deprecated, (await reopened.GetWorkflowVersionAsync("Persistent catalog", 9))!.State);
            Assert.True(await second.ExistsAsync(digest));
            await using var bytes = await second.OpenAsync(digest);
            Assert.Equal(42, bytes!.ReadByte());
        }
        finally { await SqliteTestFiles.DeleteAsync(folder); }
    }

    [Fact]
    public async Task FileSystemContract()
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.ArtifactContract", Guid.NewGuid().ToString("N"));
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        try
        {
            var artifacts = new FileSystemArtifactStore(folder);
            var store = new InMemoryWorkflowStateRepository(time);
            await ArtifactContractAsync(store, artifacts, () =>
            {
                foreach (var path in Directory.EnumerateFiles(folder, "*.artifact"))
                    File.SetLastWriteTimeUtc(path, time.GetUtcNow().AddDays(-91).UtcDateTime);
                return Task.CompletedTask;
            }, "File retention");
            Assert.Empty(Directory.EnumerateFiles(folder, "*.tmp"));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }

    private static async Task ArtifactContractAsync(IWorkflowStateRepository store, IArtifactStore artifacts, Func<Task> age, string name)
    {
        var bytes = Encoding.UTF8.GetBytes("abc");
        var digest = await artifacts.PutAsync(ArtifactKind.Bundle, new MemoryStream(bytes));
        Assert.Equal("sha256:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", digest.Value);
        Assert.Equal(digest, await artifacts.PutAsync(ArtifactKind.Package, new MemoryStream(bytes)));
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
            await artifacts.PutAsync(ArtifactKind.Bundle, new MemoryStream(bytes))));
        Assert.All(concurrent, value => Assert.Equal(digest, value));
        Assert.True(await artifacts.ExistsAsync(digest));
        await using (var opened = await artifacts.OpenAsync(digest))
        {
            Assert.NotNull(opened);
            using var copy = new MemoryStream();
            await opened.CopyToAsync(copy);
            Assert.Equal(bytes, copy.ToArray());
        }
        await artifacts.DeleteAsync(digest);
        await artifacts.DeleteAsync(digest);
        Assert.False(await artifacts.ExistsAsync(digest));
        Assert.Null(await artifacts.OpenAsync(digest));
        var keep = await artifacts.PutAsync(ArtifactKind.Bundle, new MemoryStream([1, 2, 3]));
        var indirect = await artifacts.PutAsync(ArtifactKind.Template, new MemoryStream([4, 5, 6]));
        var orphan = await artifacts.PutAsync(ArtifactKind.Package, new MemoryStream([7, 8, 9]));
        await store.RegisterWorkflowVersionAsync(new(name, 1, "artifacts", ArtifactDigest: keep, ArtifactReferences: [indirect]));
        await store.SetWorkflowVersionStateAsync(name, 1, WorkflowVersionState.Retired);
        await age();
        Assert.Equal(1, await store.PurgeArtifactsAsync(artifacts, TimeSpan.FromDays(90), 1));
        Assert.True(await artifacts.ExistsAsync(keep));
        Assert.True(await artifacts.ExistsAsync(indirect));
        Assert.False(await artifacts.ExistsAsync(orphan));
        var fresh = await artifacts.PutAsync(ArtifactKind.Bundle, new MemoryStream([10]));
        Assert.Equal(0, await store.PurgeArtifactsAsync(artifacts, TimeSpan.FromDays(90), 10));
        Assert.True(await artifacts.ExistsAsync(fresh));
    }

    private static async Task ConcurrentStartAsync(IWorkflowStateRepository store, string name, bool child)
    {
        var instance = Instance(child ? name + "-parent" : name);
        if (child) await store.SeedAsync(instance);
        var lease = await StoreTestSupport.LeaseAsync(store, instance.InstanceId);
        var started = Instance(name);
        if (child)
        {
            started.ParentInstanceId = instance.InstanceId;
            started.ParentWaitId = "child-wait";
            started.Status = WorkflowStatus.Running;
        }
        var commit = CheckpointCommit.Create(instance, lease.Epoch, instance.Revision,
            childStarts: child ? [new(started.InstanceId, JsonSerializer.Serialize(started), "child-wait")] : [],
            openedWaits: child ? [new("child-wait", WaitKind.Child, "child", ChildCompleted.EventType, null)] : []);
        var blocker = new RetentionBarrier();
        var holding = Task.Run(() => store.PurgeArtifactsAsync(blocker, TimeSpan.FromDays(90), 1));
        await blocker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var retiring = Task.Run(() => store.SetWorkflowVersionStateAsync(name, 1, WorkflowVersionState.Retired));
        var starting = Task.Run(async () =>
        {
            try { return (Error: (WorkflowEngineException?)null, Result: await store.CommitWorkflowInstanceAsync(commit)); }
            catch (WorkflowEngineException error) { return (Error: error, Result: (WorkflowCommitResult?)null); }
        });
        try
        {
            await Task.Delay(100);
            Assert.False(retiring.IsCompleted);
            Assert.False(starting.IsCompleted);
        }
        finally { blocker.Release.TrySetResult(); }
        await holding;
        await retiring;
        var outcome = await starting;
        if (outcome.Error != null)
        {
            Assert.Equal(WorkflowErrorKind.ArtifactUnavailable, outcome.Error.Kind);
            Assert.Null(await store.GetWorkflowInstanceAsync(child ? started.InstanceId : instance.InstanceId));
        }
        else Assert.Equal(WorkflowCommitStatus.Applied, outcome.Result!.Status);
        // A start prepared before retirement must be refused once retirement has committed.
        var late = Instance(name);
        await Refused(() => store.SeedAsync(late), WorkflowErrorKind.ArtifactUnavailable);
    }

    private sealed class RetentionBarrier : IArtifactStore, IArtifactRetentionStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime olderThanUtc,
            int batchSize, CancellationToken cancellationToken = default)
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

    private static async Task Refused(Func<Task> action, WorkflowErrorKind kind) =>
        Assert.Equal(kind, (await Assert.ThrowsAsync<WorkflowEngineException>(action)).Kind);

    private static WorkflowInstance Instance(string name) => new()
    {
        InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = name, WorkflowVersion = 1,
        Status = WorkflowStatus.Suspended, ExecutionStateJson = StoreTestSupport.CheckpointJson,
        WorkflowDataJson = "{}", WorkflowDataType = typeof(Data).AssemblyQualifiedName!, CorrelationId = "catalog"
    };

    private static WorkflowDefinition Definition(string name, int version = 1) => Workflow.Create<Data>(name, version).Step<Noop>().Build();
    public sealed class Data;
    public sealed class Answer;
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    [IxIFlow.Core.Workflow("Catalog startup", Version = 7)]
    public sealed class StartupWorkflow : IWorkflow<Data>
    {
        public void Build(IWorkflowBuilder<Data> builder) => builder.Step<Noop>();
    }
}

