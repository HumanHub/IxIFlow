using IxIFlow.Tests.Infrastructure;
using System.Collections.Concurrent;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// Step and Invoke run with or without a mapping setup; a setup that is given is applied.
/// </summary>
[Trait("Feature", "children")]
public sealed class OptionalSetupTests
{
    [Fact]
    public async Task StepRunsWithoutSetup()
    {
        using var services = Services();
        var definition = Workflow.Create<ParentData>("StepWithoutSetup")
            .Step<RecordActivity>()
            .Build();

        var result = await Engine(services).RunToEndAsync(definition, new ParentData());

        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(["activity-default"], Probe(services).Seen);
    }

    [Fact]
    public async Task StepAppliesItsSetup()
    {
        using var services = Services();
        var definition = Workflow.Create<ParentData>("StepWithSetup")
            .Step<RecordActivity>(setup => setup
                .Input(activity => activity.Value).From(ctx => ctx.WorkflowData.Value)
                .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Result))
            .Build();

        var result = await Engine(services).RunAsync(definition, new ParentData { Value = "from-parent" });

        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(["from-parent"], Probe(services).Seen);
        Assert.Equal("from-parent", ((ParentData)result.WorkflowData!).Result);
    }

    [Fact]
    public async Task InvokeByNameRunsTheChildWithDefaultDataWithoutSetup()
    {
        using var services = Services();
        await RegisterChildAsync(services);
        var definition = Workflow.Create<ParentData>("InvokeByNameWithoutSetup")
            .Step<RecordActivity>()
            .Invoke<ChildData>("SetupChild", 1)
            .Build();

        var result = await Engine(services).RunToEndAsync(definition, new ParentData());

        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(["activity-default", "child-default"], Probe(services).Seen);
    }

    [Fact]
    public async Task InvokeByTypeRunsTheChildWithDefaultDataWithoutSetup()
    {
        using var services = Services();
        var definition = Workflow.Create<ParentData>("InvokeByTypeWithoutSetup")
            .Step<RecordActivity>()
            .Invoke<ChildWorkflow, ChildData>()
            .Build();

        var result = await Engine(services).RunToEndAsync(definition, new ParentData());

        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(["activity-default", "child-default"], Probe(services).Seen);
    }

    [Fact]
    public async Task InvokeAppliesItsSetup()
    {
        using var services = Services();
        await RegisterChildAsync(services);
        var definition = Workflow.Create<ParentData>("InvokeWithSetup")
            .Step<RecordActivity>()
            .Invoke<ChildData>("SetupChild", 1, setup => setup
                .Input(child => child.Value).From(ctx => ctx.WorkflowData.Value)
                .Output(child => child.Result).To(ctx => ctx.WorkflowData.Result))
            .Build();

        var result = await Engine(services).RunToEndAsync(definition, new ParentData { Value = "from-parent" });

        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(["activity-default", "from-parent"], Probe(services).Seen);
        Assert.Equal("from-parent", ((ParentData)result.WorkflowData!).Result);
    }

    [Fact]
    public async Task InvokeRunsWithoutSetupInACatchAndAfterASaga()
    {
        using var services = Services();
        await RegisterChildAsync(services);
        var definition = Workflow.Create<ParentData>("InvokeWithoutSetupInScopes")
            .Step<RecordActivity>()
            .Try(attempt => attempt.Step<FailActivity>())
            .Catch<InvalidOperationException>(handler => handler
                .Invoke<ChildData>("SetupChild", 1))
            .Saga(saga => saga.Step<RecordActivity>())
            .Invoke<ChildData>("SetupChild", 1)
            .Build();

        var result = await Engine(services).RunToEndAsync(definition, new ParentData());

        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(["activity-default", "child-default", "activity-default", "child-default"],
            Probe(services).Seen);
    }

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow(options => options.ReplyTokenKeys = TestReplyKeys.Ring);
        services.RegisterWorkflow<ChildWorkflow>();
        services.AddSingleton<SetupProbe>();
        services.AddTransient<RecordActivity>();
        services.AddTransient<FailActivity>();
        return services.BuildServiceProvider();
    }

    private static IWorkflowClient Engine(IServiceProvider services) =>
        services.GetRequiredService<IWorkflowClient>();

    private static SetupProbe Probe(IServiceProvider services) => services.GetRequiredService<SetupProbe>();

    private static Task RegisterChildAsync(IServiceProvider services)
    {
        var builder = new WorkflowBuilder<ChildData>("SetupChild", 1);
        new ChildWorkflow().Build(builder);
        return services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(builder.Build());
    }

    public sealed class SetupProbe
    {
        private readonly ConcurrentQueue<string> _seen = new();
        public IReadOnlyList<string> Seen => _seen.ToArray();
        public void Record(string value) => _seen.Enqueue(value);
    }

    public sealed class ParentData
    {
        public string Value { get; set; } = "parent-default";
        public string Result { get; set; } = "";
    }

    public sealed class ChildData
    {
        public string Value { get; set; } = "child-default";
        public string Result { get; set; } = "";
    }

    [IxIFlow.Core.Workflow("ChildWorkflow", Version = 1)]
    public sealed class ChildWorkflow : IWorkflow<ChildData>
    {

        public void Build(IWorkflowBuilder<ChildData> builder) =>
            builder.Step<RecordActivity>(setup => setup
                .Input(activity => activity.Value).From(ctx => ctx.WorkflowData.Value)
                .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Result));
    }

    public sealed class RecordActivity(SetupProbe probe) : IAsyncActivity
    {
        public string Value { get; set; } = "activity-default";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.Record(Value);
            return Task.CompletedTask;
        }
    }

    public sealed class FailActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Planned failure");
    }
}
