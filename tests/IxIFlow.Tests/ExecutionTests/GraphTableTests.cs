using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "control-flow"), Trait("Spec", "W-01")]
public sealed class GraphTableTests
{
    [Fact]
    public async Task MutableLabelMetadataCannotChangeRoutingOrCachedHash()
    {
        var label = new MutableLabel { Values = [7] };
        var definition = Workflow.Create<LabelData>("GraphTableLabels").Flowchart(f => f.Start("route")
            .Node("route", e => e.Switch(c => c.WorkflowData.Label).Case(label, "selected").Otherwise("end"))
            .Node("selected", n => n.Step<MarkSelected>())).Build();
        var hash = WorkflowDefinitionValidator.Inspect(definition);
        var metadata = definition.GraphTable.Single().Transitions[0].Cases.Single().Label;
        Assert.NotSame(label, metadata);
        // A detached mutable copy or an immutable JSON projection is acceptable.
        if (metadata is MutableLabel copy) copy.Values[0] = 99;
        else
        {
            var json = Assert.IsType<JsonElement>(metadata);
            Assert.Equal(7, json.GetProperty("Values")[0].GetInt32());
            var jsonCopy = JsonSerializer.Deserialize<MutableLabel>(json)!;
            jsonCopy.Values[0] = 99;
        }
        Assert.Equal(7, label.Values[0]);
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow();
        await using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<IWorkflowClient>().RunAsync(definition, new LabelData());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.True(Assert.IsType<LabelData>(result.WorkflowData).Selected);
        Assert.Equal(hash, WorkflowDefinitionValidator.Inspect(definition));
    }

