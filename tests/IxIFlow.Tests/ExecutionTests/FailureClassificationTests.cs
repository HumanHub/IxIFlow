using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// Infrastructure failures interrupt an instance and leave it recoverable; only business
/// failures reach Catch, saga handlers or a failed state (spec A-07).
/// </summary>
[Trait("Feature", "recovery")]
[Trait("Spec", "A-07")]
public sealed class FailureClassificationTests
{
    [Fact]
    public async Task StoreFailureWhileAChildTellsItsParentLeavesBothRecoverable()
    {
        var time = new FakeTimeProvider();
        var repository = new FaultInjectingRepository(time);
        using var services = Services(repository, time);
        var child = Workflow.Create<ChildData>("ClassifiedTimedChild")
            .Step<NoopActivity>()
            .WaitFor<Approval>("approval", configure: wait => wait
                .TimeoutAfter(TimeSpan.FromMinutes(10))
                .OnTimeout(path => path.Step<MarkActivity>(step => step
                    .Output(activity => activity.Marked).To(ctx => ctx.WorkflowData.TimedOut))))
            .Build();
        var parent = Workflow.Create<ParentData>("ClassifiedTimedParent")
            .Step<NoopActivity>()
            .Invoke<ChildData>("ClassifiedTimedChild", 1, step => step
                .Output(data => data.TimedOut).To(ctx => ctx.WorkflowData.ChildTimedOut))
            .Build();
        var registry = services.GetRequiredService<IWorkflowCatalog>();
        await registry.RegisterWorkflowAsync(child);
        await registry.RegisterWorkflowAsync(parent);
        var started = await Engine(services).RunAsync(parent, new ParentData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        var parentId = started.InstanceId;
        // The child runs on its own; it waits for its approval before the clock moves. A jump during its first
        // activity would expire its lease, and an activity whose outcome nobody saved waits for an operator.
        var childId = Assert.Single((await repository.QueryInstancesAsync(
            new InstanceQuery(WorkflowName: "ClassifiedTimedChild", Limit: int.MaxValue))).Items).InstanceId;
        using (var patience = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            Assert.Equal(WorkflowExecutionStatus.Suspended,
                (await Engine(services).AwaitAsync(childId, cancellationToken: patience.Token)).Status);

        // The child's terminal commit, which also tells its parent, fails on every attempt: neither is lost.
        repository.FailCommit = instance => instance.InstanceId == childId && instance.Status == WorkflowStatus.Completed;
        time.Advance(TimeSpan.FromMinutes(11));
        // The child's own timer, or the scan, runs it into the failing commit.
        using (var patience = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            while (repository.FailedCommits < 3)
            {
                await RecoverOnceAsync(services, repository, time);
                await Task.Delay(20, patience.Token);
            }
        Assert.Equal(WorkflowStatus.Suspended, (await repository.GetWorkflowInstanceAsync(parentId))!.Status);
        Assert.NotEqual(WorkflowStatus.Failed,
            Assert.Single(await Task.WhenAll((await repository.QueryInstancesAsync(new InstanceQuery(WorkflowName: "ClassifiedTimedChild", Limit: int.MaxValue))).Items.Select(async row => (await repository.GetWorkflowInstanceAsync(row.InstanceId))!))).Status);

        // The host's own timers fired with the clock as well; scan until the parent has finished.
        repository.FailCommit = _ => false;
        WorkflowInstance? saved = null;
        for (var pass = 0; pass < 40 && saved?.Status != WorkflowStatus.Completed; pass++)
        {
            // The scan defers what just failed for a while, on the store's clock.
            time.Advance(TimeSpan.FromMinutes(1));
            await RecoverOnceAsync(services, repository, time);
            saved = await repository.GetWorkflowInstanceAsync(parentId);
            if (saved?.Status != WorkflowStatus.Completed)
                await Task.Delay(50);
        }
        Assert.Equal(WorkflowStatus.Completed, saved!.Status);
        Assert.Contains("\"ChildTimedOut\":true", saved.WorkflowDataJson.InlineText);
    }

    [Fact]
    public async Task ChildCommitFailureIsNotHandledAsAChildFailure()
    {
        var time = new FakeTimeProvider();
        var repository = new FaultInjectingRepository(time);
        using var services = Services(repository, time);
        var child = Workflow.Create<ChildData>("ClassifiedChild")
            .Step<NoopActivity>()
            .Build();
        var parent = Workflow.Create<ParentData>("ClassifiedParent")
            .Step<NoopActivity>()
            .Try(attempt => attempt
                .Invoke<ChildData>("ClassifiedChild", 1))
            .Catch<Exception>(handler => handler
                .Step<MarkActivity>(step => step
                    .Output(activity => activity.Marked).To(ctx => ctx.WorkflowData.Caught)))
            .Build();
        var registry = services.GetRequiredService<IWorkflowCatalog>();
        await registry.RegisterWorkflowAsync(child);
        await registry.RegisterWorkflowAsync(parent);
        repository.FailCommit = instance => instance.WorkflowName == "ClassifiedChild";

        // The child's run fails on its own commits; that is the store's failure, not the child's, so the
        // parent hears nothing and catches nothing.
        var parentId = (await Engine(services).RunAsync(parent, new ParentData())).InstanceId;
        using (var patience = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            while (repository.FailedCommits < 3)
                await Task.Delay(10, patience.Token);
        var interrupted = await repository.GetWorkflowInstanceAsync(parentId);
        Assert.DoesNotContain("\"Caught\":true", interrupted!.WorkflowDataJson.InlineText);
        Assert.Equal(WorkflowStatus.Suspended, interrupted.Status);

        repository.FailCommit = _ => false;
        time.Advance(TimeSpan.FromMinutes(1));
        WorkflowInstance? saved = null;
        for (var pass = 0; pass < 40 && saved?.Status != WorkflowStatus.Completed; pass++)
        {
            await RecoverOnceAsync(services, repository, time);
            saved = await repository.GetWorkflowInstanceAsync(parentId);
            if (saved?.Status != WorkflowStatus.Completed)
                await Task.Delay(50);
        }
        Assert.Equal(WorkflowStatus.Completed, saved!.Status);
        Assert.DoesNotContain("\"Caught\":true", saved.WorkflowDataJson.InlineText);
    }

    private static ServiceProvider Services(IWorkflowStateRepository repository, TimeProvider time)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(time);
        services.AddSingleton(repository);
        services.AddIxIFlow();
        services.AddTransient<NoopActivity>();
        services.AddTransient<MarkActivity>();
        return services.BuildServiceProvider();
    }

    private static IWorkflowClient Engine(IServiceProvider services) =>
        services.GetRequiredService<IWorkflowClient>();

    private static async Task RecoverOnceAsync(IServiceProvider services,
        IWorkflowStateRepository repository, TimeProvider time)
    {
        using var scanner = new WorkflowRecoveryService(services.GetRequiredService<IServiceScopeFactory>(),
            repository, NullLogger<WorkflowRecoveryService>.Instance, time);
        await scanner.RecoverOnceAsync(CancellationToken.None);
    }

    public sealed class ParentData
    {
        public bool ChildTimedOut { get; set; }
        public bool Caught { get; set; }
    }

    public sealed class ChildData
    {
        public bool TimedOut { get; set; }
    }

    public sealed class Approval;

    public sealed class NoopActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    public sealed class MarkActivity : IAsyncActivity
    {
        public bool Marked { get; private set; }

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Marked = true;
            return Task.CompletedTask;
        }
    }
}
