using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "timers")]
public sealed class WorkflowDeadlineTests
{
    [Fact]
    public async Task ApprovalBeforeDeadlineRunsOnlyTheReceivedPath()
    {
        // The clock never moves, so the deadline cannot pass before the approval.
        using var services = Services(new FakeTimeProvider());
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
        var time = new FakeTimeProvider();
        using var services = Services(time);
        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(ApprovalWorkflow("ApprovalTimedOut"),
            new ApprovalData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        // The host resumes the instance when its deadline passes (C-12).
        time.Advance(TimeSpan.FromMinutes(2));
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
        var time = new FakeTimeProvider();
        var repository = new InMemoryWorkflowStateRepository(time);
        string instanceId;
        using (var first = Services(time, repository))
            instanceId = (await first.GetRequiredService<IWorkflowClient>()
                .RunAsync(ApprovalWorkflow("LateApproval"), new ApprovalData())).InstanceId;
        using var services = Services(time, repository);
        await services.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(ApprovalWorkflow("LateApproval"));
        var engine = services.GetRequiredService<IWorkflowClient>();
        time.Advance(TimeSpan.FromMinutes(2));

        var result = await engine.ResumeAsync(instanceId, "approval",
            new Approval());

        // The store refuses the approval as it arrives, by its own clock (D-35); the deadline's path
        // runs when a host next takes the instance up.
        EventAssert.Rejected(result, EventRejection.WaitTimedOut);
        var recovered = await engine.RecoverAsync(instanceId);
        Assert.Equal("timeout", Assert.IsType<ApprovalData>(recovered.WorkflowData).Outcome);
    }

    [Fact]
    public async Task DueWaitDoesNotDiscardAnEventForAnotherBranch()
    {
        var time = new FakeTimeProvider();
        using var services = Services(time);
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<ApprovalData>("ParallelDeadlineAndApproval")
            .Step<SeedActivity>()
            .Parallel(parallel => parallel
                .Do(branch => branch.WaitFor<Approval>("first", configure: wait => wait
                    .TimeoutAfter(TimeSpan.FromMinutes(1))
                    .OnTimeout(path => path.Step<MarkActivity>(step => step
                        .Input(activity => activity.Value).From(_ => "timeout")
                        .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome)))))
                .Do(branch => branch.WaitFor<Approval>("second")
                    .Step<AfterActivity>(step => step
                        .Output(activity => activity.Done).To(ctx => ctx.WorkflowData.After))))
            .Build();
        var started = await engine.RunAsync(definition, new ApprovalData());
        time.Advance(TimeSpan.FromMinutes(2));

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
        var time = new FakeTimeProvider();
        using var services = Services(time);
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<ApprovalData>("WholeInstanceTimeout")
            .Step<SeedActivity>()
            .WaitFor<Approval>("approval")
            .Build();
        var started = await engine.RunAsync(definition, new ApprovalData(),
            new WorkflowOptions { ExecutionTimeout = TimeSpan.FromMinutes(1) });
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        time.Advance(TimeSpan.FromMinutes(2));

        var result = await engine.AwaitAsync(started.InstanceId, WorkflowAwaitCondition.Finished)
            .WaitAsync(Patience);

        Assert.Equal(WorkflowExecutionStatus.TimedOut, result.Status);
    }

    [Fact]
    public async Task InstanceDeadlineInterruptsAnActiveActivity()
    {
        var time = new TimerRecordingClock();
        using var services = Services(time);
        var engine = services.GetRequiredService<IWorkflowClient>();
        var blocking = services.GetRequiredService<BlockingProbe>();
        var definition = Workflow.Create<ApprovalData>("ActiveExecutionTimeout")
            .Step<BlockingActivity>()
            .Build();

        var deadline = time.GetUtcNow() + TimeSpan.FromSeconds(5);
        var execution = engine.RunAsync(definition, new ApprovalData(),
            new WorkflowOptions { ExecutionTimeout = TimeSpan.FromSeconds(5) });
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        // The run waits for its activity and for the deadline's timer; only that timer can wake it.
        await time.WaitForTimerDueAtAsync(deadline);
        // One step, shorter than a lease renewal, while the run holds its lease.
        time.Advance(TimeSpan.FromSeconds(6));
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(WorkflowExecutionStatus.TimedOut, result.Status);
        Assert.Equal(WorkflowStatus.TimedOut,
            (await services.GetRequiredService<IWorkflowStateRepository>()
                .GetWorkflowInstanceAsync(result.InstanceId))!.Status);
    }