    public sealed class MutableLabel
    {
        public List<int> Values { get; set; } = [];
        public override bool Equals(object? obj) => obj is MutableLabel other && Values.SequenceEqual(other.Values);
        public override int GetHashCode() => Values.Aggregate(0, HashCode.Combine);
    }
    public sealed class LabelData
    {
        public MutableLabel Label { get; set; } = new() { Values = [7] };
        public bool Selected { get; set; }
    }
    public sealed class MarkSelected : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { ((LabelData)context.WorkflowData).Selected = true; return Task.CompletedTask; }
    }

    [Fact]
    public void CatalogTopologyIncludesNestedGraphsOrderedCasesActionsAndExplicitTimeoutEdges()
    {
        var definition = Definition();
        Assert.Equal(2, definition.GraphTable.Count);
        var flow = definition.GraphTable.Single(g => g.Kind == WorkflowStepType.Flowchart);
        Assert.Equal("root/0", flow.CatalogId);
        Assert.Equal("route", flow.Start);
        var node = flow.Nodes.Single(n => n.Key == "route");
        Assert.Equal("Route", node.Name);
        Assert.Equal("root/0/node/route/0", node.EnterStepId);
        Assert.Equal("author.cs:20", node.SourceLocation);
        Assert.Equal("root/0/node/route", node.BodyScopeId);
        Assert.Null(node.ExitScopeId);
        var explicitEdge = Assert.Single(flow.Transitions, t => t.Kind == TransitionCause.Explicit);
        Assert.Equal("done", explicitEdge.Cases.Single().To);
        Assert.Equal("leave.cs:24", explicitEdge.Cases.Single().SourceLocation);
        var machine = definition.GraphTable.Single(g => g.Kind == WorkflowStepType.StateMachine);
        var pending = machine.Nodes.Single(n => n.Key == "pending");
        Assert.Equal(machine.CatalogId + "/state/pending/exit", pending.ExitScopeId);
        Assert.True(machine.Nodes.Single(n => n.Key == "done").Final);
        Assert.Equal(new[] { TransitionCause.Activity, TransitionCause.Event, TransitionCause.Timer }, machine.Transitions.Select(t => t.Kind));
        var activity = machine.Transitions[0];
        Assert.Contains(typeof(Work).FullName!, activity.ActivityType);
        Assert.Null(activity.EventType);
        Assert.Equal(new[] { 0, 1 }, activity.Cases.Select(c => c.Order));
        Assert.True(activity.Cases[0].Guarded && activity.Cases[0].HasAction);
        Assert.False(activity.Cases[1].Guarded || activity.Cases[1].HasAction);
        Assert.Equal("go", machine.Transitions[1].Key);
        Assert.Contains(typeof(Signal).FullName!, machine.Transitions[1].EventType);
        Assert.Equal("00:01:00", machine.Transitions[2].After);
        foreach (var graph in definition.GraphTable)
        {
            Assert.Contains(definition.StepTable, s => s.CatalogId == graph.CatalogId && s.Kind == graph.Kind);
            Assert.All(graph.Nodes, n => Assert.Contains(definition.StepTable, s => s.CatalogId == n.EnterStepId));
            Assert.All(graph.Transitions.SelectMany(t => t.Cases), c => Assert.Contains(definition.StepTable, s => s.CatalogId == c.EdgeStepId));
        }
        var clone = new WorkflowDefinition { Name = definition.Name, Version = definition.Version, WorkflowDataType = typeof(Data), Steps = definition.Steps.Select(s => s.Clone()).ToList() };
        WorkflowDefinitionValidator.Validate(clone);
        Assert.Equal(JsonSerializer.Serialize(definition.GraphTable), JsonSerializer.Serialize(clone.GraphTable));
    }

    [Fact]
    public async Task OperationsReturnDetachedTopologyOfTheRegisteredDefinition()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow();
        await using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<Data>("GraphTableOperations").StateMachine(m => m.Start("pending")
            .State("pending", s => s.On<Signal>("go", "done"))
            .State("done", s => s.Final())).Build();
        var client = provider.GetRequiredService<IWorkflowClient>();
        var start = await client.RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Suspended, start.Status);
        var operations = provider.GetRequiredService<IWorkflowOperations>();
        var table = await operations.GetGraphTableAsync(start.InstanceId);
        Assert.Equal(JsonSerializer.Serialize(definition.GraphTable), JsonSerializer.Serialize(table));
        Assert.NotSame(definition.GraphTable[0], table[0]);
        var nodes = Assert.IsAssignableFrom<IList<GraphNodeEntry>>(table[0].Nodes);
        nodes[0] = nodes[0] with { Name = "Changed locally" };
        Assert.Equal("pending", (await operations.GetGraphTableAsync(start.InstanceId))[0].Nodes[0].Name);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operations.GetGraphTableAsync(start.InstanceId, cancelled.Token));
        await client.ResumeAsync(start.InstanceId, "go", new Signal());
        Assert.Equal(JsonSerializer.Serialize(definition.GraphTable), JsonSerializer.Serialize(await operations.GetGraphTableAsync(start.InstanceId)));
    }

    private static WorkflowDefinition Definition() => Workflow.Create<Data>("GraphTableNested").Flowchart(f => f.Start("route")
        .Node("route", n => n.WaitFor<Signal>("outer", configure: w => w.TimeoutAfter(TimeSpan.FromMinutes(1))
            .OnTimeout(t => t.Transition("done", sourcePath: "leave.cs", sourceLine: 24)))
            .StateMachine(m => m.Start("pending").State("pending", s => s
                .Exit(e => e.Step<Work>())
                .OnActivity<Work>(t => t.When(c => c.PreviousStep.Value > 0, "done", a => a.Step<Work>()).Otherwise("end"))
                .On<Signal>("go", "done").After(TimeSpan.FromMinutes(1), "done"))
                .State("done", s => s.Final())), e => e.Always("done"), sourcePath: "author.cs", sourceLine: 20).Named("Route")
        .Node("done")).Build();

    public sealed class Data;
    public sealed class Signal;
    public sealed class Work : IAsyncActivity
    {
        public int Value { get; set; }
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
