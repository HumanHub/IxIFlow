using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// A checkpoint keeps only what can still run, and a checkpoint over the size limit is never saved:
/// the instance stops at NeedsResolution with its last checkpoint until an operator acts (spec A-08, D-28).
/// </summary>
[Trait("Feature", "durability")]
[Trait("Spec", "A-08")]
public sealed class CheckpointLimitTests
{
    private const int GrownLength = 20_000;

    [Fact]
    public async Task FinishedBranchesAndJoinsAreNotSaved()
    {
        var repository = new InMemoryWorkflowStateRepository();
        using var services = Services(repository, ExecutionLimits.DefaultMaxCheckpointBytes);
        var definition = Workflow.Create<LimitData>("PrunedBranches")
            .Step<NoopActivity>()
            .Parallel(parallel => parallel
                .Do(branch => branch.Step<NoopActivity>())
                .Do(branch => branch.Step<NoopActivity>())
                .Do(branch => branch.Step<NoopActivity>()))
            .WaitFor<Approval>("approval")
            .Build();

        var result = await Engine(services).ExecuteWorkflowAsync(definition, new LimitData());

        Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
        var saved = ExecutionCheckpoint.Read((await repository.GetWorkflowInstanceAsync(result.InstanceId))!
            .ExecutionStateJson);
        Assert.Single(saved.Continuations);
        Assert.Empty(saved.Joins);
    }

    [Fact]
    public async Task RunOverTheLimitStopsWithItsLastCheckpointUntilTheLimitIsRaised()
    {
        var repository = new InMemoryWorkflowStateRepository();
        string instanceId;
        using (var limited = Services(repository, 8_000))
        {
            var result = await Engine(limited).ExecuteWorkflowAsync(GrowingWorkflow(), new LimitData());

            Assert.Equal(WorkflowExecutionStatus.NeedsResolution, result.Status);
            Assert.Contains("over the configured limit of 8000 bytes", result.ErrorMessage);
            instanceId = result.InstanceId;
        }

        var stopped = (await repository.GetWorkflowInstanceAsync(instanceId))!;
        Assert.Equal(WorkflowStatus.NeedsResolution, stopped.Status);
        Assert.Null(stopped.NextDueAtUtc);
        Assert.DoesNotContain(new string('x', GrownLength), stopped.WorkflowDataJson);
        Assert.NotNull(ExecutionCheckpoint.Read(stopped.ExecutionStateJson).ResourceStop);
        Assert.DoesNotContain(await repository.GetWorkflowsRequiringRecoveryAsync(),
            instance => instance.InstanceId == instanceId);

        // An operator raises the limit: a host with the higher limit and the same workflow recovers it.
        using var raised = Services(repository, ExecutionLimits.DefaultMaxCheckpointBytes);
        await raised.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(GrowingWorkflow());
        var recovered = await Engine(raised).RecoverWorkflowAsync(instanceId);

        Assert.Equal(WorkflowExecutionStatus.Success, recovered.Status);
        Assert.Equal(GrownLength, ((LimitData)recovered.WorkflowData!).Text.Length);
    }

    [Fact]
    public async Task StartOverTheLimitIsRefusedAndNothingIsSaved()
    {
        var repository = new InMemoryWorkflowStateRepository();
        using var services = Services(repository, 8_000);

        await EngineAssert.FailsAsync(WorkflowErrorKind.ResourceExhausted,
            () => Engine(services).ExecuteWorkflowAsync(GrowingWorkflow("RefusedStart"),
                new LimitData { Text = new string('x', GrownLength) }));

        Assert.Empty(await repository.GetWorkflowInstancesByNameAsync("RefusedStart"));
    }

    private static WorkflowDefinition GrowingWorkflow(string name = "GrowingCheckpoint") =>
        Workflow.Create<LimitData>(name)
            .Step<NoopActivity>()
            .Step<GrowActivity>(setup => setup
                .Output(activity => activity.Text).To(ctx => ctx.WorkflowData.Text))
            .Build();

    private static ServiceProvider Services(IWorkflowStateRepository repository, long maxCheckpointBytes)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(repository);
        services.AddIxIFlow(options => options.MaxCheckpointBytes = maxCheckpointBytes);
        services.AddTransient<NoopActivity>();
        services.AddTransient<GrowActivity>();
        return services.BuildServiceProvider();
    }

    private static IWorkflowEngine Engine(IServiceProvider services) =>
        services.GetRequiredService<IWorkflowEngine>();

    public sealed class LimitData
    {
        public string Text { get; set; } = "";
    }

    public sealed class Approval;

    public sealed class NoopActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>Produces more data than the limit allows; running it again is safe.</summary>
    public sealed class GrowActivity : IRecoverableActivity<bool>
    {
        public string Text { get; private set; } = "";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Text = new string('x', GrownLength);
            return Task.CompletedTask;
        }

        public bool CaptureRecoveryState(IActivityContext context) => true;

        public Task<ActivityRecoveryResult<bool>> RecoverAsync(bool state, IActivityContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ActivityRecoveryResult<bool>>(new ActivityRecoveryResult<bool>.Execute());
    }
}
