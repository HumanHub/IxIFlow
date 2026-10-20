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

        var result = await Engine(services).RunAsync(definition, new LimitData());

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
            var result = await Engine(limited).RunAsync(GrowingWorkflow(), new LimitData());

            Assert.Equal(WorkflowExecutionStatus.NeedsResolution, result.Status);
            Assert.Contains("over the configured limit of 8000 bytes", result.ErrorMessage);
            instanceId = result.InstanceId;
        }

        var stopped = (await repository.GetWorkflowInstanceAsync(instanceId))!;
        Assert.Equal(WorkflowStatus.NeedsResolution, stopped.Status);
        Assert.Null(stopped.RunnableAtUtc);
        Assert.DoesNotContain(new string('x', GrownLength), stopped.WorkflowDataJson);
        Assert.NotNull(ExecutionCheckpoint.Read(stopped.ExecutionStateJson).OperatorStop);
        Assert.DoesNotContain(await repository.GetRunnableInstancesAsync(new ClaimQuery(Guid.Empty, new Dictionary<string, string>(), 100)),
            instance => instance.InstanceId == instanceId);

        // An operator raises the limit: a host with the higher limit and the same workflow releases it.
        // Recovery alone leaves it stopped.
        using var raised = Services(repository, ExecutionLimits.DefaultMaxCheckpointBytes);
        await raised.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(GrowingWorkflow());
        var stillStopped = await Engine(raised).RecoverAsync(instanceId);
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, stillStopped.Status);
        Assert.Contains("release it", stillStopped.ErrorMessage);
        var recovered = await Engine(raised).ReleaseAsync(instanceId);

        Assert.Equal(WorkflowExecutionStatus.Success, recovered.Status);
        Assert.Equal(GrownLength, ((LimitData)recovered.WorkflowData!).Text.Length);
    }

    [Fact]
    public async Task StopIsSavedEvenWhenTheLastCheckpointIsJustUnderTheLimit()
    {
        var measured = new InMemoryWorkflowStateRepository();
        long stopBytes;
        using (var services = Services(measured, 8_000))
        {
            var first = await Engine(services).RunAsync(GrowingWorkflow(), new LimitData());
            stopBytes = CheckpointInspection.CheckpointBytes(
                (await measured.GetWorkflowInstanceAsync(first.InstanceId))!);
        }

        // The last checkpoint fits, but adding the stop marker does not.
        var repository = new InMemoryWorkflowStateRepository();
        using var tight = Services(repository, stopBytes - 1);
        var result = await Engine(tight).RunAsync(GrowingWorkflow(), new LimitData());

        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, result.Status);
        var stopped = (await repository.GetWorkflowInstanceAsync(result.InstanceId))!;
        Assert.Equal(WorkflowStatus.NeedsResolution, stopped.Status);
        Assert.Contains("over the configured limit", stopped.LastError);
    }

    [Fact]
    public async Task EventsWaitForTheReleaseAndResolutionsAreRefusedWhileStopped()
    {
        var repository = new InMemoryWorkflowStateRepository();
        using var services = Services(repository, 8_000);
        var definition = Workflow.Create<LimitData>("StoppedWithWait")
            .Step<NoopActivity>()
            .Parallel(parallel => parallel
                .Do(branch => branch.WaitFor<Approval>("approval"))
                .Do(branch => branch.Step<GrowActivity>(setup => setup
                    .Output(activity => activity.Text).To(ctx => ctx.WorkflowData.Text))))
            .Build();

        var stopped = await Engine(services).RunAsync(definition, new LimitData());
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, stopped.Status);

        // The event is stored for after the release (D-33); the instance stays stopped.
        var resumed = await Engine(services).ResumeAsync(stopped.InstanceId, "approval", new Approval(),
            "approval-1");
        Assert.Equal(EventOutcome.Pending, resumed.Event!.Outcome);
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, resumed.Status);
        Assert.Contains("stopped at a resource limit", resumed.ErrorMessage);
        var resolved = await Engine(services).ResolveAsync(stopped.InstanceId, "any",
            ActivityResolution.Failed("operator"));
        Assert.Contains("stopped at a resource limit", resolved.ErrorMessage);
        Assert.Equal(WorkflowStatus.NeedsResolution,
            (await repository.GetWorkflowInstanceAsync(stopped.InstanceId))!.Status);
        Assert.Equal(EventOutcome.Pending,
            (await repository.GetEventAsync(stopped.InstanceId, "approval-1"))!.Outcome);

        // The release takes it.
        await Engine(services).ReleaseAsync(stopped.InstanceId);
        Assert.Equal(EventOutcome.Consumed,
            (await repository.GetEventAsync(stopped.InstanceId, "approval-1"))!.Outcome);
    }

    [Fact]
    public async Task ChildRefusedAtTheLimitStopsItsParentForResolution()
    {
        var repository = new InMemoryWorkflowStateRepository();
        using var services = Services(repository, 8_000);
        await services.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(
            Workflow.Create<LimitData>("OversizedChild").Step<NoopActivity>().Build());
        var parent = Workflow.Create<LimitData>("ParentOfOversizedChild")
            .Step<NoopActivity>()
            .Invoke<LimitData>("OversizedChild", 1, setup => setup
                .Input(child => child.Text).From(_ => new string('x', GrownLength)))
            .Build();

        var result = await Engine(services).RunAsync(parent, new LimitData());

        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, result.Status);
        var pending = Assert.Single(await Engine(services).GetPendingActivitiesAsync(result.InstanceId));
        Assert.Matches(@"^ActivityUnresolved \([0-9a-f]{32}\)$", pending.Reason!);
        var raw = Assert.Single(await services.GetRequiredService<IWorkflowDiagnostics>().ReadAsync(result.InstanceId));
        Assert.Contains("The child workflow could not start", raw.Message);
        Assert.Equal(pending.Reason, new InspectionError(raw.ErrorType, raw.DiagnosticId).ToString());
    }

    [Fact]
    public async Task StartOverTheLimitIsRefusedAndNothingIsSaved()
    {
        var repository = new InMemoryWorkflowStateRepository();
        using var services = Services(repository, 8_000);

        await EngineAssert.FailsAsync(WorkflowErrorKind.ResourceExhausted,
            () => Engine(services).RunAsync(GrowingWorkflow("RefusedStart"),
                new LimitData { Text = new string('x', GrownLength) }));

        Assert.Empty(await Task.WhenAll((await repository.QueryInstancesAsync(new InstanceQuery(WorkflowName: "RefusedStart", Limit: int.MaxValue))).Items.Select(async row => (await repository.GetWorkflowInstanceAsync(row.InstanceId))!)));
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
        IxIFlow.Tests.Infrastructure.DiagnosticTestAccess.Allow(services);
        services.AddIxIFlow(options => options.MaxCheckpointBytes = maxCheckpointBytes);
        services.AddTransient<NoopActivity>();
        services.AddTransient<GrowActivity>();
        return services.BuildServiceProvider();
    }

    private static IWorkflowClient Engine(IServiceProvider services) =>
        services.GetRequiredService<IWorkflowClient>();

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
