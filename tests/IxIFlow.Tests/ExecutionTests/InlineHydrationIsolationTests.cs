using System.Data.Common;
using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Spec", "D-18")]
[Trait("Spec", "F-17")]
public sealed class InlineHydrationIsolationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SteppingAnotherBranchDoesNotReadHeldInlineData(bool unavailable)
    {
        var store = new ProbedRepository();
        var probe = new Probe();
        var collection = new ServiceCollection().AddLogging();
        collection.AddIxIFlow(options =>
        {
            options.Debugging = true;
            options.PayloadThresholdBytes = 128;
            options.VerifyCheckpointParts = true;
        });
        collection.AddSingleton<IWorkflowStateRepository>(store);
        collection.AddSingleton(probe);
        await using var services = collection.BuildServiceProvider();
        var client = services.GetRequiredService<IWorkflowClient>();
        var operations = services.GetRequiredService<IWorkflowOperations>();
        var definition = Workflow.Create<Data>("InlineHydrationIsolation").Step<Noop>().WaitFor<Signal>("start")
            .Parallel(branches => branches
                .Do(body => body.Use<HeldBody, FrameData>())
                .Do(body => body.Step<Count>().WithId("stepped").WaitFor<Signal>("other")))
            .Build();
        var idle = await client.RunAsync(definition, new Data());
        await operations.SetBreakpointsAsync(idle.InstanceId, [new("step", "stepped")], "set");
        var paused = await client.ResumeAsync(idle.InstanceId, "start", new Signal());
        Assert.Equal(WorkflowExecutionStatus.Paused, paused.Status);
        var inspection = (await operations.InspectAsync(idle.InstanceId))!;
        var thread = Assert.Single(inspection.Threads, item => item.AuthorStepId == "stepped");
        var saved = (await store.GetWorkflowInstanceAsync(idle.InstanceId))!;
        var framePart = Assert.Single(saved.SavedParts!, part => part.Key.StartsWith("i/", StringComparison.Ordinal));
        var reference = Assert.IsType<PayloadReference>(framePart.Value.Reference);
        store.Reads.Target = reference;
        store.Reads.Unavailable = unavailable;

        var stepped = await operations.StepAsync(idle.InstanceId, thread.ContinuationId, DebugStepKind.Over, "step");

        Assert.Equal(WorkflowExecutionStatus.Paused, stepped.Status);
        Assert.Equal(1, probe.Runs);
        Assert.Equal(0, store.Reads.TargetReads);
        var after = (await store.GetWorkflowInstanceAsync(idle.InstanceId))!;
        Assert.Equal(reference, after.SavedParts![framePart.Key].Reference);

        // Reading the held branch later still uses the on-demand path.
        store.Reads.Unavailable = false;
        await operations.SetBreakpointsAsync(idle.InstanceId, [], "clear");
        await operations.ContinueAsync(idle.InstanceId, "continue");
        var resumed = await client.ResumeAsync(idle.InstanceId, "held", new Signal());
        Assert.Equal(WorkflowExecutionStatus.Suspended, resumed.Status);
        Assert.True(store.Reads.TargetReads > 0);
        Assert.Equal(2, probe.Runs);
    }

    public sealed class Data { }
    public sealed class Signal { }
    public sealed class FrameData { public string Text { get; set; } = new('x', 4096); }
    public sealed class Probe { public int Runs; }
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public sealed class Count(Probe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.Runs++;
            return Task.CompletedTask;
        }
    }
    [Workflow("HydrationHeldBody")]
    public sealed class HeldBody : IWorkflow<FrameData>
    {
        public void Build(IWorkflowBuilder<FrameData> builder) => builder.Step<Noop>().WaitFor<Signal>("held").Step<Count>();
    }

    private sealed class ProbedRepository : InMemoryWorkflowStateRepository
    {
        private ReadProbe? _reads;
        public ReadProbe Reads => _reads ??= new(base.PayloadStore);
        public override IPayloadStore PayloadStore => Reads;
    }

    private sealed class ReadProbe(IPayloadStore inner) : IPayloadStore
    {
        public PayloadReference? Target;
        public bool Unavailable;
        public int TargetReads;
        public ValueTask<ReadOnlyMemory<byte>?> GetAsync(PayloadReference reference, CancellationToken ct = default)
        {
            if (reference == Target)
            {
                TargetReads++;
                if (Unavailable) return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);
            }
            return inner.GetAsync(reference, ct);
        }
        public ValueTask<PayloadReference> PutAsync(ReadOnlyMemory<byte> body, IReadOnlyCollection<PayloadReference>? dependencies = null, CancellationToken ct = default) => inner.PutAsync(body, dependencies, ct);
        public ValueTask<bool> CollectAsync(PayloadReference reference, TimeSpan grace, CancellationToken ct = default) => inner.CollectAsync(reference, grace, ct);
        public ValueTask<IReadOnlyList<PayloadReference>> FindCollectionCandidatesAsync(int limit, TimeSpan grace, CancellationToken ct = default) => inner.FindCollectionCandidatesAsync(limit, grace, ct);
        public ValueTask InTransactionAsync(Func<IPayloadTransaction, CancellationToken, ValueTask> action, CancellationToken ct = default) => inner.InTransactionAsync(action, ct);
        public IPayloadTransaction Enlist(DbTransaction transaction) => inner.Enlist(transaction);
    }
}
