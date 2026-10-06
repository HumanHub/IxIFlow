using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

public class StructuredLoopWaitTests
{
    [Fact]
    public void WhileDoExposesCorrectLoopKindToAuthoring()
    {
        var definition = WhileWorkflow();
        Assert.Equal(LoopType.WhileDo, definition.Steps[1].StepMetadata["LoopType"]);
    }

    [Fact]
    public async Task WhileDoCanCompleteMoreThanOneThousandIterations()
    {
        using var services = CreateServices();
        var definition = Workflow.Create<LoopData>("LongFiniteLoop")
            .Step<StartActivity>()
            .WhileDo(ctx => ctx.WorkflowData.Count < 1001,
                body => body.Step<IncrementActivity>(step => step
                    .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Count)
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Count)))
            .Build();

        var result = await services.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new LoopData(),
                new WorkflowOptions { PersistState = false });

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(1001, Data(result).Count);
    }

    [Fact]
    public async Task WhileDo_WaitsOnEachIterationAndDoesNotRepeatCompletedWork()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var started = await engine.ExecuteWorkflowAsync(WhileWorkflow(), new LoopData { Target = 2 });

        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        Assert.Equal(0, Data(started).Count);

        var first = await engine.ResumeWorkflowAsync(started.InstanceId, "approval", new LoopApproval());
        Assert.Equal(WorkflowExecutionStatus.Suspended, first.Status);
        Assert.Equal(1, Data(first).Count);

        var second = await engine.ResumeWorkflowAsync(started.InstanceId, "approval", new LoopApproval());
        Assert.Equal(WorkflowExecutionStatus.Success, second.Status);
        Assert.Equal(2, Data(second).Count);
    }

    [Fact]
    public async Task WhileDo_FalseConditionSkipsTheWait()
    {
        using var services = CreateServices();
        var result = await services.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(WhileWorkflow(), new LoopData { Target = 0 });

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(0, Data(result).Count);
    }

    [Fact]
    public async Task DoWhile_WaitsOnceBeforeCheckingTheCondition()
    {
        using var services = CreateServices();
        var engine = services.GetRequiredService<IWorkflowEngine>();
        var started = await engine.ExecuteWorkflowAsync(DoWhileWorkflow(), new LoopData { Target = 0 });

        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        var completed = await engine.ResumeWorkflowAsync(started.InstanceId, "approval", new LoopApproval());
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal(1, Data(completed).Count);
    }

    [Fact]
    public async Task WhileDo_ResumesANewIterationFromTheSavedPosition()
    {
        var repository = new InMemoryWorkflowStateRepository();
        string instanceId;
        using (var firstProvider = CreateServices(repository))
        {
            var engine = firstProvider.GetRequiredService<IWorkflowEngine>();
            var started = await engine.ExecuteWorkflowAsync(WhileWorkflow(), new LoopData { Target = 2 });
            var first = await engine.ResumeWorkflowAsync(started.InstanceId, "approval", new LoopApproval());
            Assert.Equal(WorkflowExecutionStatus.Suspended, first.Status);
            Assert.Equal(1, Data(first).Count);
            instanceId = started.InstanceId;
        }

        using var secondProvider = CreateServices(repository);
        await secondProvider.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(WhileWorkflow());
        var completed = await secondProvider.GetRequiredService<IWorkflowEngine>()
            .ResumeWorkflowAsync(instanceId, "approval", new LoopApproval());

        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal(2, Data(completed).Count);
    }

    private static WorkflowDefinition WhileWorkflow() => Workflow.Create<LoopData>("LoopApprovals")
        .Step<StartActivity>()
        .WhileDo(ctx => ctx.WorkflowData.Count < ctx.WorkflowData.Target,
            body => body.WaitFor<LoopApproval>("approval")
                .Step<IncrementActivity>(setup => setup
                    .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Count)
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Count)))
        .Build();

    private static WorkflowDefinition DoWhileWorkflow() => Workflow.Create<LoopData>("DoWhileApproval")
        .Step<StartActivity>()
        .DoWhile(body => body.WaitFor<LoopApproval>("approval")
                .Step<IncrementActivity>(setup => setup
                    .Input(activity => activity.Count).From(ctx => ctx.WorkflowData.Count)
                    .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Count)),
            ctx => ctx.WorkflowData.Count < ctx.WorkflowData.Target)
        .Build();

    private static ServiceProvider CreateServices(IWorkflowStateRepository? repository = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        if (repository != null)
            services.AddSingleton(repository);
        return services.BuildServiceProvider();
    }

    private static LoopData Data(WorkflowExecutionResult result) => Assert.IsType<LoopData>(result.WorkflowData);

    public sealed class LoopData
    {
        public int Count { get; set; }
        public int Target { get; set; }
    }

    public sealed class LoopApproval;

    public sealed class StartActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class IncrementActivity : IAsyncActivity
    {
        public int Count { get; set; }
        public int Result { get; private set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Result = Count + 1;
            return Task.CompletedTask;
        }
    }
}
