using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests;

/// <summary>
/// An event sent to a busy instance is stored and taken at its owner's next pass (D-02, D-33), and a
/// published event reaches every instance with an open wait for its key, busy or not (D-13).
/// </summary>
[Trait("Feature", "waits")]
public class EventManagementTests
{
    [Fact]
    [Trait("Spec", "D-02")]
    public async Task ATargetedEventToABusyInstanceIsTakenAtItsNextPass()
    {
        using var services = Services();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var gate = services.GetRequiredService<BusyGate>();
        var repository = services.GetRequiredService<IWorkflowStateRepository>();
        var started = await engine.StartAsync(BusyWorkflow(), new BusyData());
        await BusyAndWaitingAsync(gate, repository, started);

        // The instance is running its activity when the event arrives; the event waits for its next pass.
        var sent = engine.ResumeAsync(started, "approval", new TestEvent { ApprovalStatus = "approved" }, "e1");
        while (await repository.GetEventAsync(started, "e1") == null)
            await Task.Delay(10);
        Assert.False(sent.IsCompleted);
        gate.Release.TrySetResult();

        var resumed = await sent.WaitAsync(TimeSpan.FromSeconds(10));
        EventAssert.Consumed(resumed);
        Assert.Equal(WorkflowExecutionStatus.Success, resumed.Status);
        Assert.Equal("approved", Assert.IsType<BusyData>(resumed.WorkflowData).Status);
    }

    [Fact]
    [Trait("Spec", "D-13")]
    public async Task ACorrelatedEventReachesEveryMatchingInstanceIncludingBusyOnes()
    {
        using var services = Services();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var gate = services.GetRequiredService<BusyGate>();
        var repository = services.GetRequiredService<IWorkflowStateRepository>();
        var idle = (await engine.RunAsync(IdleWorkflow(), new BusyData())).InstanceId;
        var busy = await engine.StartAsync(BusyWorkflow(), new BusyData());
        await BusyAndWaitingAsync(gate, repository, busy);

        var deliveries = await engine.PublishAsync("approval", new TestEvent { ApprovalStatus = "approved" }, "e1");

        Assert.Equal(
            new[] { new EventDelivery(idle, EventAppendStatus.Stored), new EventDelivery(busy, EventAppendStatus.Stored) }
                .OrderBy(item => item.InstanceId),
            deliveries.OrderBy(item => item.InstanceId));
        Assert.Equal(EventOutcome.Consumed, (await EventAssert.DecidedAsync(repository, idle, "e1")).Outcome);
        // The busy instance's owner is woken by the host and takes its copy while its activity still runs.
        Assert.Equal(EventOutcome.Consumed, (await EventAssert.DecidedAsync(repository, busy, "e1")).Outcome);
        Assert.False(gate.Release.Task.IsCompleted);
        gate.Release.TrySetResult();
        Assert.Equal(WorkflowExecutionStatus.Success,
            (await engine.AwaitAsync(busy, WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(10))).Status);

        // Publishing the same event again reaches nobody twice.
        Assert.Empty(await engine.PublishAsync("approval", new TestEvent { ApprovalStatus = "approved" }, "e1"));
    }

    // The activity is running and the wait in the other branch is saved, so it can take an event.
    private static async Task BusyAndWaitingAsync(BusyGate gate, IWorkflowInbox inbox, string instanceId)
    {
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        while ((await inbox.GetOpenWaitsAsync(new WaitQuery(InstanceId: instanceId))).Count == 0)
            await Task.Delay(10);
    }

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<BusyGate>();
        services.AddTransient<BlockingActivity>();
        services.AddIxIFlow();
        return services.BuildServiceProvider();
    }

    // The wait is open while the parallel branch runs a blocking activity, so the instance is busy.
    private static WorkflowDefinition BusyWorkflow() =>
        Workflow.Create<BusyData>("BusyApproval")
            .Step<NoopActivity>()
            .Parallel(parallel => parallel
                .Do(branch => branch.Step<BlockingActivity>())
                .Do(branch => branch.WaitFor<TestEvent>("approval", configure: wait => wait
                    .Output(reply => reply.ApprovalStatus).To(ctx => ctx.WorkflowData.Status))))
            .Build();

    private static WorkflowDefinition IdleWorkflow() =>
        Workflow.Create<BusyData>("IdleApproval")
            .Step<NoopActivity>()
            .WaitFor<TestEvent>("approval", configure: wait => wait
                .Output(reply => reply.ApprovalStatus).To(ctx => ctx.WorkflowData.Status))
            .Build();

    public sealed class BusyData
    {
        public string Status { get; set; } = "";
    }

    public sealed class BusyGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class NoopActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class BlockingActivity(BusyGate gate) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            gate.Entered.TrySetResult();
            await gate.Release.Task.WaitAsync(cancellationToken);
        }
    }

    // Test event classes
    public class TestEvent
    {
        public string ApprovalStatus { get; set; } = "";
    }
}