    [Fact]
    public async Task InstanceDeadlinePassingAfterRootCompletionStillCompletes()
    {
        var time = new FakeTimeProvider();
        var timeout = TimeSpan.FromSeconds(5);
        var repository = new DelayRootCompletionRepository(time, timeout + TimeSpan.FromSeconds(1));
        using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<TimeProvider>(time)
            .AddTransient<SeedActivity>()
            .AddIxIFlow().Services
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
        var time = new FakeTimeProvider();
        var repository = new InMemoryWorkflowStateRepository(time);
        string instanceId;
        using (var first = Services(time, repository))
            instanceId = (await first.GetRequiredService<IWorkflowClient>()
                .RunAsync(ApprovalWorkflow("ScannedApproval"), new ApprovalData())).InstanceId;
        using var services = Services(time, repository);
        await services.GetRequiredService<IWorkflowVersionRegistry>()
            .RegisterWorkflowAsync(ApprovalWorkflow("ScannedApproval"));
        Assert.Contains(services.GetServices<IHostedService>(),
            service => service is WorkflowRecoveryService);
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Contains(await repository.GetRunnableInstancesAsync(100),
            instance => instance.InstanceId == instanceId);
        using var scanner = new WorkflowRecoveryService(
            services.GetRequiredService<IServiceScopeFactory>(), repository,
            NullLogger<WorkflowRecoveryService>.Instance, time);

        await scanner.RecoverOnceAsync(CancellationToken.None);

        var saved = await repository.GetWorkflowInstanceAsync(instanceId);
        Assert.Equal(WorkflowStatus.Completed, saved!.Status);
        Assert.Equal("timeout", JsonSerializer.Deserialize<ApprovalData>(saved.WorkflowDataJson.InlineText)!.Outcome);
    }

    [Fact]
    public async Task MemoryRecoveryScanIncludesDueInstanceNeedingResolution()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var instance = new WorkflowInstance
        { ExecutionStateJson = StoreTestSupport.CheckpointJson,
            InstanceId = Guid.NewGuid().ToString("N"),
            Status = WorkflowStatus.NeedsResolution,
            RunnableAtUtc = DateTime.UtcNow.AddMinutes(-1)
        };
        await repository.SeedAsync(instance, TimeSpan.Zero);

