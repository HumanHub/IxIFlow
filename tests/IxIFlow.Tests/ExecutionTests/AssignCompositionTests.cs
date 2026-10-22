using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "control-flow")]
[Trait("Spec", "W-04")]
public sealed class AssignCompositionTests
{
    [Fact]
    public async Task ParallelAndParallelItemsUseTheExistingCombiner()
    {
        var definition = Workflow.Create<Data>("AssignCombiners").Assign(ctx => ctx.WorkflowData.Count, ctx => 0)
            .Parallel(p => p.Combine<int, Sum<int>>(d => d.Count)
                .Do(b => b.Assign(ctx => ctx.WorkflowData.Count, ctx => 2))
                .Do(b => b.Assign(ctx => ctx.WorkflowData.Count, ctx => 3)))
            .ForEach(itemsExpression: ctx => ctx.WorkflowData.Items,
                body: (b, item) => b.Assign(ctx => ctx.WorkflowData.Other, ctx => ctx.Item(item)),
                setup: s => s.Parallel().Combine<int, Sum<int>>(d => d.Other)).Build();
        await using var services = Services();
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var data = Assert.IsType<Data>(result.WorkflowData);
        Assert.Equal(5, data.Count);
        Assert.Equal(3, data.Other);
    }

    [Theory]
    [InlineData(false, 1)] [InlineData(true, 2)]
    public async Task SagaRetryReappliesAssignmentsOnlyWhenTheForwardPassRestarts(bool restart, int expected)
    {
        var definition = Workflow.Create<Data>("AssignSaga" + restart).Assign(ctx => ctx.WorkflowData.Count, ctx => 0)
            .Saga(s => s.Assign(ctx => ctx.WorkflowData.Count, ctx => ctx.WorkflowData.Count + 1).Step<FailOnce>())
            .OnError<InvalidOperationException>(e =>
            {
                var undo = restart ? e.Compensate() : e.CompensateNone();
                undo.ThenRetry(new RetryPolicy(1));
            }).Build();
        await using var services = Services();
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(expected, Assert.IsType<Data>(result.WorkflowData).Count);
        Assert.Equal(2, services.GetRequiredService<Attempts>().Count);
    }

    [Fact]
    public async Task NestedCatchWaitRestoresInnerAndOuterFaultsAcrossAParentJoin()
    {
        var definition = Workflow.Create<Data>("AssignShadow").Assign(ctx => ctx.WorkflowData.Text, ctx => "")
            .Try(b => b.Step<FailOuter>()).Catch<InvalidOperationException, Fault>(outer => outer.Parallel(p => p.Do(b => b
                .Try(t => t.Step<FailInner>()).Catch<ArgumentException, Fault>(inner => inner.WaitFor<Go>("inner")
                    .Assign(ctx => ctx.WorkflowData.Text, ctx => ctx.Fault.Message))
                .Finally(cleanup => cleanup.Assign(ctx => ctx.WorkflowData.Text, ctx => ctx.WorkflowData.Text + "|" + ctx.Fault.Message)))))
            .Build();
        var store = new InMemoryWorkflowStateRepository();
        var catalog = new WorkflowCatalog();
        await catalog.RegisterWorkflowAsync(definition);
        string id;
        await using (var first = Services(store, catalog))
        {
            var idle = await first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
            Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
            id = idle.InstanceId;
        }
        await using var second = Services(store, catalog);
        var result = await second.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "inner", new Go());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("inner|outer", Assert.IsType<Data>(result.WorkflowData).Text);
    }

    [Fact]
    public async Task ClockAndPreviousValuesUseTheCurrentExecutionContext()
    {
        var time = new FakeTimeProvider();
        var definition = Workflow.Create<Data>("AssignClock").Assign(ctx => ctx.WorkflowData.Stamp, ctx => ctx.UtcNow)
            .Step<Seed>().Sequence(b => b.Assign(ctx => ctx.WorkflowData.Count, ctx => ctx.PreviousStep.Count))
            .Assign(ctx => ctx.WorkflowData.Other, ctx => ctx.PreviousStep.Count).Build();
        await using var services = Services(time: time);
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var data = Assert.IsType<Data>(result.WorkflowData);
        Assert.Equal(time.GetUtcNow(), data.Stamp);
        Assert.Equal(7, data.Count);
        Assert.Equal(7, data.Other);
    }

    [Fact]
    public void TargetIndexesCannotHideCollectionMutation()
    {
        Assert.Contains("mutation", Assert.Throws<WorkflowEngineException>(() => Workflow.Create<Data>("AssignIndexMutation")
            .Assign(ctx => ctx.WorkflowData.Items[ctx.WorkflowData.Items.Remove(1) ? 0 : 1], ctx => 2).Build()).Message);
    }

    private static ServiceProvider Services(IWorkflowStateRepository? store = null, IWorkflowCatalog? catalog = null, TimeProvider? time = null)
    {
        var services = new ServiceCollection().AddLogging().AddIxIFlow(o => o.PayloadThresholdBytes = 1).Services;
        services.AddSingleton<Attempts>();
        if (store != null) services.AddSingleton(store);
        if (catalog != null) services.AddSingleton(catalog);
        if (time != null) services.AddSingleton(time);
        return services.BuildServiceProvider();
    }
    public sealed class Data
    {
        public int Count { get; set; }
        public int Other { get; set; }
        public string Text { get; set; } = "";
        public DateTimeOffset Stamp { get; set; }
        public List<int> Items { get; set; } = [1, 2];
    }
    public sealed class Fault { public string Message { get; set; } = ""; }
    public sealed class Go { }
    public sealed class Attempts { public int Count; }
    public sealed class FailOnce(Attempts attempts) : IAsyncActivity
    { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => ++attempts.Count == 1 ? Task.FromException(new InvalidOperationException("retry")) : Task.CompletedTask; }
    public sealed class FailOuter : IAsyncActivity
    { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.FromException(new InvalidOperationException("outer")); }
    public sealed class FailInner : IAsyncActivity
    { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.FromException(new ArgumentException("inner")); }
    public sealed class Seed : IAsyncActivity
    { public int Count { get; set; } = 7; public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask; }
}
