using System.Text;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
public sealed partial class DebugPayloadFixTests
{
    private static readonly string Large = new('x', 300_000);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task C_QuarantineKeepsReferencedBranchesAndForEachResults(bool sqlite)
    {
        await using var h = new Harness(sqlite);
        var definition = Workflow.Create<Data>("quarantine-parts").Step<Noop>()
            .ForEach(c => c.WorkflowData.Items, (body, item) => body.Step<Noop>()
                .WaitFor<Go>("item").Result(c => c.Item(item)), s => s.Parallel(1).Into(c => c.WorkflowData.Results)).Build();
        var first = await h.Client.RunAsync(definition, new Data { Items = [Large, Large + "2"] });
        await h.Client.ResumeAsync(first.InstanceId, "item", new Go(), "first");
        var saved = (await h.Store.GetWorkflowInstanceAsync(first.InstanceId))!;
        Assert.Contains(saved.SavedParts!, p => p.Key.StartsWith("c/") && p.Value.Reference != null);
        Assert.Contains(saved.SavedParts!, p => p.Key.StartsWith("r/") && p.Value.Reference != null);
        var (interrupted, lease) = await h.RecreateForClaimAsync(first.InstanceId, "crash", TimeSpan.FromSeconds(1));
        first.InstanceId = interrupted.InstanceId;
        saved = interrupted;
        saved.Status = WorkflowStatus.Running;
        Assert.Equal(WorkflowCommitStatus.Applied, (await h.Store.CommitWorkflowInstanceAsync(
            CheckpointCommit.FromFields(CommittedInstance.Of(saved), JsonSerializer.Serialize(saved), [], [], false,
                saved.Incarnation, lease.Epoch, Guid.NewGuid().ToString("N"), saved.Revision, true, false,
                [], null, [], [], [], null, false, [], [], null, [], [], null,
                CheckpointPayloads.References(saved.ExecutionStateJson, saved.SavedParts!)))).Status);
        var before = (await h.Store.GetWorkflowInstanceAsync(first.InstanceId))!;
        h.Clock.Advance(TimeSpan.FromSeconds(3));
        var stopped = await h.Client.RecoverAsync(first.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, stopped.Status);
        var after = (await h.Store.GetWorkflowInstanceAsync(first.InstanceId))!;
        Assert.Equal(before.SavedParts, after.SavedParts);
        foreach (var reference in before.SavedParts!.Values.Where(v => v.Reference != null).Select(v => v.Reference!))
            Assert.False(await h.Store.PayloadStore.CollectAsync(reference, TimeSpan.Zero));
        Assert.Equal(WorkflowExecutionStatus.Suspended, (await h.Client.ReleaseAsync(first.InstanceId)).Status);
        var completed = await h.Client.ResumeAsync(first.InstanceId, "item", new Go(), "second");
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal(new[] { Large, Large + "2" }, Assert.IsType<Data>(completed.WorkflowData).Results.Select(r => r.Value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task D_SecondRecoveryReactivatesAnUnresolvedReferencedBranch(bool sqlite)
    {
        FaultInjectingRepository faults = null!;
        await using var h = new Harness(sqlite, s => faults = new(s));
        faults.FailCommit = _ => h.Probe.Executions > 0;
        var id = Guid.NewGuid().ToString("N");
        var definition = Workflow.Create<Data>("recover-branch").Step<Noop>()
            .Parallel(p => p.Do(b => b.Step<Recoverable>()).Do(b => b.Step<Noop>())).Build();
        await Assert.ThrowsAnyAsync<Exception>(() => h.Client.RunAsync(definition, new Data(), new() { InstanceId = id }));
        faults.FailCommit = _ => false;
        Assert.Contains((await h.Store.GetWorkflowInstanceAsync(id))!.SavedParts!, p => p.Key.StartsWith("c/") && p.Value.Reference != null);
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, (await h.Client.RecoverAsync(id)).Status);
        Assert.Equal(1, h.Probe.Recoveries);
        h.Probe.Resolve = true;
        Assert.Equal(WorkflowExecutionStatus.Success, (await h.Client.RecoverAsync(id)).Status);
        Assert.Equal(2, h.Probe.Recoveries);
        Assert.Equal(1, h.Probe.Executions);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task A_DeletionReleasesOnlyTheDeletedIncarnationsCheckpoint(bool sqlite, bool retention)
    {
        await using var h = new Harness(sqlite);
        var definition = Workflow.Create<Data>("delete-pins").Step<Noop>().Build();
        var first = await h.Client.RunAsync(definition, new Data { Text = Large });
        var second = await h.Client.RunAsync(definition, new Data { Text = Large });
        var reference = (await h.Store.GetWorkflowInstanceAsync(first.InstanceId))!.WorkflowDataJson.Reference!;
        if (retention)
        {
            h.Clock.Advance(TimeSpan.FromDays(2));
            Assert.Equal(1, (await h.Store.PurgeAsync(TimeSpan.FromDays(1), TimeSpan.FromDays(1), 1)).InstancesRemoved);
        }
        else await h.Store.DeleteWorkflowInstanceAsync(first.InstanceId);
        Assert.False(await h.Store.PayloadStore.CollectAsync(reference, TimeSpan.Zero));
        // Remove the other owner, whichever instance the bounded retention pass selected first.
        foreach (var id in new[] { first.InstanceId, second.InstanceId }) await h.Store.DeleteWorkflowInstanceAsync(id);
        Assert.True(await h.Store.PayloadStore.CollectAsync(reference, TimeSpan.Zero));
        Assert.Null(await h.Store.PayloadStore.GetAsync(reference));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_RetentionServiceCollectsOrphanBodiesInBoundedPasses(bool sqlite)
    {
        await using var h = new Harness(sqlite);
        var reference = await h.Store.PayloadStore.PutAsync(Encoding.UTF8.GetBytes("\"orphan\""));
        h.Clock.Advance(TimeSpan.FromDays(100));
        var service = new WorkflowRetentionService(h.Store, RetentionSettings.Default, h.Clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkflowRetentionService>.Instance);
        await service.PurgeOnceAsync(default);
        Assert.Null(await h.Store.PayloadStore.GetAsync(reference));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task E_RetiredDigestIsRepublishedAfterCollectionInTheSameAssignment(bool sqlite)
    {
        await using var h = new Harness(sqlite);
        var definition = Workflow.Create<Data>("reuse-collected").Step<ReplaceData>()
            .Step<CollectOriginal>().Step<RestoreOriginal>().Build();
        var completed = await h.Client.RunAsync(definition, new Data { Text = Large });
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal(Large, Assert.IsType<Data>(completed.WorkflowData).Text);
    }

    public sealed class ReplaceData : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { ((Data)context.WorkflowData).Text = "B"; return Task.CompletedTask; }
    }
    public sealed class CollectOriginal(IWorkflowStateRepository store) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            var original = PayloadReference.Of(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Data { Text = Large })));
            Assert.True(await store.PayloadStore.CollectAsync(original, TimeSpan.Zero));
        }
    }
    public sealed class RestoreOriginal : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { ((Data)context.WorkflowData).Text = Large; return Task.CompletedTask; }
    }

    public sealed class RecoveryProbe { public int Executions; public int Recoveries; public bool Resolve; }
    public sealed class Recoverable(RecoveryProbe probe) : IRecoverableActivity<string>
    {
        public string CaptureRecoveryState(IActivityContext context) => Large;
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { probe.Executions++; return Task.CompletedTask; }
        public Task<ActivityRecoveryResult<string>> RecoverAsync(string state, IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.Recoveries++;
            return Task.FromResult<ActivityRecoveryResult<string>>(probe.Resolve
                ? new ActivityRecoveryResult<string>.Completed() : new ActivityRecoveryResult<string>.Unresolved("try again"));
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "ixiflow-dbg-fix-" + Guid.NewGuid().ToString("N"));
        private readonly SqliteWorkflowStateRepository? _sqlite;
        public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);
        public IWorkflowStateRepository Store { get; }
        public ServiceProvider Services { get; }
        public string ConnectionString => "Data Source=" + Path.Combine(_folder, "state.db");
        public IWorkflowClient Client => Services.GetRequiredService<IWorkflowClient>();
        public RecoveryProbe Probe { get; } = new();
        public Harness(bool sqlite, Func<IWorkflowStateRepository, IWorkflowStateRepository>? decorate = null,
            Action<IServiceCollection>? configureServices = null)
        {
            Directory.CreateDirectory(_folder);
            _sqlite = sqlite ? new("Data Source=" + Path.Combine(_folder, "state.db"), Clock) : null;
            Store = _sqlite is null ? new InMemoryWorkflowStateRepository(Clock) : _sqlite;
            Store = decorate?.Invoke(Store) ?? Store;
            var services = new ServiceCollection().AddLogging();
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton(Store);
            services.AddSingleton(Probe);
            services.AddIxIFlow(o => { o.PayloadThresholdBytes = 64; o.MaxCrashes = 1; o.Debugging = true; });
            configureServices?.Invoke(services);
            Services = services.BuildServiceProvider();
        }
        // Idle rows cannot be claimed just to fabricate a crash. Recreate their saved trunk,
        // parts and open waits under a new ID, then claim through the normal placement contract.
        public async Task<(WorkflowInstance Instance, ExecutionLeaseGrant Lease)> RecreateForClaimAsync(
            string instanceId, string token, TimeSpan duration, bool suspended = false)
        {
            var saved = (await Store.GetWorkflowInstanceAsync(instanceId))!;
            var waits = await Store.GetOpenWaitsAsync(new(InstanceId: instanceId));
            var parts = saved.SavedParts!;
            saved.InstanceId = Guid.NewGuid().ToString("N");
            saved.Incarnation = Guid.NewGuid();
            saved.Revision = 0;
            saved.Status = suspended ? WorkflowStatus.Suspended : WorkflowStatus.Running;
            var claim = await StoreTestSupport.ClaimAsync(Services);
            var lease = (await Store.TryAcquireExecutionLeaseAsync(saved.InstanceId, token, duration, claim))!;
            var opened = waits.Select(wait => new OpenedWait(wait.WaitId, wait.Kind, wait.Key, wait.EventType,
                wait.DeadlineUtc - Clock.GetUtcNow().UtcDateTime, wait.DefaultPayloadJson)).ToArray();
            var commit = CheckpointCommit.FromFields(CommittedInstance.Of(saved),
                JsonSerializer.Serialize(saved.AsTrunk(saved.ExecutionStateJson)),
                parts.OrderBy(part => part.Key, StringComparer.Ordinal).Select(part => new CheckpointPart(part.Key, part.Value)).ToArray(),
                [], true, saved.Incarnation, lease.Epoch, Guid.NewGuid().ToString("N"), 0, true, false,
                [], null, opened, [], [], suspended ? TimeSpan.Zero : null, false, [], [], null, [], [], null,
                CheckpointPayloads.References(saved.ExecutionStateJson, parts));
            Assert.Equal(WorkflowCommitStatus.Applied, (await Store.CommitWorkflowInstanceAsync(commit)).Status);
            // New owners pin the same immutable bodies before the old fixture releases its owners.
            await Store.DeleteWorkflowInstanceAsync(instanceId);
            if (suspended)
                lease = (await Store.TryAcquireExecutionLeaseAsync(saved.InstanceId, token, duration, claim))!;
            return ((await Store.GetWorkflowInstanceAsync(saved.InstanceId))!, lease);
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            if (_sqlite != null) await _sqlite.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(_folder, true);
        }
    }

    public sealed class Data
    {
        public string Text { get; set; } = "";
        public List<string> Items { get; set; } = [];
        public List<ItemResult<string>> Results { get; set; } = [];
    }
    public sealed class Go;
    public sealed class Noop : IAsyncActivity
    { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask; }
}
