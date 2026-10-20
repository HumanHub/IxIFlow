using System.Reflection;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "host")]
[Trait("Spec", "A-02")]
[Trait("Spec", "A-03")]
public sealed class SerializedHostAdmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealHostRefusesASerializedCheckpointFromAnEarlierEpochOrAssignmentWithoutStoreCalls(bool earlierAssignment)
    {
        var time = new FakeTimeProvider();
        var store = DispatchProxy.Create<IWorkflowStateRepository, StoreProbe>();
        var probe = (StoreProbe)store;
        probe.Inner = new InMemoryWorkflowStateRepository(time);
        using var services = new ServiceCollection().AddLogging().AddIxIFlow().Services.BuildServiceProvider();
        var definition = Workflow.Create<Data>("admission").Step<Noop>().Build();
        var registry = services.GetRequiredService<IWorkflowCatalog>();
        await registry.RegisterWorkflowAsync(definition);
        var (instance, checkpoint, _) = WorkflowInstances.NewInstance(definition, new Data(), new(), time.GetUtcNow().UtcDateTime);
        instance.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
        await store.SeedAsync(instance);
        instance = (await store.GetWorkflowInstanceAsync(instance.InstanceId))!;
        // The watch reads the unwrapped store, so its background reads cannot change the probe's count.
        await using var renewer = new ExecutionLeaseRenewer(store, time);
        await using var watch = new CancellationWatch(probe.Inner, time);
        await using var owner = (await WorkflowExecutionLease.TryAcquireAsync(store, instance,
            new(TimeSpan.FromHours(1), TimeSpan.FromMinutes(30)), watch, renewer, await StoreTestSupport.ClaimAsync(services), _ => { }, _ => { }, _ => { }, time))!;
        var operations = new WorkflowHost.InstanceOperations(services, services.GetRequiredService<IActivityExecutor>(),
            store, registry, new InProcessInstanceGate(), renewer, timeProvider: time);
        var identity = Guid.NewGuid();
        using var admission = operations.Admit(instance, owner, identity);
        var host = new WorkflowHost.InstanceOperations.ExecutionHost(operations);
        var commit = CheckpointCommit.Create(instance, earlierAssignment ? owner.Epoch : owner.Epoch + 1, instance.Revision);
        var request = new Checkpoint(earlierAssignment ? Guid.NewGuid() : identity, commit);
        request = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(request,
            ExecutionMessageContext.Default.Checkpoint), ExecutionMessageContext.Default.Checkpoint)!;
        var before = probe.Calls;
        var reply = await host.CheckpointAsync(request);
        reply = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(reply,
            ExecutionMessageContext.Default.CheckpointReply), ExecutionMessageContext.Default.CheckpointReply)!;
        Assert.Equal(CheckpointReplyKind.Conflict, reply.Kind);
        Assert.Equal(ConflictReason.NotAssigned, reply.Conflict);
        Assert.Equal(before, probe.Calls);
        Assert.Throws<WorkflowEngineException>(() => operations.AdmitReady(new(identity,
            new(AssignmentOutcomeKind.Idle), null, ExecutionContract.Version + 1)));
    }

    public class StoreProbe : DispatchProxy
    {
        public IWorkflowStateRepository Inner = null!;
        public int Calls;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Interlocked.Increment(ref Calls);
            try { return targetMethod!.Invoke(Inner, args); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
    }
    public sealed class Data;
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
