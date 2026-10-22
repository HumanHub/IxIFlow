using System.Text;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "control-flow"), Trait("Spec", "F-17")]
public sealed class InlineCheckpointTests
{
    [Theory, InlineData(false), InlineData(true)]
    public void ForkBaselineSurvivesOwnerUnwindAndRecovery(bool items)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var definition = items ? Workflow.Create<Caller>("ItemsOwner").Use<Items, Data>().Build()
            : Workflow.Create<Caller>("ForkOwner").Use<Fork, Data>().Build();
        var (checkpoint, scopes, instance) = Start(definition);
        var transitions = new ContinuationTransitions(services, TimeProvider.System);
        var root = checkpoint.Continuations[0];
        transitions.Advance(scopes, checkpoint, root, 100, 100);
        var frame = root.Stack[^1];
        var inline = checkpoint.InlineFrames[frame.ActivationId];
        ((Data)inline.Value).Value = 10;
        checkpoint.MarkDataChanged(root);
        frame.NextStepIndex = 1; // the no-op only establishes fluent previous typing
        transitions.Advance(scopes, checkpoint, root, 100, 100);
        var join = Assert.Single(checkpoint.Joins);
        Assert.Equal(frame.ActivationId, join.DataFrameId);
        var branch = checkpoint.BranchesOf(join)[0];
        ((Data)checkpoint.DataOf(branch)).Value = 23;
        checkpoint.MarkDataChanged(branch);
        checkpoint.Trace.DropFrames(root, 1, ItemEndCause.UnwoundByOuterHandler);
        Assert.IsType<Caller>(checkpoint.DataOf(root));
        var stored = ExecutionCheckpointWriter.Prepare(instance, checkpoint, checkpoint.RootData, scopes, DateTime.UtcNow,
            preserveContinuationState: true, verify: true);
        Assert.Contains(StoredCheckpoint.FrameKey(frame.ActivationId), stored.Parts.Keys);
        var restored = ExecutionCheckpoint.Read(StoredCheckpoint.AssembleState(stored.Trunk, stored.Parts));
        restored.RootData = new Caller();
        Assert.Equal(23, Assert.IsType<Data>(restored.DataOf(restored.ContinuationById(branch.Id)!)).Value);
        Assert.Equal(10, Assert.IsType<Data>(restored.Baseline(Assert.Single(restored.Joins))).Value);
        restored.Joins.Clear();
        restored.RemoveContinuations(c => c.ParentJoinId != null);
        restored.RecordInlineFrames(null);
        Assert.Empty(restored.InlineFrames);
    }

    [Fact]
    public void DirtyTrackingNamesTheActiveOwnerAndFailedCommitMarksAreRestored()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var definition = Workflow.Create<Caller>("Dirty").Use<Fork, Data>().Build();
        var (checkpoint, scopes, instance) = Start(definition);
        var root = checkpoint.Continuations[0];
        new ContinuationTransitions(services, TimeProvider.System).Advance(scopes, checkpoint, root, 100, 100);
        var frame = root.Stack[^1];
        var first = ExecutionCheckpointWriter.Prepare(instance, checkpoint, checkpoint.RootData, scopes, DateTime.UtcNow);
        checkpoint.TakeMarks();
        checkpoint.RootDataChanged = false;
        ((Data)checkpoint.DataOf(root)).Value = 42;
        checkpoint.MarkDataChanged(root);
        Assert.False(checkpoint.RootDataChanged);
        Assert.False(root.DataChanged);
        checkpoint.RecordInlineFrames(null);
        var rejected = checkpoint.TakeMarks();
        Assert.True(rejected.HasFrame(frame.ActivationId));
        Assert.False(checkpoint.InlineFrames[frame.ActivationId].Changed);
        checkpoint.RestoreMarks(rejected);
        var retried = StoredCheckpoint.Split(checkpoint, first.Parts[CheckpointParts.DataKey], first.Parts, checkpoint.TakeMarks(), verify: true);
        Assert.NotEqual(first.Parts[StoredCheckpoint.FrameKey(frame.ActivationId)], retried.Parts[StoredCheckpoint.FrameKey(frame.ActivationId)]);
    }

    [Fact]
    public async Task CheckpointLimitRejectionRestoresFrameMarksForTheNextPreparation()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var definition = Workflow.Create<Caller>("RejectedFrame").Use<Fork, Data>().Build();
        var (checkpoint, scopes, instance) = Start(definition);
        var root = checkpoint.Continuations[0];
        new ContinuationTransitions(services, TimeProvider.System).Advance(scopes, checkpoint, root, 100, 100);
        var id = root.Stack[^1].ActivationId;
        var saved = ExecutionCheckpointWriter.Prepare(instance, checkpoint, checkpoint.RootData, scopes, DateTime.UtcNow);
        instance.Saved(saved.Parts, instance.Revision);
        ((Data)checkpoint.DataOf(root)).Value = 99;
        checkpoint.MarkDataChanged(root);
        var session = new ExecutionSession(Moq.Mock.Of<IExecutionHost>(), Guid.NewGuid(), instance.InstanceId, 0,
            ExecutionLimits.Default with { MaxCheckpointBytes = 1 }, canPark: false);
        await Assert.ThrowsAsync<CheckpointLimitExceededException>(() => ExecutionCheckpointWriter.CommitAsync(session,
            instance, checkpoint, checkpoint.RootData, scopes, DateTime.UtcNow));
        Assert.Same(saved.Parts[StoredCheckpoint.FrameKey(id)], instance.PartsAt(instance.Revision)![StoredCheckpoint.FrameKey(id)]);
        var retried = ExecutionCheckpointWriter.Prepare(instance, checkpoint, checkpoint.RootData, scopes, DateTime.UtcNow, verify: true);
        Assert.Equal(99, BranchData.Copy(retried.Parts[StoredCheckpoint.FrameKey(id)].InlineText, typeof(Data)) is Data data ? data.Value : -1);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task LargeFramePartsUseClaimCheckAndHydrateUnderTheBudget(bool branchOwner)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var definition = Workflow.Create<Caller>("PayloadFrame").Use<Fork, Data>().Build();
        var (checkpoint, scopes, instance) = Start(definition);
        var root = checkpoint.Continuations[0];
        if (branchOwner)
        {
            var parent = new ContinuationState { Id = "parent", Stack = [new() { ScopeId = "root" }] };
            checkpoint.Continuations.Insert(0, parent);
            var join = new ParallelJoinState { ParentContinuationId = parent.Id };
            join.ChildContinuationIds.Add(root.Id);
            checkpoint.Joins.Add(join);
            root.ParentJoinId = join.Id;
        }
        new ContinuationTransitions(services, TimeProvider.System).Advance(scopes, checkpoint, root, 100, 100);
        var id = root.Stack[^1].ActivationId;
        var frame = checkpoint.InlineFrames[id];
        ((Data)frame.Value).Text = new string('x', 4096);
        checkpoint.MarkDataChanged(root);
        var payloads = new CheckpointPayloads(ExecutionLimits.Default with { PayloadThresholdBytes = 128 }, new PayloadBudget(1_000_000));
        checkpoint.RecordInlineFrames(payloads);
        var saved = StoredCheckpoint.Split(checkpoint, StoredJson.Inline("{}"), payloads: payloads);
        var part = saved.Parts[StoredCheckpoint.FrameKey(id)];
        Assert.NotNull(part.Reference);
        Assert.Contains(part.Reference, saved.PayloadReferences);
        Assert.DoesNotContain(new string('x', 100), saved.Trunk);
        Assert.True(saved.Bytes < 4096);
        var bodies = payloads.Pending.ToDictionary(value => value.Identity, value => value.Encoded);
        var coldParts = saved.Parts.ToDictionary(pair => pair.Key, pair => pair.Value.Reference is { } reference
            ? StoredJson.FromReference(reference) : pair.Value);
        var reader = new PayloadReader(reference => Task.FromResult(bodies[reference]), new PayloadBudget(1_000_000));
        // Branch envelopes also use claim check; assembly hydrates envelopes, then frame data on demand.
        foreach (var (key, value) in coldParts) if (key.StartsWith("c/")) await reader.ReadAsync(value);
        var restored = ExecutionCheckpoint.Read(StoredCheckpoint.AssembleState(saved.Trunk, coldParts));
        await reader.ReadAsync(restored.InlineFrames[id].Data);
        Assert.Equal(new string('x', 4096), Assert.IsType<Data>(restored.InlineFrames[id].Value).Text);
        var unchanged = StoredCheckpoint.Split(checkpoint, StoredJson.Inline("{}"), saved.Parts, checkpoint.TakeMarks(), payloads: payloads);
        Assert.Same(saved.Parts[StoredCheckpoint.FrameKey(id)], unchanged.Parts[StoredCheckpoint.FrameKey(id)]);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task MissingOrCorruptFramePayloadIsUnavailable(bool corrupt)
    {
        var json = StoredJson.Inline("{\"Value\":12}");
        var frame = new InlineFrameState { DataType = WorkflowTypeIdentity.SavedName(typeof(Data)), Data = StoredJson.FromReference(json.Identity) };
        var reader = new PayloadReader(_ => corrupt ? Task.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes("bad"))
            : Task.FromException<ReadOnlyMemory<byte>>(new InvalidDataException("missing")), new PayloadBudget(1000));
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => reader.ReadAsync(frame.Data));
        Assert.Equal(WorkflowErrorKind.PayloadUnavailable, error.Kind);
    }

    [Fact]
    public void FramePartsCountTowardCheckpointSizeAndContractElevenRejectsTen()
    {
        var checkpoint = new ExecutionCheckpoint();
        checkpoint.InlineFrames.Add("frame", new() { DataType = WorkflowTypeIdentity.SavedName(typeof(Data)), Data = StoredJson.Inline(new string('x', 1000)) });
        var stored = StoredCheckpoint.Split(checkpoint, StoredJson.Inline("{}"));
        Assert.True(stored.Bytes >= 1000);
        Assert.Equal(11, WorkflowDefinitionValidator.CheckpointContractVersion);
        var error = Assert.Throws<WorkflowEngineException>(() => ExecutionCheckpoint.Read("{\"Runtime\":\"structured-v2\",\"SchemaVersion\":10}"));
        Assert.Contains("expected 11", error.Message);
    }

    private static (ExecutionCheckpoint, WorkflowScopeCatalog, WorkflowInstance) Start(WorkflowDefinition definition)
    {
        WorkflowDefinitionValidator.Validate(definition);
        var scopes = WorkflowDefinitionValidator.ForExecution(definition);
        var checkpoint = new ExecutionCheckpoint { RootData = new Caller(), StructureHash = scopes.StructureHash,
            Continuations = [new() { Id = "root", Stack = [new() { ScopeId = "root" }] }] };
        var instance = new WorkflowInstance { InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = definition.Name, WorkflowVersion = 1 };
        checkpoint.Trace.Configure(instance, checkpoint, ExecutionLimits.Default, TimeProvider.System, scopes);
        return (checkpoint, scopes, instance);
    }
    public sealed class Caller { public int Different { get; set; } }
    public sealed class Data { public int Value { get; set; } public string Text { get; set; } = ""; public List<int> Values { get; set; } = [1, 2]; }
    public sealed class Signal { }
    public sealed class Noop : IAsyncActivity { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken) => Task.CompletedTask; }
    [Workflow("Fork")] public sealed class Fork : IWorkflow<Data>
    { public void Build(IWorkflowBuilder<Data> b) => b.Step<Noop>().Parallel(p => p.Do(x => x.WaitFor<Signal>("one")).Do(x => x.WaitFor<Signal>("two"))); }
    [Workflow("Items")] public sealed class Items : IWorkflow<Data>
    { public void Build(IWorkflowBuilder<Data> b) => b.Step<Noop>().ForEach(c => c.WorkflowData.Values, (body, item) => body.WaitFor<Signal>("item"), s => s.Parallel()); }
}
