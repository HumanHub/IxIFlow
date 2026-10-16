using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// A busy owner learns of new events from the host's watch, not only at its next commit (D-32), and an
/// event forwarded to a child counts as taken only once the child has decided it (D-33).
/// </summary>
[Trait("Feature", "waits")]
public sealed class EventArrivalTests
{
    [Fact]
    [Trait("Spec", "D-02")]
    public async Task AnEventReachesARunThatWaitsForALongActivity()
    {
        using var services = Services();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var gate = services.GetRequiredService<SlowGate>();
        var definition = Workflow.Create<RaceData>("ApprovalOrSlowWork")
            .Step<StartActivity>()
            .Parallel(parallel => parallel.WaitAny()
                .Do(branch => branch.WaitFor<Approval>("approval", configure: wait => wait
                    .Output(approval => approval.Approved).To(ctx => ctx.WorkflowData.Approved)))
                .Do(branch => branch.Step<SlowActivity>()))
            .Build();
        var started = await engine.StartAsync(definition, new RaceData());
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var repository = services.GetRequiredService<IWorkflowStateRepository>();
        while ((await repository.GetOpenWaitsAsync(new WaitQuery(InstanceId: started))).Count == 0)
            await Task.Delay(10);

        // The slow activity never finishes on its own; the approval wins the join and cancels it.
        var approved = await engine.ResumeAsync(started, "approval", new Approval { Approved = true })
            .WaitAsync(TimeSpan.FromSeconds(10));

        EventAssert.Consumed(approved);
        Assert.Equal(WorkflowExecutionStatus.Success, approved.Status);
        Assert.True(Assert.IsType<RaceData>(approved.WorkflowData).Approved);
        Assert.True(gate.Cancelled.Task.IsCompleted);
    }

    [Fact]
    [Trait("Spec", "D-03")]
    public async Task AnEventForAChildThatAnotherHostOwnsStaysPendingUntilItsOwnerDecides()
    {
        var repository = new InMemoryWorkflowStateRepository();
        using var services = Services(repository);
        var engine = services.GetRequiredService<IWorkflowClient>();
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(
            Workflow.Create<ChildData>("ChildWithApproval")
                .Step<StartActivity>()
                .WaitFor<Approval>("approval", configure: wait => wait
                    .Output(approval => approval.Approved).To(ctx => ctx.WorkflowData.Approved))
                .Build());
        var parent = await engine.RunAsync(Workflow.Create<RaceData>("ParentOfChildWithApproval")
            .Step<StartActivity>()
            .Invoke<ChildData>("ChildWithApproval", 1, child => child
                .Output(data => data.Approved).To(ctx => ctx.WorkflowData.Approved))
            .Build(), new RaceData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, parent.Status);
        var childId = await repository.WaitingInstanceAsync("ChildWithApproval", "approval");
        Assert.NotNull(await repository.TryAcquireExecutionLeaseAsync(childId, "other-host", TimeSpan.FromMinutes(1)));

        // The event goes to the child (D-17); its owner has not decided it.
        using var attempt = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            engine.ResumeAsync(childId, "approval", new Approval { Approved = true }, "approval-1", attempt.Token));
        Assert.Equal(EventOutcome.Pending, (await repository.GetEventAsync(childId, "approval-1"))!.Outcome);

        // Once the child is free, it takes the event, and its completion wakes its parent.
        Assert.True(await repository.ReleaseExecutionLeaseAsync(childId, "other-host"));
        await engine.RecoverAsync(childId);
        Assert.Equal(EventOutcome.Consumed,
            (await EventAssert.DecidedAsync(repository, childId, "approval-1")).Outcome);
        var finished = await engine.AwaitAsync(parent.InstanceId, WorkflowAwaitCondition.Finished)
            .WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(Assert.IsType<RaceData>(finished.WorkflowData).Approved);
    }

    private static ServiceProvider Services(IWorkflowStateRepository? repository = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (repository != null)
            services.AddSingleton(repository);
        services.AddSingleton<SlowGate>();
        services.AddTransient<SlowActivity>();
        services.AddIxIFlow();
        return services.BuildServiceProvider();
    }

    public sealed class RaceData
    {
        public bool Approved { get; set; }
    }

    public sealed class ChildData
    {
        public bool Approved { get; set; }
    }

    public sealed class Approval
    {
        public bool Approved { get; set; }
    }

    public sealed class StartActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class SlowGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class SlowActivity(SlowGate gate) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            gate.Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                gate.Cancelled.TrySetResult();
                throw;
            }
        }
    }
}
