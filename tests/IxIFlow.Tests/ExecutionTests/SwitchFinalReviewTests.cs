using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "authoring"), Trait("Spec", "W-03")]
public sealed class SwitchFinalReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatchForEachUseRetainsResultAndChildPrevious(bool pinned)
    {
        var child = Child();

        var definition = Workflow.Create<Data>("CatchInlineResult").Step<Echo>().Try(t => t.Step<Fail>())
            .Catch<InvalidOperationException, Fault>(c => c.ForEach(itemsExpression: ctx => ctx.WorkflowData.Items,
                body: (body, item) => Use(body, child, pinned).Result(ctx => ctx.PreviousStep.Value),
                setup: s => s.Into<string>(ctx => ctx.WorkflowData.Results))).Build();

        await using var services = Services();
        var run = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, run.Status);
        Assert.Equal("child", Assert.Single(Assert.IsType<Data>(run.WorkflowData).Results).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatchTimeoutUseRetainsTransitionAndChildPrevious(bool pinned)
    {
        var child = Child();
        var time = new FakeTimeProvider();
        var store = new InMemoryWorkflowStateRepository(time);

        var definition = Workflow.Create<Data>("CatchInlineTimeout").Step<Echo>().Try(t => t.Step<Fail>())
            .Catch<InvalidOperationException, Fault>(c => c.Flowchart(g => g.Start("wait")
                .Node("wait", b => b.WaitFor<Signal>("go", configure: w => w.TimeoutAfter(TimeSpan.FromSeconds(1))
                    .OnTimeout(body => Use(body, child, pinned)
                        .Step<Echo>(s => s.Input(x => x.Value).From(ctx => ctx.PreviousStep.Value)
                            .Output(x => x.Value).To(ctx => ctx.WorkflowData.Value)).Transition("done"))))
                .Node("done"))).Build();

        var catalog = new WorkflowCatalog();
        await catalog.RegisterWorkflowAsync(definition);
        string id;

        await using (var services = Services(store, time: time, catalog: catalog))
        {
            var run = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
            Assert.Equal(WorkflowExecutionStatus.Suspended, run.Status);
            id = run.InstanceId;
        }

        time.Advance(TimeSpan.FromSeconds(2));
        await using var recovered = Services(store, time: time, catalog: catalog);
        var client = recovered.GetRequiredService<IWorkflowClient>();
        await client.RecoverAsync(id);
        var result = await client.AwaitAsync(id, WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("child", Assert.IsType<Data>(result.WorkflowData).Value);
    }

    [Fact]
    public void FaultSpecializedExplicitContractInvokeRetainsBothExitsAndChildPrevious()
    {
        var contract = WorkflowDefinitionValidator.GetContract(Child());

        var definition = Workflow.Create<Data>("CatchInvokeExits").Step<Echo>().Try(t => t.Step<Fail>())
            .Catch<InvalidOperationException, Fault>(c => c
                .ForEach(itemsExpression: ctx => ctx.WorkflowData.Items, body: (b, item) => b
                    .Invoke<ChildData>("InlineReviewChild", 1, contract, s => s.Input(x => x.Value).From(ctx => ctx.Fault.Message))
                    .Result(ctx => ctx.PreviousStep.Value), setup: s => s.Into<string>(ctx => ctx.WorkflowData.Results))
                .Flowchart(g => g.Start("wait").Node("wait", b => b.WaitFor<Signal>("go",
                    configure: w => w.TimeoutAfter(TimeSpan.FromSeconds(1))
                        .OnTimeout(b => b
                            .Invoke<ChildData>("InlineReviewChild", 1, contract, s => s.Input(x => x.Value).From(ctx => ctx.Fault.Message))
                            .Step<Echo>(s => s.Input(x => x.Value).From(ctx => ctx.PreviousStep.Value)).Transition("done")))).Node("done")))
            .Build();

        var invokes = ParallelRules.Flatten(definition.Steps).Where(s => s.StepType == WorkflowStepType.WorkflowInvocation).ToArray();
        Assert.Equal(2, invokes.Length);
        Assert.All(invokes, step => Assert.Equal(contract.ToJson(), step.InvokedContract!.ToJson()));
    }

    [Theory]
    [InlineData(TraceDetail.Full, null, true)]
    [InlineData(TraceDetail.Activities, null, false)]
    [InlineData(TraceDetail.Full, 1L, false)]
    public async Task InlineTraceEntriesRespectDetailAndExhaustedControlFlowBudget(TraceDetail detail, long? budget, bool inline)
    {
        var definition = Workflow.Create<Data>("InlineTraceLimits").Step<Echo>().Use<InlineReviewChild, ChildData>().Build();
        await using var services = Services(detail: detail, budget: budget);
        var client = services.GetRequiredService<IWorkflowClient>();
        var result = await client.RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var trace = await client.GetTraceAsync(result.InstanceId);
        Assert.Equal(inline, trace.Any(t => t.EntryType == TraceEntryType.InlineEntered));
        Assert.Equal(inline, trace.Any(t => t.EntryType == TraceEntryType.InlineExited));
        Assert.Contains(trace, t => t.EntryType == TraceEntryType.ActivityCompleted);

        if (budget != null)
        {
            var marker = Assert.Single(trace, t => t.EntryType == TraceEntryType.TraceBudgetReached);
            Assert.DoesNotContain(trace.Skip(trace.ToList().IndexOf(marker) + 1), t => TraceBuffer.IsControlFlow(t.EntryType));
        }
    }

    private static IFaultForEachWorkflowBuilder<Data, Fault, ChildData> Use(IFaultForEachWorkflowBuilder<Data, Fault, Echo> body,
        WorkflowDefinition child, bool pinned) =>
        pinned ? body.Use<ChildData>(Child, Pin(child)) : body.Use<InlineReviewChild, ChildData>();

    private static IFaultTimeoutWorkflowBuilder<Data, Fault, ChildData> Use(IFaultTimeoutWorkflowBuilder<Data, Fault, Echo> body,
        WorkflowDefinition child, bool pinned) =>
        pinned ? body.Use<ChildData>(Child, Pin(child)) : body.Use<InlineReviewChild, ChildData>();

    private static WorkflowDefinition Child()
    {
        var builder = Workflow.Create<ChildData>("InlineReviewChild");
        new InlineReviewChild().Build(builder);

        return builder.Build();
    }

    private static WorkflowInlinePin Pin(WorkflowDefinition child) => new(child.Name, child.Version,
        WorkflowDefinitionValidator.Inspect(child), WorkflowDefinitionValidator.GetContract(child).Hash);

    private static ServiceProvider Services(IWorkflowStateRepository? store = null, TraceDetail detail = TraceDetail.Full,
        long? budget = null, TimeProvider? time = null, IWorkflowCatalog? catalog = null)
    {
        var services = new ServiceCollection().AddLogging();

        services.AddIxIFlow(o =>
        {
            o.Debugging = true;
            o.TraceDetail = detail;
            o.MaxTraceEntriesPerInstance = budget;
        });

        if (store != null)
            services.AddSingleton(store);

        if (time != null)
            services.AddSingleton(time);

        if (catalog != null)
            services.AddSingleton(catalog);

        return services.BuildServiceProvider();
    }

    public sealed class Data
    {
        public List<int> Items { get; set; } = [1];
        public List<ItemResult<string>> Results { get; set; } = [];
        public string Value { get; set; } = "";
    }

    public sealed class ChildData
    {
        public string Value { get; set; } = "";
    }

    public sealed class Fault
    {
        public string Message { get; set; } = "";
    }

    public sealed class Signal;

    public sealed class Echo : IAsyncActivity
    {
        public string Value { get; set; } = "";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public sealed class Fail : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("fault"));
    }

    [Workflow("InlineReviewChild")]
    public sealed class InlineReviewChild : IWorkflow<ChildData>
    {
        public void Build(IWorkflowBuilder<ChildData> b) => b.Step<Echo>(s => s.Input(x => x.Value).From(ctx => "child")
            .Output(x => x.Value).To(ctx => ctx.WorkflowData.Value));
    }
}