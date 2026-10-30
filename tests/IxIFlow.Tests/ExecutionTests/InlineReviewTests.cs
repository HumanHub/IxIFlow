using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "control-flow"), Trait("Spec", "F-17")]
public sealed class InlineReviewTests
{
    [Fact]
    public async Task ExhaustedBodyDiscardsUnserializableActivityBeforeExitAndRecoversThere()
    {
        var store = new ExitRepository();
        var catalog = new WorkflowCatalog();
        var probe = new Probe();

        var definition = Workflow.Create<Caller>("InlineDeadPrevious").Use<DelegateBody, Data>(u => u
            .Output(x => x.Value).To(c => c.WorkflowData.Value)).Build();

        var id = Guid.NewGuid().ToString("N");

        await using (var first = Services(store, catalog, probe))
            await Assert.ThrowsAnyAsync<Exception>(() => first.GetRequiredService<IWorkflowClient>()
                .RunAsync(definition, new Caller(), new() { InstanceId = id }));

        var saved = (await store.GetWorkflowInstanceAsync(id))!;
        var checkpoint = ExecutionCheckpoint.Read(StoredCheckpoint.WholeState(saved));
        var root = Assert.Single(checkpoint.Continuations);
        Assert.Equal(FrameKind.Inline, root.Stack[^1].Kind);
        Assert.Equal(1, root.Stack[^1].NextStepIndex);
        Assert.False(root.Stack[^1].RestorePreviousOnExit);
        Assert.Null(root.Previous);
        Assert.Equal(1, probe.Runs);
        store.Interrupt = false;
        await using var second = Services(store, catalog, probe);
        var result = await second.GetRequiredService<IWorkflowClient>().RecoverAsync(id);
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(42, Assert.IsType<Caller>(result.WorkflowData).Value);
        Assert.Equal(1, probe.Runs);
    }

    [Theory]
    [InlineData(false, false, false), InlineData(false, false, true)]
    [InlineData(false, true, false), InlineData(false, true, true)]
    [InlineData(true, false, false), InlineData(true, false, true)]
    [InlineData(true, true, false), InlineData(true, true, true)]
    public async Task ApplicationReferencesAreOpaqueInRootAndBranchFrames(bool claimCheck, bool branch, bool valid)
    {
        var digest = valid ? "sha256:" + new string('a', 64) : "business-id";
        var json = JsonSerializer.SerializeToElement(new { reference = new { digest, bytes = 12 } });
        var store = new InMemoryWorkflowStateRepository();
        var catalog = new WorkflowCatalog();
        var builder = Workflow.Create<Caller>("InlineOpaque").Step<Noop>();

        var definition = branch
            ? builder.Parallel(p => p.Do(b => b.Use<OpaqueBody, Data>(u => u
                .Input(x => x.Json).From(c => c.WorkflowData.Json)))).Build()
            : builder.Use<OpaqueBody, Data>(u => u.Input(x => x.Json).From(c => c.WorkflowData.Json)).Build();

        string id;

        await using (var first = Services(store, catalog, new(), claimCheck ? 4096 : 0))
        {
            var result = await first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Caller { Json = json });
            Assert.True(result.Status == WorkflowExecutionStatus.Suspended, result.ErrorMessage);
            id = result.InstanceId;
            var saved = (await store.GetWorkflowInstanceAsync(id))!;
            Assert.True(Assert.Single(saved.SavedParts!, p => p.Key.StartsWith("i/", StringComparison.Ordinal)).Value.IsInline);
            var references = CheckpointPayloads.References(saved.ExecutionStateJson, saved.SavedParts!);
            Assert.DoesNotContain(references, r => r.Digest == digest);

            // Remove in-memory dependency caches, as a cold store read would.
            var cold = saved.SavedParts!.ToDictionary(p => p.Key, p => p.Value.Reference is { } r
                ? StoredJson.FromReference(r)
                : StoredJson.Inline(p.Value.InlineText));

            Assert.Equal(references, CheckpointPayloads.References(saved.ExecutionStateJson, cold));
        }