        Assert.Contains(await repository.GetRunnableInstancesAsync(100),
            candidate => candidate.InstanceId == instance.InstanceId);
    }

    [Fact]
    public async Task TimeoutExitsOnlyItsContainingSequence()
    {
        var time = new FakeTimeProvider();
        using var services = Services(time);
        var engine = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<ApprovalData>("NestedWaitTimeout")
            .Step<SeedActivity>()
            .Sequence(sequence => sequence
                .WaitFor<Approval>("approval", configure: wait => wait
                    .TimeoutAfter(TimeSpan.FromMinutes(1))
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
        time.Advance(TimeSpan.FromMinutes(2));

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
        var time = new TimerRecordingClock();
        using var services = Services(time);
        var blocking = services.GetRequiredService<BlockingProbe>();
        var definition = Workflow.Create<ApprovalData>("TimeoutFinally")
            .Step<SeedActivity>()
            .Try(body => body.Step<BlockingActivity>())
            .Finally(body => body.Step<AfterActivity>(step => step
                .Output(activity => activity.Done).To(ctx => ctx.WorkflowData.After)))
            .Build();

        var deadline = time.GetUtcNow() + TimeSpan.FromSeconds(5);
        var execution = services.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new ApprovalData(),
                new WorkflowOptions { ExecutionTimeout = TimeSpan.FromSeconds(5) });
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        // The run waits for its activity and for the deadline's timer; only that timer can wake it.
        await time.WaitForTimerDueAtAsync(deadline);
        // One step, shorter than a lease renewal, while the run holds its lease.
        time.Advance(TimeSpan.FromSeconds(6));
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(WorkflowExecutionStatus.TimedOut, result.Status);
        Assert.True(Assert.IsType<ApprovalData>(result.WorkflowData).After);
    }

    [Fact]
    public async Task TimedWaitInFinallyCompletesBusinessCancellation()
    {
        var time = new FakeTimeProvider();
        using var services = Services(time);
        var definition = CleanupWaitWorkflow("CancelCleanupTimeout");
        var engine = services.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(definition, new ApprovalData());
        var cleaning = await engine.CancelAsync(started.InstanceId,
            new CancellationReason { ReasonCode = "operator" });
        Assert.Equal(WorkflowExecutionStatus.Suspended, cleaning.Status);
        time.Advance(TimeSpan.FromMinutes(2));

        var result = await engine.AwaitAsync(started.InstanceId, WorkflowAwaitCondition.Finished)
            .WaitAsync(Patience);

        Assert.Equal(WorkflowExecutionStatus.Cancelled, result.Status);
        Assert.True(Assert.IsType<ApprovalData>(result.WorkflowData).After);
    }

    [Fact]
    public async Task TimedWaitInFinallyCompletesExecutionTimeout()
    {
        var time = new FakeTimeProvider();
        using var services = Services(time);
        var definition = CleanupWaitWorkflow("DeadlineCleanupTimeout", TimeSpan.FromMinutes(10));
        var engine = services.GetRequiredService<IWorkflowClient>();
        var repository = services.GetRequiredService<IWorkflowStateRepository>();
        var started = await engine.RunAsync(definition, new ApprovalData(),
            new WorkflowOptions { ExecutionTimeout = TimeSpan.FromMinutes(1) });

        // The host wakes the instance at its deadline; it then waits in its cleanup wait.
        time.Advance(TimeSpan.FromMinutes(2));
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            var saved = await repository.GetWorkflowInstanceAsync(started.InstanceId);
            if (saved!.Status == WorkflowStatus.Suspended &&
                ExecutionCheckpoint.Read(saved.ExecutionStateJson).TimedOut)
            {
                Assert.False(JsonSerializer.Deserialize<ApprovalData>(saved.WorkflowDataJson.InlineText)!.After);
                break;
            }
            Assert.True(DateTime.UtcNow < deadline, "The instance did not stop in its cleanup wait");
            await Task.Delay(10);
        }

        // The cleanup wait times out and the instance ends.
        time.Advance(TimeSpan.FromMinutes(11));
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
                .TimeoutAfter(cleanupTimeout ?? TimeSpan.FromMinutes(1))
                .OnTimeout(path => path.Step<AfterActivity>(step => step
                    .Output(activity => activity.Done).To(ctx => ctx.WorkflowData.After)))))
            .Build();

    private static WorkflowDefinition ApprovalWorkflow(string name) =>
        Workflow.Create<ApprovalData>(name)
            .Step<SeedActivity>()
            .WaitFor<Approval>("approval", configure: wait => wait
                .TimeoutAfter(TimeSpan.FromMinutes(1))
                .OnTimeout(path => path.Step<MarkActivity>(step => step
                    .Input(activity => activity.Value).From(_ => "timeout")
                    .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome))))
            .Step<MarkActivity>(step => step
                .Input(activity => activity.Value).From(_ => "received")
                .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Outcome))
            .Build();

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private static ServiceProvider Services(TimeProvider time, IWorkflowStateRepository? repository = null)
    {
        var registrations = new ServiceCollection();
        registrations.AddLogging();
        registrations.AddSingleton(time);
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

    // Holds the commit that records a completed root until the execution deadline has passed: it moves
    // the clock past the deadline, in one step shorter than a lease renewal.
    private sealed class DelayRootCompletionRepository(FakeTimeProvider time, TimeSpan delay)
        : IWorkflowStateRepository
    {
        public Task RegisterMemberAsync(MemberRegistration member, CancellationToken cancellationToken = default) =>
            _inner.RegisterMemberAsync(member, cancellationToken);
        public Task<bool> HeartbeatAsync(Guid memberId, IReadOnlyDictionary<string, string> capabilities,
            CancellationToken cancellationToken = default) =>
            _inner.HeartbeatAsync(memberId, capabilities, cancellationToken);
        public Task UnregisterMemberAsync(Guid memberId, CancellationToken cancellationToken = default) =>
            _inner.UnregisterMemberAsync(memberId, cancellationToken);
        public Task<IReadOnlyList<StoreMember>> GetLiveMembersAsync(CancellationToken cancellationToken = default) =>
            _inner.GetLiveMembersAsync(cancellationToken);

        public Task<RetentionResult> PurgeAsync(TimeSpan finishedRetention, TimeSpan tombstoneRetention, int batchSize,
            CancellationToken cancellationToken = default) =>
            _inner.PurgeAsync(finishedRetention, tombstoneRetention, batchSize, cancellationToken);
        public Task<WorkflowTombstone?> GetTombstoneAsync(string instanceId) =>
            _inner.GetTombstoneAsync(instanceId);
        public Task<WorkflowCommitResult> AdmitWorkflowInstanceAsync(CheckpointCommit commit, WorkflowAdmission admission) =>
            _inner.AdmitWorkflowInstanceAsync(commit, admission);
        public Task<WorkflowOwnership?> GetWorkflowOwnershipAsync(string instanceId) =>
            _inner.GetWorkflowOwnershipAsync(instanceId);
        public Task<WorkflowAdmissionRecord?> GetWorkflowAdmissionAsync(string sender, string trigger, string key) =>
            _inner.GetWorkflowAdmissionAsync(sender, trigger, key);

        public Task RegisterPublisherAsync(string name, IReadOnlyCollection<string> eventTypes, bool replayRetained = false) =>
            _inner.RegisterPublisherAsync(name, eventTypes, replayRetained);
        public Task UnregisterPublisherAsync(string name) => _inner.UnregisterPublisherAsync(name);
        public Task<IReadOnlyList<OutboxDelivery>> ClaimDeliveriesAsync(string publisherName, string token, TimeSpan leaseDuration, int limit) =>
            _inner.ClaimDeliveriesAsync(publisherName, token, leaseDuration, limit);
        public Task<bool> RenewDeliveryLeaseAsync(string eventId, string publisherName, string token, TimeSpan leaseDuration) =>
            _inner.RenewDeliveryLeaseAsync(eventId, publisherName, token, leaseDuration);
        public Task<bool> SettleDeliveryAsync(string eventId, string publisherName, string token, OutboxPublishResult result) =>
            _inner.SettleDeliveryAsync(eventId, publisherName, token, result);
        public Task<OutboxDelivery?> GetDeliveryAsync(string eventId, string publisherName) =>
            _inner.GetDeliveryAsync(eventId, publisherName);
        public Task<OutboxPublisherState?> GetPublisherAsync(string publisherName) =>
            _inner.GetPublisherAsync(publisherName);

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

        private readonly InMemoryWorkflowStateRepository _inner = new(time);
        private int _delayed;

        public bool Delayed => Volatile.Read(ref _delayed) != 0;

        public async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            // The whole instance the commit saves: the commit applied to the instance it was based on.
            var instance = commit.ReadInstance(
                commit.ReplacesParts ? null : await _inner.GetWorkflowInstanceAsync(commit.InstanceId));
            if (ExecutionCheckpoint.IsStructured(instance.ExecutionStateJson) &&
                ExecutionCheckpoint.Read(instance.ExecutionStateJson).Continuations[0].Status ==
                    ContinuationStatus.Completed &&
                Interlocked.Exchange(ref _delayed, 1) == 0)
                time.Advance(delay);
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

        public Task<WorkflowInstance?> GetWorkflowInstanceSummaryAsync(string instanceId) =>
            _inner.GetWorkflowInstanceSummaryAsync(instanceId);
        public Task<IEnumerable<WorkflowInstance>> GetChildInstancesAsync(string parentInstanceId) =>
            _inner.GetChildInstancesAsync(parentInstanceId);
        public Task<IReadOnlyDictionary<string, CancellationReason>> GetCancellationRequestsAsync(
            IReadOnlyCollection<string> instanceIds) =>
            StoreTestSupport.ReadCancellationsAsync(instanceIds, GetCancellationRequestAsync);

        public Task<WorkflowInstance?> GetWorkflowInstanceAsync(string instanceId) =>
            _inner.GetWorkflowInstanceAsync(instanceId);
        public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByNameAsync(string workflowName) =>
            _inner.GetWorkflowInstancesByNameAsync(workflowName);
        public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByStatusAsync(WorkflowStatus status) =>
            _inner.GetWorkflowInstancesByStatusAsync(status);
        public Task<IEnumerable<WorkflowInstance>> GetWorkflowInstancesByCorrelationIdAsync(string correlationId) =>
            _inner.GetWorkflowInstancesByCorrelationIdAsync(correlationId);
        public Task<IReadOnlyList<StoredTraceEntry>> GetTraceAsync(string instanceId, long afterSequence = 0,
            int limit = 1000) =>
            _inner.GetTraceAsync(instanceId, afterSequence, limit);
        public Task DeleteWorkflowInstanceAsync(string instanceId, TimeSpan? tombstoneRetention = null) =>
            _inner.DeleteWorkflowInstanceAsync(instanceId, tombstoneRetention);
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
