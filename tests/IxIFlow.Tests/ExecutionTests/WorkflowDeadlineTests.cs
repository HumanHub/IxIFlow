using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "timers")]
public sealed class WorkflowDeadlineTests
{
    [Fact]
    public async Task ApprovalBeforeDeadlineRunsOnlyTheReceivedPath()
    {
        using var services = Services();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(ApprovalWorkflow("ApprovedInTime"),
            new ApprovalData());

        var result = await engine.ResumeAsync(started.InstanceId, "approval",
            new Approval());

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("received", Assert.IsType<ApprovalData>(result.WorkflowData).Outcome);
    }

    [Fact]
    [Trait("Spec", "C-12")]
    public async Task WaitDeadlineRunsTimeoutPathAndSkipsReceivedPath()
    {
        using var services = Services();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(ApprovalWorkflow("ApprovalTimedOut"),
            new ApprovalData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        // The host resumes the instance when its deadline passes (C-12).
        var result = await engine.AwaitAsync(started.InstanceId, WorkflowAwaitCondition.Finished)
            .WaitAsync(Patience);

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("timeout", Assert.IsType<ApprovalData>(result.WorkflowData).Outcome);
    }

    [Fact]
    public async Task LateApprovalCannotBeatItsCommittedDeadline()
    {
        // The host that saved the deadline is gone, so the deadline is still unprocessed when a
        // late approval reaches another host.
        var repository = new InMemoryWorkflowStateRepository();
        string instanceId;
        using (var first = Services(repository))
            instanceId = (await first.GetRequiredService<IWorkflowClient>()
                .RunAsync(ApprovalWorkflow("LateApproval"), new ApprovalData())).InstanceId;
        using var services = Services(repository);
        await services.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(ApprovalWorkflow("LateApproval"));
        var engine = services.GetRequiredService<IWorkflowClient>();
        await Task.Delay(160);

        var result = await engine.ResumeAsync(instanceId, "approval",
            new Approval());

        EventAssert.Rejected(result, EventRejection.WaitTimedOut);
        Assert.Equal("timeout", Assert.IsType<ApprovalData>(result.WorkflowData).Outcome);
    }

    [Fact]
    public async Task DueWaitDoesNotDiscardAnEventForAnotherBranch()
    {
        using var services = Services();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<ApprovalData>("ParallelDeadlineAndApproval")
            .Step<SeedActivity>()
            .Parallel(parallel => parallel
                .Do(branch => branch.WaitFor<Approval>("first", configure: wait => wait
                    .TimeoutAfter(TimeSpan.FromMilliseconds(80))
                    .OnTimeout(path => path.Step<MarkActivity>(step => step
                        .Input(activity => activity.Value).From(_ => "timeout")
                        .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome)))))
                .Do(branch => branch.WaitFor<Approval>("second")
                    .Step<AfterActivity>(step => step
                        .Output(activity => activity.Done).To(ctx => ctx.WorkflowData.After))))
            .Build();
        var started = await engine.RunAsync(definition, new ApprovalData());
        await Task.Delay(160);

        var result = await engine.ResumeAsync(started.InstanceId, "second", new Approval());

        EventAssert.Consumed(result);
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var data = Assert.IsType<ApprovalData>(result.WorkflowData);
        Assert.Equal("timeout", data.Outcome);
        Assert.True(data.After);
    }

    [Fact]
    public async Task InstanceDeadlineIncludesTimeParkedAtWait()
    {
        using var services = Services();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<ApprovalData>("WholeInstanceTimeout")
            .Step<SeedActivity>()
            .WaitFor<Approval>("approval")
            .Build();
        var started = await engine.RunAsync(definition, new ApprovalData(),
            new WorkflowOptions { ExecutionTimeout = TimeSpan.FromMilliseconds(80) });
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        var result = await engine.AwaitAsync(started.InstanceId, WorkflowAwaitCondition.Finished)
            .WaitAsync(Patience);

        Assert.Equal(WorkflowExecutionStatus.TimedOut, result.Status);
    }

    [Fact]
    public async Task InstanceDeadlineInterruptsAnActiveActivity()
    {
        using var services = Services();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var blocking = services.GetRequiredService<BlockingProbe>();
        var definition = Workflow.Create<ApprovalData>("ActiveExecutionTimeout")
            .Step<BlockingActivity>()
            .Build();

        var execution = engine.RunAsync(definition, new ApprovalData(),
            new WorkflowOptions { ExecutionTimeout = TimeSpan.FromMilliseconds(500) });
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(WorkflowExecutionStatus.TimedOut, result.Status);
        Assert.Equal(WorkflowStatus.TimedOut,
            (await services.GetRequiredService<IWorkflowStateRepository>()
                .GetWorkflowInstanceAsync(result.InstanceId))!.Status);
    }