        await using var second = Services(store, catalog, new(), claimCheck ? 4096 : 0);
        var completed = await second.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "opaque", new Signal());
        Assert.True(completed.Status == WorkflowExecutionStatus.Success, completed.ErrorMessage);
        Assert.Equal(digest, Assert.IsType<Caller>(completed.WorkflowData).Json.GetProperty("reference").GetProperty("digest").GetString());
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task StoredFrameSerializationDoesNotKeepCollectibleDataAlive(bool claimCheck)
    {
        var weak = SerializeCollectibleFrame(claimCheck);

        for (var i = 0; i < 150 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            _ = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
                .GetTypeInfo(typeof(Caller), new JsonSerializerOptions());

            await Task.Delay(30);
        }

        Assert.False(weak.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference SerializeCollectibleFrame(bool claimCheck)
    {
        var context = new AssemblyLoadContext("inline-frame-options", isCollectible: true);

        // Bundle unload releases its context profiles; static serializer options would still leak.
        context.Unloading += WorkflowJson.Release;
        using var stream = File.OpenRead(typeof(InlineCheckpointTests.Data).Assembly.Location);
        var type = context.LoadFromStream(stream).GetType(typeof(InlineCheckpointTests.Data).FullName!)!;
        var value = Activator.CreateInstance(type)!;

        var payloads = new CheckpointPayloads(ExecutionLimits.Default with { PayloadThresholdBytes = claimCheck ? 1 : 0 },
            new PayloadBudget(1_000_000));

        var checkpoint = new ExecutionCheckpoint();
        checkpoint.InlineFrames.Add("frame", new() { Runtime = value, DataType = "unused", Changed = true });
        checkpoint.Continuations.Add(new() { Stack = [new() { Kind = FrameKind.Inline, ActivationId = "frame" }] });
        checkpoint.RecordInlineFrames(payloads);
        _ = payloads.Serialize(value);
        Assert.Contains("Value", checkpoint.InlineFrames["frame"].Data.InlineText);
        var weak = new WeakReference(context);
        context.Unload();

        return weak;
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task NestedLiveDataCopiesAndReturnedPreviousStayIsolatedAcrossRestart(bool restart)
    {
        var store = new InMemoryWorkflowStateRepository();
        var catalog = new WorkflowCatalog();
        var probe = new Probe();

        WorkflowDefinition OuterBody() => Workflow.Create<Data>("LiveOuter").Step<MutateOuter>().Use<Inner, Data>(u => u
                .Input(x => x.Items).From(c => probe.CaptureOuter(c.WorkflowData.Items))
                .Output(x => x.Items).To(c => c.WorkflowData.Items))
            .Step<Noop>(a => a.Input(x => x.Value).From(c => probe.Check(c.PreviousStep, c.WorkflowData.Items))).Build();

        var body = OuterBody();

        var pin = new WorkflowInlinePin(body.Name, body.Version, WorkflowDefinitionValidator.Inspect(body),
            WorkflowDefinitionValidator.GetContract(body).Hash);

        var definition = Workflow.Create<Caller>("InlineLiveIsolation").Use<Data>(OuterBody, pin, u => u
                .Input(x => x.Items).From(c => probe.CaptureCaller(c.WorkflowData.Items))
                .Output(x => x.Items).To(c => c.WorkflowData.Results))
            .Step<Noop>(a => a.Input(x => x.Value).From(c => probe.Check(c.PreviousStep, c.WorkflowData.Results))).Build();

        string id;

        await using (var first = Services(store, catalog, probe))
        {
            var client = first.GetRequiredService<IWorkflowClient>();
            var result = await client.RunAsync(definition, new Caller());
            Assert.True(result.Status == WorkflowExecutionStatus.Suspended, result.ErrorMessage);
            id = result.InstanceId;
            Assert.Equal(new[] { 1 }, probe.Caller);
            Assert.Equal(new[] { 1, 2 }, probe.Outer);
            Assert.Equal(new[] { 1, 2, 3 }, probe.Live!.Items);

            if (!restart)
            {
                var completed = await client.ResumeAsync(id, "isolation", new Signal());
                Assert.True(completed.Status == WorkflowExecutionStatus.Success, completed.ErrorMessage);
                Assert.Equal(2, probe.Checks);

                return;
            }
        }

        await using var second = Services(store, catalog, probe);
        var recovered = await second.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "isolation", new Signal());
        Assert.True(recovered.Status == WorkflowExecutionStatus.Success, recovered.ErrorMessage);
        Assert.Equal(2, probe.Checks);
        Assert.Equal(new[] { 1 }, Assert.IsType<Caller>(recovered.WorkflowData).Items);
    }

    private static ServiceProvider Services(IWorkflowStateRepository store, IWorkflowCatalog catalog, Probe probe, int threshold = 0)
    {
        var services = new ServiceCollection().AddLogging();

        services.AddIxIFlow(o =>
        {
            o.VerifyCheckpointParts = true;
            o.PayloadThresholdBytes = threshold;
        });

        services.AddSingleton(store);
        services.AddSingleton(catalog);
        services.AddSingleton(probe);

        return services.BuildServiceProvider();
    }

    private sealed class ExitRepository : TestWorkflowStateRepository
    {
        public bool Interrupt = true;

        public override async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit,
            CancellationToken cancellationToken = default)
        {
            var before = await GetWorkflowInstanceAsync(commit.InstanceId);
            var instance = commit.ReadInstance(commit.ReplacesParts ? null : before);

            if (Interrupt && before != null && ExecutionCheckpoint.Read(StoredCheckpoint.WholeState(before)).InlineFrames.Count > 0 &&
                ExecutionCheckpoint.Read(instance.ExecutionStateJson).InlineFrames.Count == 0)
                throw new IOException("Interrupted after the final activity and before inline exit");

            return await base.CommitWorkflowInstanceAsync(commit, cancellationToken: cancellationToken);
        }
    }

    public sealed class Caller
    {
        public int Value { get; set; }
        public JsonElement Json { get; set; } = JsonSerializer.SerializeToElement(new { });
        public List<int> Items { get; set; } = [1];
        public List<int> Results { get; set; } = [];
    }

    public sealed class Data
    {
        public int Value { get; set; }
        public JsonElement Json { get; set; } = JsonSerializer.SerializeToElement(new { });
        public List<int> Items { get; set; } = [];
    }

    public sealed class Signal
    {
    }

    public sealed class Probe
    {
        public int Runs, Checks;
        public List<int>? Caller, Outer;
        public Data? Live, OuterData;

        public List<int> CaptureCaller(List<int> items)
        {
            Caller = items;

            return items;
        }

        public List<int> CaptureOuter(List<int> items)
        {
            Outer = items;

            return items;
        }

        public int Check(Data previous, List<int> outputs)
        {
            Assert.NotSame(outputs, previous.Items);
            Assert.NotSame(Live, previous);
            Assert.NotSame(OuterData, previous);
            Assert.Equal(new[] { 1, 2, 3 }, previous.Items);
            previous.Items.Add(99);
            Assert.Equal(new[] { 1, 2, 3 }, outputs);
            Checks++;

            return Checks;
        }
    }

    public sealed class DelegateActivity(Probe probe) : IAsyncActivity
    {
        public Action Callback { get; set; } = null!;

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken)
        {
            Callback();
            ((Data)context.WorkflowData).Value = 42;
            probe.Runs++;

            return Task.CompletedTask;
        }
    }

    public sealed class MutateLive(Probe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken)
        {
            var data = (Data)context.WorkflowData;
            Assert.NotSame(probe.Caller, data.Items);
            Assert.NotSame(probe.Outer, data.Items);
            data.Items.Add(3);
            probe.Live = data;

            return Task.CompletedTask;
        }
    }

    public sealed class MutateOuter(Probe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken)
        {
            var data = (Data)context.WorkflowData;
            Assert.NotSame(probe.Caller, data.Items);
            data.Items.Add(2);
            probe.OuterData = data;

            return Task.CompletedTask;
        }
    }

    public sealed class Noop : IAsyncActivity
    {
        public int Value { get; set; }
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Workflow("DelegateBody")]
    public sealed class DelegateBody : IWorkflow<Data>
    {
        public void Build(IWorkflowBuilder<Data> b) =>
            b.Step<DelegateActivity>(a => a.Input(x => x.Callback).From(c => new Action(Callback)));

        private static void Callback()
        {
        }
    }

    [Workflow("OpaqueBody")]
    public sealed class OpaqueBody : IWorkflow<Data>
    {
        public void Build(IWorkflowBuilder<Data> b) => b.Step<Noop>().WaitFor<Signal>("opaque");
    }

    [Workflow("LiveInner")]
    public sealed class Inner : IWorkflow<Data>
    {
        public void Build(IWorkflowBuilder<Data> b) => b.Step<MutateLive>().WaitFor<Signal>("isolation");
    }
}