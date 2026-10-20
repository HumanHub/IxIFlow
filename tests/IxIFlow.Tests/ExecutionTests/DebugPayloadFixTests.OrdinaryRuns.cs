using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

public sealed partial class DebugPayloadFixTests
{
    [Fact]
    public void CachedCommitMetadataStillReturnsDetachedSnapshots()
    {
        var value = System.Text.Json.JsonSerializer.SerializeToElement(7);
        var instance = new WorkflowInstance
        {
            InstanceId = "cached-trunk", WorkflowName = "cached-trunk", Status = WorkflowStatus.Running,
            WorkflowDataJson = StoredJson.Inline("{}"), ExecutionStateJson = StoreTestSupport.CheckpointJson,
            Properties = new() { ["value"] = value },
            CancellationReason = new() { ReasonCode = "saved", Metadata = new() { ["value"] = value } }
        };
        var commit = CheckpointCommit.Create(instance, 1, 0);
        var hash = commit.ContentHash;
        instance.Properties.Clear();
        instance.CancellationReason.ReasonCode = "changed";
        var first = commit.ReadTrunk();
        first.Status = WorkflowStatus.Failed;
        first.ExecutionStateJson = "{}";
        first.Properties.Clear();
        first.CancellationReason!.Metadata.Clear();
        first.CancellationReason.ReasonCode = "changed again";
        var second = commit.ReadTrunk();
        Assert.Equal(WorkflowStatus.Running, second.Status);
        Assert.NotEqual("{}", second.ExecutionStateJson);
        Assert.Equal(7, second.Properties["value"].GetInt32());
        Assert.Equal("saved", second.CancellationReason!.ReasonCode);
        Assert.Equal(7, second.CancellationReason.Metadata["value"].GetInt32());
        Assert.Equal(hash, commit.ContentHash);
    }

    [Fact]
    public async Task AnUncontendedMemoryCheckpointDoesNotScheduleAContinuation()
    {
        var store = new InMemoryWorkflowStateRepository();
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddSingleton<IWorkflowStateRepository>(store);
        registrations.AddIxIFlow();
        await using var services = registrations.BuildServiceProvider();
        var catalog = services.GetRequiredService<IWorkflowCatalog>();
        var definition = Workflow.Create<Data>("inline-checkpoint").Step<Noop>().Build();
        await catalog.RegisterWorkflowAsync(definition);
        var (initial, checkpoint, _) = WorkflowInstances.NewInstance(definition, new Data(), new(), DateTime.UtcNow);
        initial.ExecutionStateJson = System.Text.Json.JsonSerializer.Serialize(checkpoint);
        await store.SeedAsync(initial);
        var instance = (await store.GetWorkflowInstanceAsync(initial.InstanceId))!;
        await using var watch = new CancellationWatch(store);
        await using var owner = (await WorkflowExecutionLease.TryAcquireAsync(store, instance,
            ExecutionLeaseSettings.Default, watch, _ => { }, _ => { }, _ => { }))!;
        var operations = new WorkflowHost.InstanceOperations(services, services.GetRequiredService<IActivityExecutor>(),
            store, catalog, new InProcessInstanceGate(), watch: watch);
        var identity = Guid.NewGuid();
        using var admission = operations.Admit(instance, owner, identity);
        var endpoint = new WorkflowHost.InstanceOperations.ExecutionHost(operations);
        var context = new PostingContext();
        var previous = SynchronizationContext.Current;
        Task<CheckpointReply> reply;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            reply = endpoint.CheckpointAsync(new(identity, CheckpointCommit.Create(instance, owner.Epoch, instance.Revision)));
            Assert.True(reply.IsCompletedSuccessfully);
            Assert.Equal(0, context.Posts);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        Assert.Equal(CheckpointReplyKind.Ack, (await reply).Kind);
    }

    [Fact]
    public async Task OrdinaryExecutionAndResumeDoNotAccessPayloads()
    {
        var store = new OrdinaryStore();
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddSingleton<IWorkflowStateRepository>(store);
        registrations.AddIxIFlow();
        registrations.AddTransient<Noop>();
        await using var services = registrations.BuildServiceProvider();
        var client = services.GetRequiredService<IWorkflowClient>();
        var started = await client.RunAsync(Workflow.Create<Data>("ordinary-path")
            .Step<Noop>().WaitFor<Go>("go").Step<Noop>().Build(), new Data { Text = "inline data" });
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        var resumed = await client.ResumeAsync(started.InstanceId, "go", new Go());
        Assert.Equal(WorkflowExecutionStatus.Success, resumed.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TurningClaimCheckOffStillReleasesPreviouslyReferencedData(bool sqlite)
    {
        await using var h = new Harness(sqlite);
        var definition = Workflow.Create<Data>("claim-check-off")
            .Step<Noop>().WaitFor<Go>("go").Step<ClearText>().Build();
        var started = await h.Client.RunAsync(definition, new Data { Text = Large });
        var reference = (await h.Store.GetWorkflowInstanceAsync(started.InstanceId))!.WorkflowDataJson.Reference!;
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddSingleton(h.Store);
        registrations.AddSingleton<TimeProvider>(h.Clock);
        registrations.AddIxIFlow();
        registrations.AddTransient<ClearText>();
        await using var services = registrations.BuildServiceProvider();
        await services.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(definition);
        var resumed = await services.GetRequiredService<IWorkflowClient>().ResumeAsync(started.InstanceId, "go", new Go());
        Assert.Equal(WorkflowExecutionStatus.Success, resumed.Status);
        Assert.Equal("", Assert.IsType<Data>(resumed.WorkflowData).Text);
        Assert.True(await h.Store.PayloadStore.CollectAsync(reference, TimeSpan.Zero));
    }

    private sealed class OrdinaryStore : InMemoryWorkflowStateRepository
    {
        public override IPayloadStore PayloadStore => throw new InvalidOperationException("Ordinary execution accessed payload storage");
    }

    private sealed class PostingContext : SynchronizationContext
    {
        private int _posts;
        public int Posts => Volatile.Read(ref _posts);
        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref _posts);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
    }

    public sealed class ClearText : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { ((Data)context.WorkflowData).Text = ""; return Task.CompletedTask; }
    }
}
