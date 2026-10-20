using System.Reflection;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "host")]
[Trait("Spec", "A-02")]
public sealed class AssignmentResultRegressionTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("completed")]
    [InlineData("parked")]
    [InlineData("idle")]
    [Trait("Spec", "B-08")]
    [Trait("Spec", "C-12")]
    public async Task AReleasingAckKeepsItsOutcomeAfterAnOutstandingRenewalFails(string ending)
    {
        var time = new TimerRecordingClock();
        var store = new ObservedStore(time) { HoldRenewalAtRelease = true };
        await using var run = new Harness(store, time);
        var definition = ending switch
        {
            "parked" => Workflow.Create<Data>("AckPark").Step<Noop>()
                .Saga(body => body.Step<Fail>(step => step.OnError<InvalidOperationException>(
                    handler => handler.ThenRetry(new RetryPolicy(TimeSpan.FromHours(1), TimeSpan.FromHours(1), 1, 1)))))
                .Build(),
            "idle" => Workflow.Create<Data>("AckIdle").Step<Noop>().Delay(TimeSpan.FromHours(1)).Build(),
            _ => Workflow.Create<Data>("AckComplete").Step<Noop>().Build()
        };
        var instance = await run.SeedAsync(definition);
        AfterReleasingAck? seen = null;
        var endpoint = new AfterAckHost(run.Endpoint, async (request, reply) =>
        {
            if (reply.Kind != CheckpointReplyKind.Ack || !Releases(request.Commit, reply)) return;
            // Observed here, asserted after the run, so a failure cannot be absorbed by the host.
            // The renewal was sent before the final commit. Its false reply arrives only after Ack.
            var renewalPending = !store.Renewal.Task.IsCompleted;
            store.Renewal.SetResult(false);
            await store.Renewal.Task;
            time.Advance(TimeSpan.FromMinutes(2));
            seen = new(renewalPending,
                run.Operations.AdmitReady(new(request.AssignmentId, new(AssignmentOutcomeKind.Idle),
                    new(request.Commit.CommitId, reply.Revision))),
                run.Operations.AdmitReady(new(request.AssignmentId, new(AssignmentOutcomeKind.Idle), null)),
                (await run.Endpoint.CheckpointAsync(request)).Conflict,
                (await run.Endpoint.ReadInboxAsync(new(request.AssignmentId, instance.InstanceId, 0, 10))).Conflict,
                (await run.Endpoint.ReadWaitAsync(new(request.AssignmentId, instance.InstanceId, "wait"))).Conflict,
                (await run.Endpoint.StartChildAsync(new(request.AssignmentId, instance.InstanceId, request.Commit.Epoch,
                    "root/0", new("child", "{}", "wait"), "hash"))).Conflict);
        });
        run.UseEndpoint(endpoint);

        var result = await run.Operations.RecoverAsync(instance.InstanceId, default).WaitAsync(Patience);

        var after = Assert.IsType<AfterReleasingAck>(seen);
        Assert.True(after.RenewalPendingAtAck);
        Assert.Null(after.ReadyWithAck);
        Assert.Equal(ConflictReason.NotAssigned, after.ReadyWithoutAck);
        Assert.Equal(ConflictReason.NotAssigned, after.Checkpoint);
        Assert.Equal(ConflictReason.NotAssigned, after.ReadInbox);
        Assert.Equal(ConflictReason.NotAssigned, after.ReadWait);
        Assert.Equal(ConflictReason.NotAssigned, after.StartChild);
        var saved = (await store.GetWorkflowInstanceAsync(instance.InstanceId))!;
        Assert.Equal(1, endpoint.ReleasingAcks);
        Assert.Equal(1, store.HeldRenewals);
        Assert.Equal(ending == "completed" ? WorkflowExecutionStatus.Success :
            ending == "parked" ? WorkflowExecutionStatus.Scheduled : WorkflowExecutionStatus.Suspended, result.Status);
        Assert.Equal(ending == "completed" ? WorkflowStatus.Completed :
            ending == "parked" ? WorkflowStatus.Running : WorkflowStatus.Suspended, saved.Status);
        Assert.Equal("assigned", Assert.IsType<Data>(result.WorkflowData).Value);
        if (ending != "completed")
            await time.WaitForTimerDueAtAsync(new DateTimeOffset(saved.RunnableAtUtc!.Value));
    }

    [Fact]
    [Trait("Spec", "B-12")]
    public async Task ARecoveredInstanceRecycledBeforeItsFirstStepReturnsTheAssignedData()
    {
        var time = new TimerRecordingClock();
        var store = new ObservedStore(time);
        await using var run = new Harness(store, time);
        var instance = await run.SeedAsync(Workflow.Create<Data>("EarlyRecycle").Step<Noop>().Build());
        run.Control.Recycle();
        var before = store.Commits;

        var result = await run.Operations.RecoverAsync(instance.InstanceId, default).WaitAsync(Patience);

        Assert.Equal(WorkflowExecutionStatus.Running, result.Status);
        Assert.Equal("assigned", Assert.IsType<Data>(result.WorkflowData).Value);
        Assert.Equal(0, run.Services.GetRequiredService<Probe>().Steps);
        Assert.Equal(before, store.Commits);
        Assert.Equal(instance.Revision, (await store.GetWorkflowInstanceAsync(instance.InstanceId))!.Revision);
    }

    [Fact]
    [Trait("Spec", "B-08")]
    [Trait("Spec", "B-09")]
    public async Task ReleasingAnOversizedStopSavesItsRecoveryPreparation()
    {
        var time = new TimerRecordingClock();
        var store = new ObservedStore(time);
        await using var run = new Harness(store, time, new ExecutionLimits
            { MaxCheckpointBytes = 1_000, MaxTraceEntriesPerInstance = 100 });
        var instance = await run.SeedAsync(Workflow.Create<Data>("OversizedRelease").Step<Noop>().Build(), stopped: true);
        run.Control.Recycle();

        var result = await run.Operations.ReleaseAsync(instance.InstanceId, "operator", default).WaitAsync(Patience);

        Assert.Equal(WorkflowExecutionStatus.Running, result.Status);
        var saved = (await store.GetWorkflowInstanceAsync(instance.InstanceId))!;
        Assert.Equal(instance.WorkflowDataJson, saved.WorkflowDataJson);
        var checkpoint = ExecutionCheckpoint.Read(saved.ExecutionStateJson);
        Assert.Null(checkpoint.OperatorStop);
        Assert.Equal(ContinuationStatus.Active, Assert.Single(checkpoint.Continuations).Status);
        Assert.True(checkpoint.TraceEntriesRecorded > 0);
        Assert.Equal(0, run.Services.GetRequiredService<Probe>().Steps);
    }

    [Fact]
    [Trait("Spec", "B-09")]
    public async Task AHostSizeRefusalRestoresTheSavedInstanceAndPendingMarks()
    {
        var time = new TimerRecordingClock();
        var store = new ObservedStore(time);
        await using var run = new Harness(store, time);
        var definition = Workflow.Create<Data>("HostSizeRefusal").Step<Noop>().Build();
        var instance = await run.SeedAsync(definition);
        var checkpoint = ExecutionCheckpoint.Read(instance.ExecutionStateJson);
        var dataJson = instance.WorkflowDataJson;
        var stateJson = instance.ExecutionStateJson;
        var stored = instance.Stored;
        var endpoint = new RefusingHost();
        var session = new ExecutionSession(endpoint, Guid.NewGuid(), instance.InstanceId, 1,
            ExecutionLimits.Default, false);
        checkpoint.TakeMarks();
        checkpoint.Touch(checkpoint.Continuations[0]);

        await Assert.ThrowsAsync<CheckpointLimitExceededException>(() => ExecutionCheckpointWriter.CommitAsync(
            session, instance, checkpoint, new Data { Value = "unsaved" },
            WorkflowDefinitionValidator.ForExecution(definition), time.GetUtcNow().UtcDateTime));

        Assert.NotNull(endpoint.Attempt);
        Assert.Equal(dataJson, instance.WorkflowDataJson);
        Assert.Equal(stateJson, instance.ExecutionStateJson);
        Assert.Same(stored, instance.Stored);
        Assert.NotEqual(dataJson, endpoint.Attempt!.ApplyParts(instance.PartsAt(instance.Revision))[CheckpointParts.DataKey]);
        Assert.NotEmpty(checkpoint.TakeMarks().Ids!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Spec", "B-08")]
    public async Task OwnershipLossDoesNotReturnDataOrErrorsFromAReplacementIncarnation(bool missing)
    {
        var time = new TimerRecordingClock();
        var store = new ObservedStore(time);
        await using var run = new Harness(store, time);
        var instance = await run.SeedAsync(Workflow.Create<Data>("ReplacedInstance").Step<Noop>().Build());
        store.Replacement = missing ? null : new WorkflowInstance
        {
            InstanceId = instance.InstanceId, Incarnation = Guid.NewGuid(),
            WorkflowName = "Replacement", WorkflowDataType = typeof(Data).AssemblyQualifiedName!,
            WorkflowDataJson = "not valid JSON", Status = WorkflowStatus.Completed,
            LastError = "replacement error", LastErrorStackTrace = "replacement stack"
        };
        store.LoseOnCommit = true;

        var result = await run.Operations.RecoverAsync(instance.InstanceId, default).WaitAsync(Patience);

        Assert.True(store.Lost);
        Assert.Equal(WorkflowExecutionStatus.Running, result.Status);
        Assert.Null(result.WorkflowData);
        Assert.Contains("ownership was lost", result.ErrorMessage);
        Assert.Null(result.ErrorStackTrace);
        Assert.Null(result.ErrorType);
        Assert.Empty(result.TraceEntries);
    }

    private sealed record AfterReleasingAck(bool RenewalPendingAtAck,
        ConflictReason? ReadyWithAck, ConflictReason? ReadyWithoutAck, ConflictReason? Checkpoint,
        ConflictReason? ReadInbox, ConflictReason? ReadWait, ConflictReason? StartChild);

    private static bool Releases(CheckpointCommit commit, CheckpointReply reply) =>
        (commit.Instance.Status != WorkflowStatus.Running || reply.Parked) &&
        !(reply.UnreadEvents && commit.Instance.Status == WorkflowStatus.Suspended);

    private sealed class Harness : IAsyncDisposable
    {
        public ServiceProvider Services { get; }
        public RunControl Control { get; } = new();
        public WorkflowHost.InstanceOperations Operations { get; }
        public IExecutionHost Endpoint => new WorkflowHost.InstanceOperations.ExecutionHost(Operations);
        private readonly ObservedStore _store;
        private readonly TimerRecordingClock _time;
        private readonly CancellationWatch _watch;

        public Harness(ObservedStore store, TimerRecordingClock time, ExecutionLimits? limits = null)
        {
            _store = store;
            _time = time;
            var services = new ServiceCollection().AddLogging();
            services.AddSingleton<TimeProvider>(time);
            services.AddSingleton<IWorkflowStateRepository>(store);
            services.AddIxIFlow();
            services.AddSingleton<Probe>();
            services.AddTransient<Noop>();
            services.AddTransient<Fail>();
            Services = services.BuildServiceProvider();
            var host = Services.GetRequiredService<WorkflowHost>();
            Control.OnScheduled(host.Schedule);
            Control.OnUnscheduled(host.Unschedule);
            _watch = new CancellationWatch(store, time);
            Operations = new(Services, Services.GetRequiredService<IActivityExecutor>(), store,
                Services.GetRequiredService<IWorkflowCatalog>(), new InProcessInstanceGate(),
                timeProvider: time, limits: limits, control: Control, watch: _watch);
        }

        public void UseEndpoint(IExecutionHost endpoint) =>
            typeof(WorkflowHost.InstanceOperations).GetField("_executor", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Operations, new WorkflowExecutor(Services, endpoint));

        public async Task<WorkflowInstance> SeedAsync(WorkflowDefinition definition, bool stopped = false)
        {
            await Services.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(definition);
            var (instance, checkpoint, _) = WorkflowInstances.NewInstance(definition,
                new Data { Value = "assigned", Padding = stopped ? new string('x', 5_000) : "" }, new(),
                _time.GetUtcNow().UtcDateTime);
            if (stopped)
            {
                instance.Status = WorkflowStatus.NeedsResolution;
                checkpoint.OperatorStop = new() { Kind = OperatorStopKind.Quarantine, Reason = "crashes" };
                checkpoint.Continuations[0].Status = ContinuationStatus.WaitingResolution;
                checkpoint.Continuations[0].PendingActivity = new() { StepId = definition.Steps[0].Id };
            }
            instance.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
            await _store.SeedAsync(instance);
            return (await _store.GetWorkflowInstanceAsync(instance.InstanceId))!;
        }

        public async ValueTask DisposeAsync()
        {
            await _watch.DisposeAsync();
            await Services.DisposeAsync();
        }
    }

    private sealed class ObservedStore(TimerRecordingClock time) : FaultInjectingRepository(time), IWorkflowStateRepository
    {
        public bool HoldRenewalAtRelease;
        public readonly TaskCompletionSource<bool> Renewal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _renewalStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int HeldRenewals;
        public int Commits;
        public bool LoseOnCommit;
        public bool Lost;
        public WorkflowInstance? Replacement;
        private bool _holdRenewal;

        public override async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            Commits++;
            if (LoseOnCommit)
            {
                Lost = true;
                return new(WorkflowCommitStatus.StaleEpoch, commit.ExpectedRevision);
            }
            if (HoldRenewalAtRelease && (commit.Parks || commit.Instance.Status != WorkflowStatus.Running))
            {
                _holdRenewal = true;
                time.Advance(ExecutionLeaseSettings.Default.RenewalInterval);
                await _renewalStarted.Task.WaitAsync(Patience);
            }
            return await base.CommitWorkflowInstanceAsync(commit);
        }

        async Task<IReadOnlyList<ExecutionLeaseRenewalResult>> IWorkflowStateRepository.RenewExecutionLeasesAsync(IReadOnlyList<ExecutionLeaseRenewal> leases)
        {
            if (!_holdRenewal) return await base.RenewExecutionLeasesAsync(leases);
            Interlocked.Increment(ref HeldRenewals);
            _renewalStarted.TrySetResult();
            var renewed = await Renewal.Task;
            return leases.Select(_ => new ExecutionLeaseRenewalResult(renewed ? ExecutionLeaseRenewalStatus.Renewed : ExecutionLeaseRenewalStatus.Refused)).ToArray();
        }

        Task<WorkflowInstance?> IWorkflowStateRepository.GetWorkflowInstanceAsync(string id) =>
            Lost ? Task.FromResult(Replacement) : base.GetWorkflowInstanceAsync(id);
    }

    private sealed class AfterAckHost(IExecutionHost inner, Func<Checkpoint, CheckpointReply, Task> afterAck) : IExecutionHost
    {
        public int ReleasingAcks;
        public async Task<CheckpointReply> CheckpointAsync(Checkpoint request)
        {
            var reply = await inner.CheckpointAsync(request);
            if (reply.Kind == CheckpointReplyKind.Ack && Releases(request.Commit, reply)) ReleasingAcks++;
            await afterAck(request, reply);
            return reply;
        }
        public Task<StartChildReply> StartChildAsync(StartChild request) => inner.StartChildAsync(request);
        public Task<ReadInboxReply> ReadInboxAsync(ReadInbox request) => inner.ReadInboxAsync(request);
        public Task<ReadWaitReply> ReadWaitAsync(ReadWait request) => inner.ReadWaitAsync(request);
    }

    private sealed class RefusingHost : IExecutionHost
    {
        public CheckpointCommit? Attempt;
        public Task<CheckpointReply> CheckpointAsync(Checkpoint request)
        {
            Attempt = request.Commit;
            throw new CheckpointLimitExceededException(5_000, 1_000);
        }
        public Task<StartChildReply> StartChildAsync(StartChild request) => throw new NotSupportedException();
        public Task<ReadInboxReply> ReadInboxAsync(ReadInbox request) => throw new NotSupportedException();
        public Task<ReadWaitReply> ReadWaitAsync(ReadWait request) => throw new NotSupportedException();
    }

    public sealed class Data
    {
        public string Value { get; set; } = "";
        public string Padding { get; set; } = "";
    }
    public sealed class Probe { public int Steps; }
    public sealed class Noop(Probe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.Steps++;
            return Task.CompletedTask;
        }
    }
    public sealed class Fail : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("retry later");
    }
}
