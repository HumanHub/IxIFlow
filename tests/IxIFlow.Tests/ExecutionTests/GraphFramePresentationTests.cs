using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "control-flow"), Trait("Spec", "G-11")]
public sealed class GraphFramePresentationTests
{
    [Theory]
    [InlineData("Loop")] [InlineData("ForEach")] [InlineData("Sequence")] [InlineData("TryCatch")]
    public void OrdinaryFramesRetainTheirOwningStepKind(string kind)
    {
        var builder = new WorkflowBuilder<Data, object>([], "OrdinaryFrameKinds");
        var definition = (kind switch
        {
            "Loop" => builder.WhileDo(_ => true, b => b.Step<Work>()),
            "ForEach" => builder.ForEach(_ => new[] { 1 }, (b, _) => b.Step<Work>()),
            "Sequence" => builder.Sequence(b => b.Step<Work>()),
            _ => builder.Try(b => b.Step<Work>()).Finally(b => { })
        }).Build();
        var scopes = WorkflowDefinitionValidator.ForExecution(definition);
        var step = scopes.Steps("root").Single();
        var body = step.ChildScopes().First();
        var continuation = new ContinuationState { Id = "root", Stack =
        [
            new() { ScopeId = "root" },
            new() { ScopeId = "root/0/" + body.Kind, Kind = kind == "ForEach" ? FrameKind.Item : FrameKind.Body }
        ] };
        var checkpoint = new ExecutionCheckpoint { StructureHash = scopes.StructureHash, Continuations = [continuation] };
        var instance = new WorkflowInstance { InstanceId = "ordinary-frame-kinds" };
        var frame = InspectionProjection.Threads(instance, checkpoint, scopes, DateTime.UtcNow).Single().Frames.Last();
        Assert.Equal(kind, frame.Kind); Assert.Null(frame.GraphId);
        Assert.Equal(kind, ExecutionSnapshotFactory.Create(instance, checkpoint, scopes, DateTime.UtcNow).Frames.Last().Kind);
        Assert.True(InspectionProjection.Unchanged(new Dictionary<string, InspectionThread>
            { [continuation.Id] = InspectionProjection.Threads(instance, checkpoint, scopes, DateTime.UtcNow).Single() }, checkpoint, scopes));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RunAndListenFramesUseTheirBranchKindAndTheParentStateVisit(bool listener)
    {
        var definition = Workflow.Create<Data>("GraphBranchPresentation").StateMachine(m => m.Start("pending")
            .State("pending", s => s.OnActivity<Work>("end").On<Signal>("go", "end"))).Named("approval").Build();
        var scopes = WorkflowDefinitionValidator.ForExecution(definition);
        var graph = new GraphState { Node = "pending", Visits = 2, Via = "previous-edge" };
        var parent = new ContinuationState { Id = "root", Status = ContinuationStatus.Joining, Stack =
        [
            new() { ScopeId = "root", Graph = graph },
            new() { ScopeId = "root/0/state/pending", Kind = FrameKind.State, Node = "pending", NextStepIndex = 1, ArmOccurrence = 3 }
        ] };
        var child = new ContinuationState { Id = "child", ParentJoinId = "join", Stack =
        [new() { ScopeId = "root/0/state/pending/" + (listener ? "listen" : "on/0/run"),
            Kind = listener ? FrameKind.Listen : FrameKind.Run }] };
        var checkpoint = new ExecutionCheckpoint { StructureHash = scopes.StructureHash, Continuations = [parent, child],
            Joins = [new() { Id = "join", ParentContinuationId = parent.Id }] };
        var instance = new WorkflowInstance { InstanceId = "graph-branches" };
        var positions = InspectionProjection.Threads(instance, checkpoint, scopes, DateTime.UtcNow);
        var frame = positions.Single(t => t.ContinuationId == child.Id).Frames.Single();
        Assert.Equal(listener ? "Listen" : "Run", frame.Kind);
        Assert.Equal("approval / pending / " + (listener ? "listen" : "on 0 / run"), frame.Name);
        Assert.Equal(2, frame.Visit); Assert.Equal("previous-edge", frame.Via);
        Assert.Equal("root/0", frame.GraphId); Assert.Equal("pending", frame.Node);
        Assert.Equal(listener ? "Armed" : "Running", frame.Phase);
        var saved = positions.ToDictionary(t => t.ContinuationId);
        Assert.True(InspectionProjection.Unchanged(saved, checkpoint, scopes));
        graph.Visits++;
        Assert.False(InspectionProjection.Unchanged(saved, checkpoint, scopes));
        child.Stack[0].Fired = new() { Trigger = "root/0/state/pending/on/0/0" };
        frame = InspectionProjection.Threads(instance, checkpoint, scopes, DateTime.UtcNow)
            .Single(t => t.ContinuationId == child.Id).Frames.Single();
        Assert.Equal("Fired", frame.Phase); Assert.Equal(3, frame.Visit);
        var snapshot = ExecutionSnapshotFactory.Create(instance, checkpoint, scopes, DateTime.UtcNow);
        var publicFrame = snapshot.Frames.Single(f => f.FrameId == frame.ActivationId);
        Assert.Equal(frame.Name, publicFrame.Name); Assert.Equal(frame.Kind, publicFrame.Kind);
        Assert.Equal("3", publicFrame.State["Visit"]); Assert.Equal("pending", publicFrame.State["Node"]);
        var roundTrip = JsonSerializer.Deserialize<InspectionFrame>(JsonSerializer.Serialize(frame, ExecutionMessageContext.Default.InspectionFrame),
            ExecutionMessageContext.Default.InspectionFrame);
        Assert.Equal(frame, roundTrip);
    }

    [Fact]
    public void PendingChoiceChangesInvalidateRecordedPositionsWithoutExposingPayloads()
    {
        var definition = Workflow.Create<Data>("GraphChoicePresentation").Flowchart(f => f.Start("a")
            .Node("a", e => e.Always("end"))).Build();
        var scopes = WorkflowDefinitionValidator.ForExecution(definition);
        var graph = new GraphState { Node = "a", Visits = 2 };
        var branch = new ContinuationState { Id = "root", Stack =
        [
            new() { ScopeId = "root", Graph = graph },
            new() { ScopeId = "root/0/node/a", Kind = FrameKind.Node, Node = "a", NextStepIndex = 1 }
        ] };
        var checkpoint = new ExecutionCheckpoint { StructureHash = scopes.StructureHash, Continuations = [branch] };
        var instance = new WorkflowInstance { InstanceId = "graph-choice" };
        var saved = InspectionProjection.Threads(instance, checkpoint, scopes, DateTime.UtcNow).ToDictionary(t => t.ContinuationId);
        Assert.True(InspectionProjection.Unchanged(saved, checkpoint, scopes));
        graph.Pending = new() { Via = "root/0/node/a/to/0/0", Target = null, Cause = TransitionCause.Always,
            Event = SerializedValue.From(new Secret { Value = "private-event-payload" }) };
        Assert.False(InspectionProjection.Unchanged(saved, checkpoint, scopes));
        var frame = InspectionProjection.Threads(instance, checkpoint, scopes, DateTime.UtcNow).Single().Frames.Last();
        Assert.Equal(new("root/0/node/a/to/0/0", null, null, "Always"), frame.Pending);
        Assert.DoesNotContain("private-event-payload", JsonSerializer.Serialize(frame));
        var snapshot = ExecutionSnapshotFactory.Create(instance, checkpoint, scopes, DateTime.UtcNow).Frames.Last();
        Assert.Equal(frame.Pending!.Via, snapshot.State["PendingVia"]);
        Assert.Equal("Always", snapshot.State["PendingCause"]);
        Assert.DoesNotContain("private-event-payload", JsonSerializer.Serialize(snapshot));
    }

    public sealed class Data;
    public sealed class Signal;
    public sealed class Secret { public string Value { get; set; } = ""; }
    public sealed class Work : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