    [Fact]
    public async Task InstanceDeadlinePassingAfterRootCompletionStillCompletes()
    {
        var timeout = TimeSpan.FromMilliseconds(300);
        var repository = new DelayRootCompletionRepository(timeout * 2);
        using var services = new ServiceCollection()
            .AddLogging()
            .AddTransient<SeedActivity>()
            .AddIxIFlow()
            .AddSingleton<IWorkflowStateRepository>(repository)
            .BuildServiceProvider();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<ApprovalData>("DeadlineAfterCompletion")
            .Step<SeedActivity>()
            .Build();

        var result = await engine.RunAsync(definition, new ApprovalData(),
                new WorkflowOptions { ExecutionTimeout = timeout })
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(repository.Delayed);
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(WorkflowStatus.Completed,
            (await repository.GetWorkflowInstanceAsync(result.InstanceId))!.Status);
    }

    [Fact]
    [Trait("Spec", "C-05")]
    public async Task LocalRecoveryServiceWakesAnOverdueWait()
    {
        // The wait was saved by a host that is gone, so only the scan can find it.
        var repository = new InMemoryWorkflowStateRepository();
        string instanceId;
        using (var first = Services(repository))
            instanceId = (await first.GetRequiredService<IWorkflowClient>()
                .RunAsync(ApprovalWorkflow("ScannedApproval"), new ApprovalData())).InstanceId;
        using var services = Services(repository);
        await services.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(ApprovalWorkflow("ScannedApproval"));
        Assert.Contains(services.GetServices<IHostedService>(),
            service => service is WorkflowRecoveryService);
        await Task.Delay(160);
        Assert.Contains(await repository.GetRunnableInstancesAsync(100),
            instance => instance.InstanceId == instanceId);
        using var scanner = new WorkflowRecoveryService(
            services.GetRequiredService<IServiceScopeFactory>(), repository,
            NullLogger<WorkflowRecoveryService>.Instance);

        await scanner.RecoverOnceAsync(CancellationToken.None);

        var saved = await repository.GetWorkflowInstanceAsync(instanceId);
        Assert.Equal(WorkflowStatus.Completed, saved!.Status);
        Assert.Equal("timeout", JsonSerializer.Deserialize<ApprovalData>(saved.WorkflowDataJson)!.Outcome);
    }

    [Fact]
    public async Task MemoryRecoveryScanIncludesDueInstanceNeedingResolution()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var instance = new WorkflowInstance
        {
            InstanceId = Guid.NewGuid().ToString("N"),
            Status = WorkflowStatus.NeedsResolution,
            RunnableAtUtc = DateTime.UtcNow.AddMinutes(-1)
        };
        await repository.SaveWorkflowInstanceAsync(instance);

