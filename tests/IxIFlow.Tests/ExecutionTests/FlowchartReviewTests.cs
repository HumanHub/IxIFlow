using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "control-flow"), Trait("Spec", "W-01")]
public sealed class FlowchartReviewTests
{
    [Fact]
    public async Task CatchFlowchartPreservesFaultContextPreviousValueAndCatchChaining()
    {
        await using var services = new ServiceCollection().AddLogging().AddIxIFlow().Services.BuildServiceProvider();
        var definition = Workflow.Create<Data>("CatchGraphReview").Step<Incoming>()
            .Try(b => b.Step<Throw>())
            .Catch<InvalidOperationException, MessageFault>(c => c
                .Flowchart(f => f.Start("a")
                    .Node("a", n => n.Sequence(s => s.Step<Read>(a => a
                        .Input(x => x.Text).From(ctx => ctx.PreviousStep.Text)
                        .Output(x => x.Text).To(ctx => ctx.WorkflowData.Inside))),
                        e => e.When(ctx => ctx.PreviousStep.Text == "incoming", "b"))
                    .Node("b", n => n.Step<Read>(a => a.Input(x => x.Text).From(ctx => ctx.PreviousStep.Text)
                        .Output(x => x.Text).To(ctx => ctx.WorkflowData.AtTarget))),
                    sourcePath: "catch-graph.cs", sourceLine: 23)
                .WithId("catch-chart", sourcePath: "catch-graph.cs", sourceLine: 23)
                .Step<Read>(a => a.Input(x => x.Text).From(ctx => ctx.Fault.Message + ":" + ctx.PreviousStep.Text)
                    .Output(x => x.Text).To(ctx => ctx.WorkflowData.After)))
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        var data = Assert.IsType<Data>(result.WorkflowData);
        Assert.Equal("incoming", data.Inside);
        Assert.Equal("incoming", data.AtTarget);
        Assert.Equal("boom:incoming", data.After);
        var graph = Assert.Single(definition.StepTable, step => step.AuthorId == "catch-chart");
        Assert.Equal("catch-graph.cs:23", graph.SourceLocation);
        var scopes = WorkflowDefinitionValidator.ForExecution(definition);
        var fault = new MessageFault { Message = "captured" };
        foreach (var (_, step) in scopes.AllSteps.Where(pair => pair.Key == graph.CatalogId ||
                     pair.Key.StartsWith(graph.CatalogId + "/", StringComparison.Ordinal)))
        {
            var context = step.Runtime.Context(new MappingContextValues(new Data(),
                Activator.CreateInstance(step.PreviousStepDataType!), fault));
            Assert.Equal(typeof(FaultContext<,,>), context.GetType().GetGenericTypeDefinition());
            Assert.Same(fault, context.GetType().GetProperty("Fault")!.GetValue(context));
        }
    }

    [Theory]
    [InlineData("sequence")]
    [InlineData("timeout")]
    public void OutRetainsTheEnclosingNodeBoundaryAcrossCheckpointSerialization(string child)
    {
        var root = new ScopePosition { ScopeId = "root", Graph = new GraphState { Node = "a", Visits = 1 } };
        var node = new ScopePosition { ScopeId = "root/0/node/a", Kind = FrameKind.Node, Node = "a" };
        var nested = new ScopePosition { ScopeId = node.ScopeId + "/1/" + child };
        var branch = new ContinuationState { Id = "root", Stack = [root, node, nested] };
        var checkpoint = new ExecutionCheckpoint { StructureHash = "review", Continuations = [branch] };
        checkpoint.Debug = checkpoint.Debug with
        {
            Stepping = SteppingPolicy.Start(checkpoint, new DebugCommand("out", 1, DebugCommandKind.Step, 0,
                "review", ContinuationId: branch.Id, StepKind: DebugStepKind.Out))
        };
        checkpoint = ExecutionCheckpoint.Read(JsonSerializer.Serialize(checkpoint));
        branch = checkpoint.Continuations[0];
        Assert.Equal(node.ActivationId, checkpoint.Debug.Stepping!.Origin.ActivationId);
        branch.Stack.RemoveAt(2);
        Assert.False(SteppingPolicy.StopAtPick(checkpoint, branch, "still-in-a", DateTime.UtcNow));
        branch.Stack.RemoveAt(1);
        branch.Stack.Add(new ScopePosition { ScopeId = "root/0/node/b", Kind = FrameKind.Node, Node = "b" });
        Assert.True(SteppingPolicy.StopAtPick(checkpoint, branch, "b-enter", DateTime.UtcNow));
    }

    public sealed class Data
    {
        public string Inside { get; set; } = "";
        public string AtTarget { get; set; } = "";
        public string After { get; set; } = "";
    }
    public sealed class MessageFault { public string Message { get; set; } = ""; }
    public sealed class Incoming : IAsyncActivity
    {
        public string Text { get; set; } = "incoming";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public sealed class Read : IAsyncActivity
    {
        public string Text { get; set; } = "";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public sealed class Throw : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");
    }
}
