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
    public async Task StoreFailureWhileCheckingAChildLeavesTheParentRecoverable()
    {
        var time = new FakeTimeProvider();
        var repository = new FaultInjectingRepository();
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
        var registry = services.GetRequiredService<IWorkflowVersionRegistry>();
        await registry.RegisterWorkflowAsync(child);
        await registry.RegisterWorkflowAsync(parent);
        var started = await Engine(services).RunAsync(parent, new ParentData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        var parentId = started.InstanceId;

        repository.FailRead = id => id != parentId;
        time.Advance(TimeSpan.FromMinutes(11));
        await RecoverOnceAsync(services, repository, time);
        Assert.True(repository.FailedReads > 0);
        Assert.NotEqual(WorkflowStatus.Failed, (await repository.GetWorkflowInstanceAsync(parentId))!.Status);

        repository.FailRead = _ => false;
        await RecoverOnceAsync(services, repository, time);
        await RecoverOnceAsync(services, repository, time);
        var saved = await repository.GetWorkflowInstanceAsync(parentId);
        Assert.Equal(WorkflowStatus.Completed, saved!.Status);
        Assert.Contains("\"ChildTimedOut\":true", saved.WorkflowDataJson);
    }

    [Fact]
    public async Task ChildCommitFailureIsNotHandledAsAChildFailure()
    {
        var time = new FakeTimeProvider();
        var repository = new FaultInjectingRepository();
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
        var registry = services.GetRequiredService<IWorkflowVersionRegistry>();
        await registry.RegisterWorkflowAsync(child);
        await registry.RegisterWorkflowAsync(parent);
        repository.FailCommit = instance => instance.WorkflowName == "ClassifiedChild";

        string parentId;
        try
        {
            var result = await Engine(services).RunAsync(parent, new ParentData());
            parentId = result.InstanceId;
        }
        catch (WorkflowEngineException error) when (error.Kind == WorkflowErrorKind.Infrastructure)
        {
            parentId = (await repository.GetWorkflowInstancesByNameAsync("ClassifiedParent")).Single().InstanceId;
        }
        Assert.True(repository.FailedCommits > 0);
        var interrupted = await repository.GetWorkflowInstanceAsync(parentId);
        Assert.DoesNotContain("\"Caught\":true", interrupted!.WorkflowDataJson);
        Assert.NotEqual(WorkflowStatus.Failed, interrupted.Status);

        repository.FailCommit = _ => false;
        time.Advance(TimeSpan.FromMinutes(1));
        await RecoverOnceAsync(services, repository, time);
        await RecoverOnceAsync(services, repository, time);
        var saved = await repository.GetWorkflowInstanceAsync(parentId);
        Assert.Equal(WorkflowStatus.Completed, saved!.Status);
        Assert.DoesNotContain("\"Caught\":true", saved.WorkflowDataJson);
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