        Assert.Contains(await repository.GetRunnableInstancesAsync(100),
            candidate => candidate.InstanceId == instance.InstanceId);
    }

    [Fact]
    public async Task TimeoutExitsOnlyItsContainingSequence()
    {
        using var services = Services();
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<ApprovalData>("NestedWaitTimeout")
            .Step<SeedActivity>()
            .Sequence(sequence => sequence
                .WaitFor<Approval>("approval", configure: wait => wait
                    .TimeoutAfter(TimeSpan.FromMilliseconds(80))
                    .OnTimeout(path => path.Step<MarkActivity>(step => step
                        .Input(activity => activity.Value).From(_ => "timeout")
                        .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome))))
                .Step<MarkActivity>(step => step
                    .Input(activity => activity.Value).From(_ => "received")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome)))
            .Step<AfterActivity>(step => step
                .Output(activity => activity.Done).To(ctx => ctx.WorkflowData.After))
            .Build();
        var started = await engine.RunAsync(definition, new ApprovalData());

        var result = await engine.AwaitAsync(started.InstanceId, WorkflowAwaitCondition.Finished)
            .WaitAsync(Patience);

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var data = Assert.IsType<ApprovalData>(result.WorkflowData);
        Assert.Equal("timeout", data.Outcome);
        Assert.True(data.After);
    }

    [Fact]
    public async Task WholeInstanceTimeoutRunsFinally()
    {
        using var services = Services();
        var blocking = services.GetRequiredService<BlockingProbe>();
        var definition = Workflow.Create<ApprovalData>("TimeoutFinally")
            .Step<SeedActivity>()
            .Try(body => body.Step<BlockingActivity>())
            .Finally(body => body.Step<AfterActivity>(step => step
                .Output(activity => activity.Done).To(ctx => ctx.WorkflowData.After)))
            .Build();

        var execution = services.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new ApprovalData(),
                new WorkflowOptions { ExecutionTimeout = TimeSpan.FromMilliseconds(500) });
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(WorkflowExecutionStatus.TimedOut, result.Status);
        Assert.True(Assert.IsType<ApprovalData>(result.WorkflowData).After);
    }

    [Fact]
    public async Task TimedWaitInFinallyCompletesBusinessCancellation()
    {
        using var services = Services();
        var definition = CleanupWaitWorkflow("CancelCleanupTimeout");
        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(definition, new ApprovalData());
        var cleaning = await engine.CancelAsync(started.InstanceId,
            new CancellationReason { ReasonCode = "operator" });
        Assert.Equal(WorkflowExecutionStatus.Suspended, cleaning.Status);

        var result = await engine.AwaitAsync(started.InstanceId, WorkflowAwaitCondition.Finished)
            .WaitAsync(Patience);

        Assert.Equal(WorkflowExecutionStatus.Cancelled, result.Status);
        Assert.True(Assert.IsType<ApprovalData>(result.WorkflowData).After);
    }

    [Fact]
    public async Task TimedWaitInFinallyCompletesExecutionTimeout()
    {
        using var services = Services();
        var definition = CleanupWaitWorkflow("DeadlineCleanupTimeout", TimeSpan.FromMilliseconds(800));
        var engine = services.GetRequiredService<IWorkflowClient>();
        var repository = services.GetRequiredService<IWorkflowStateRepository>();
        var started = await engine.RunAsync(definition, new ApprovalData(),
            new WorkflowOptions { ExecutionTimeout = TimeSpan.FromMilliseconds(80) });

        // The host wakes the instance at its deadline; it then waits in its cleanup wait.
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            var saved = await repository.GetWorkflowInstanceAsync(started.InstanceId);
            if (saved!.Status == WorkflowStatus.Suspended &&
                ExecutionCheckpoint.Read(saved.ExecutionStateJson).TimedOut)
            {
                Assert.False(JsonSerializer.Deserialize<ApprovalData>(saved.WorkflowDataJson)!.After);
                break;
            }
            Assert.True(DateTime.UtcNow < deadline, "The instance did not stop in its cleanup wait");
            await Task.Delay(10);
        }

        // The cleanup wait times out and the instance ends.
        var result = await engine.AwaitAsync(started.InstanceId, WorkflowAwaitCondition.Finished)
            .WaitAsync(Patience);

        Assert.Equal(WorkflowExecutionStatus.TimedOut, result.Status);
        Assert.True(Assert.IsType<ApprovalData>(result.WorkflowData).After);
    }

    private static WorkflowDefinition CleanupWaitWorkflow(string name, TimeSpan? cleanupTimeout = null) =>
        Workflow.Create<ApprovalData>(name)
            .Step<SeedActivity>()
            .Try(body => body.WaitFor<Approval>("start"))
            .Finally(body => body.WaitFor<Approval>("cleanup", configure: wait => wait
                .TimeoutAfter(cleanupTimeout ?? TimeSpan.FromMilliseconds(80))
                .OnTimeout(path => path.Step<AfterActivity>(step => step
                    .Output(activity => activity.Done).To(ctx => ctx.WorkflowData.After)))))
            .Build();

    private static WorkflowDefinition ApprovalWorkflow(string name) =>
        Workflow.Create<ApprovalData>(name)
            .Step<SeedActivity>()
            .WaitFor<Approval>("approval", configure: wait => wait
                .TimeoutAfter(TimeSpan.FromMilliseconds(80))
                .OnTimeout(path => path.Step<MarkActivity>(step => step
                    .Input(activity => activity.Value).From(_ => "timeout")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome))))
            .Step<MarkActivity>(step => step
                .Input(activity => activity.Value).From(_ => "received")
                .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome))
            .Build();

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private static ServiceProvider Services(IWorkflowStateRepository? repository = null)
    {
        var registrations = new ServiceCollection();
        registrations.AddLogging();
        if (repository != null)
            registrations.AddSingleton(repository);
        registrations.AddTransient<SeedActivity>();
        registrations.AddTransient<MarkActivity>();
        registrations.AddSingleton<BlockingProbe>();
        registrations.AddTransient<BlockingActivity>();
        registrations.AddTransient<AfterActivity>();
        registrations.AddIxIFlow();
        return registrations.BuildServiceProvider();
    }

    public sealed class ApprovalData
    {
        public string Outcome { get; set; } = "";
        public bool After { get; set; }
    }

    public sealed class Approval;

    public sealed class SeedActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public sealed class MarkActivity : IAsyncActivity
    {
        public string Value { get; set; } = "";

        public Task ExecuteAsync(IActivityContext context,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public sealed class BlockingActivity : IAsyncActivity
    {
        private readonly BlockingProbe _probe;

        public BlockingActivity(BlockingProbe probe) => _probe = probe;

        public async Task ExecuteAsync(IActivityContext context,
            CancellationToken cancellationToken = default)
        {
            _probe.Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    // Holds the commit that records a completed root until the execution deadline has passed.
    private sealed class DelayRootCompletionRepository(TimeSpan delay) : IWorkflowStateRepository
    {
        public Task<EventAppendResult> AppendEventAsync(WorkflowEvent workflowEvent) =>
            _inner.AppendEventAsync(workflowEvent);
        public Task<InboxPage> GetPendingEventsAsync(string instanceId, long afterSequence, int limit) =>
            _inner.GetPendingEventsAsync(instanceId, afterSequence, limit);
        public Task<StoredWorkflowEvent?> GetEventAsync(string instanceId, string eventId) =>
            _inner.GetEventAsync(instanceId, eventId);
        public Task<StoredWait?> GetWaitAsync(string instanceId, string waitId) =>
            _inner.GetWaitAsync(instanceId, waitId);
        public Task<IReadOnlyDictionary<string, long>> GetInboxSequencesAsync(
            IReadOnlyCollection<string> instanceIds) =>
            _inner.GetInboxSequencesAsync(instanceIds);
        public Task<IReadOnlyList<StoredWait>> GetOpenWaitsAsync(WaitQuery query) =>
            _inner.GetOpenWaitsAsync(query);

        private readonly InMemoryWorkflowStateRepository _inner = new();
        private int _delayed;

        public bool Delayed => Volatile.Read(ref _delayed) != 0;

        public async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            var instance = commit.ReadInstance();
            if (ExecutionCheckpoint.IsStructured(instance.ExecutionStateJson) &&
                ExecutionCheckpoint.Read(instance.ExecutionStateJson).Continuations[0].Status ==
                    ContinuationStatus.Completed &&
                Interlocked.Exchange(ref _delayed, 1) == 0)
                await Task.Delay(delay);
            return await _inner.CommitWorkflowInstanceAsync(commit);
        }

        public Task<bool> RequestCancellationAsync(string instanceId, CancellationReason reason) =>
            _inner.RequestCancellationAsync(instanceId, reason);
        public Task<CancellationReason?> GetCancellationRequestAsync(string instanceId) =>
            _inner.GetCancellationRequestAsync(instanceId);
        public Task<IReadOnlyList<RunnableInstance>> GetRunnableInstancesAsync(int limit) =>
            _inner.GetRunnableInstancesAsync(limit);
        public Task<ExecutionLeaseGrant?> TryAcquireExecutionLeaseAsync(string instanceId, string token, TimeSpan duration) =>
            _inner.TryAcquireExecutionLeaseAsync(instanceId, token, duration);
        public Task<bool> RenewExecutionLeaseAsync(string instanceId, string token, TimeSpan duration) =>
            _inner.RenewExecutionLeaseAsync(instanceId, token, duration);
        public Task<bool> ReleaseExecutionLeaseAsync(string instanceId, string token) =>
            _inner.ReleaseExecutionLeaseAsync(instanceId, token);
        public Task SaveWorkflowInstanceAsync(WorkflowInstance instance) =>
            _inner.SaveWorkflowInstanceAsync(instance);
        public Task<WorkflowInstance?> GetWorkflowInstanceAsync(string instanceId) =>
            _inner.GetWorkflowInstanceAsync(instanceId);
        public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByNameAsync(string workflowName) =>
            _inner.GetWorkflowInstancesByNameAsync(workflowName);
        public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByStatusAsync(WorkflowStatus status) =>
            _inner.GetWorkflowInstancesByStatusAsync(status);
        public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByCorrelationIdAsync(string correlationId) =>
            _inner.GetWorkflowInstancesByCorrelationIdAsync(correlationId);
        public Task DeleteWorkflowInstanceAsync(string instanceId) =>
            _inner.DeleteWorkflowInstanceAsync(instanceId);
    }

    public sealed class BlockingProbe
    {
        public TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class AfterActivity : IAsyncActivity
    {
        public bool Done { get; private set; }

        public Task ExecuteAsync(IActivityContext context,
            CancellationToken cancellationToken = default)
        {
            Done = true;
            return Task.CompletedTask;
        }
    }
}
