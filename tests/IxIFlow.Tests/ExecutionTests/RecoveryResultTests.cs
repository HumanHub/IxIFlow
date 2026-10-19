using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "recovery")]
public sealed class RecoveryResultTests
{
    [Theory]
    [InlineData(WorkflowStatus.Completed, WorkflowExecutionStatus.Success)]
    [InlineData(WorkflowStatus.Failed, WorkflowExecutionStatus.Faulted)]
    [InlineData(WorkflowStatus.Cancelled, WorkflowExecutionStatus.Cancelled)]
    public async Task RecoverFinishedInstanceReturnsItsSavedResult(WorkflowStatus status,
        WorkflowExecutionStatus expected)
    {
        var time = new FakeTimeProvider();
        await using var services = Services(time);
        var client = services.GetRequiredService<IWorkflowClient>();
        var store = services.GetRequiredService<IWorkflowStateRepository>();
        var workflow = Workflow.Create<ResultData>("RecoverFinished" + status).Step<Increment>();
        var definition = status switch
        {
            WorkflowStatus.Failed => workflow.Step<Fail>().Build(),
            WorkflowStatus.Cancelled => workflow.WaitFor<Reply>("reply").Build(),
            _ => workflow.Build()
        };
        var finished = await client.RunAsync(definition, new ResultData { Value = 41 });
        if (status == WorkflowStatus.Cancelled)
            finished = await client.CancelAsync(finished.InstanceId,
                new CancellationReason { ReasonCode = "operator", Description = "Cancelled for recovery test" });
        Assert.Equal(expected, finished.Status);
        var saved = (await store.GetWorkflowInstanceAsync(finished.InstanceId))!;
        Assert.Equal(status, saved.Status);
        if (status == WorkflowStatus.Failed)
        {
            Assert.Contains("saved failure", saved.LastError);
            Assert.False(string.IsNullOrWhiteSpace(saved.LastErrorStackTrace));
        }

        var repeatedStart = await client.RunAsync(definition, new ResultData(),
            new WorkflowOptions { InstanceId = finished.InstanceId });
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var recovered = await client.RecoverAsync(finished.InstanceId);

            Assert.Equal(finished.InstanceId, recovered.InstanceId);
            Assert.Equal(expected, recovered.Status);
            Assert.Equal(repeatedStart.Status, recovered.Status);
            Assert.Equal(42, Assert.IsType<ResultData>(recovered.WorkflowData).Value);
            Assert.Equal(Assert.IsType<ResultData>(repeatedStart.WorkflowData).Value,
                Assert.IsType<ResultData>(recovered.WorkflowData).Value);
            Assert.Equal(saved.LastError, recovered.ErrorMessage);
            Assert.Equal(saved.LastErrorStackTrace, recovered.ErrorStackTrace);
            Assert.Empty(recovered.TraceEntries);
        }

        var released = await client.ReleaseAsync(finished.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.Faulted, released.Status);
        Assert.Equal("The workflow instance is not stopped for an operator", released.ErrorMessage);
        var after = (await store.GetWorkflowInstanceAsync(finished.InstanceId))!;
        Assert.Equal(saved.Revision, after.Revision);
        Assert.Equal(saved.ExecutionStateJson, after.ExecutionStateJson);
        Assert.Equal(saved.WorkflowDataJson, after.WorkflowDataJson);
    }

    [Fact]
    public async Task RecoverSuspendedInstanceWithoutWorkKeepsItsRejection()
    {
        var time = new FakeTimeProvider();
        await using var services = Services(time);
        var client = services.GetRequiredService<IWorkflowClient>();
        var store = services.GetRequiredService<IWorkflowStateRepository>();
        var definition = Workflow.Create<ResultData>("RecoverIdle").Step<Increment>()
            .WaitFor<Reply>("reply").Build();
        var suspended = await client.RunAsync(definition, new ResultData { Value = 41 });
        Assert.Equal(WorkflowExecutionStatus.Suspended, suspended.Status);
        var saved = (await store.GetWorkflowInstanceAsync(suspended.InstanceId))!;

        var recovered = await client.RecoverAsync(suspended.InstanceId);

        Assert.Equal(suspended.InstanceId, recovered.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.Faulted, recovered.Status);
        Assert.Equal("The workflow instance has no interrupted execution to recover", recovered.ErrorMessage);
        var after = (await store.GetWorkflowInstanceAsync(suspended.InstanceId))!;
        Assert.Equal(WorkflowStatus.Suspended, after.Status);
        Assert.Equal(saved.Revision, after.Revision);
        Assert.Equal(saved.ExecutionStateJson, after.ExecutionStateJson);
        Assert.Equal(saved.WorkflowDataJson, after.WorkflowDataJson);
        Assert.Single(await client.GetOpenWaitsAsync(new WaitQuery(InstanceId: suspended.InstanceId)));
    }

    [Fact]
    public async Task RecoverRemovedInstanceKeepsItsTombstoneRefusal()
    {
        var time = new FakeTimeProvider();
        await using var services = Services(time);
        var client = services.GetRequiredService<IWorkflowClient>();
        var store = services.GetRequiredService<IWorkflowStateRepository>();
        var definition = Workflow.Create<ResultData>("RecoverRemoved").Step<Increment>().Build();
        var finished = await client.RunAsync(definition, new ResultData { Value = 41 });
        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        time.Advance(TimeSpan.FromDays(31));
        Assert.Equal(1, (await store.PurgeAsync(TimeSpan.FromDays(30), TimeSpan.FromDays(90), 100)).InstancesRemoved);
        var tombstone = await store.GetTombstoneAsync(finished.InstanceId);
        Assert.NotNull(tombstone);

        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => client.RecoverAsync(finished.InstanceId));

        Assert.Equal(WorkflowErrorKind.InstanceRemoved, error.Kind);
        Assert.Equal(WorkflowExecutionStatus.Removed, await client.GetStatusAsync(finished.InstanceId));
        var removed = await client.AwaitAsync(finished.InstanceId, WorkflowAwaitCondition.Finished);
        Assert.Equal(WorkflowExecutionStatus.Removed, removed.Status);
        Assert.Null(removed.WorkflowData);
        Assert.Null(await store.GetWorkflowInstanceAsync(finished.InstanceId));
        Assert.Equal(tombstone, await store.GetTombstoneAsync(finished.InstanceId));
    }

    private static ServiceProvider Services(FakeTimeProvider time)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<IWorkflowStateRepository>(new InMemoryWorkflowStateRepository(time));
        services.AddIxIFlow();
        return services.BuildServiceProvider();
    }

    public sealed class ResultData { public int Value { get; set; } }
    public sealed class Reply;

    public sealed class Increment : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            ((ResultData)context.WorkflowData).Value++;
            return Task.CompletedTask;
        }
    }

    public sealed class Fail : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("saved failure");
    }
}
