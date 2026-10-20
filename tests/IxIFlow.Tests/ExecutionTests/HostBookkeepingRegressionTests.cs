using System.Collections;
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
public sealed class HostBookkeepingRegressionTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    [Trait("Spec", "B-09")]
    public async Task ManyCommitsWithoutTraceKeepConstantBookkeepingAndIgnoreDuplicateAcks()
    {
        await using var host = new Harness();
        await using var run = await host.AssignAsync();
        CheckpointCommit? first = null;
        for (var index = 0; index < 512; index++)
        {
            run.Instance.WorkflowDataJson = JsonSerializer.Serialize(new Data { Value = index.ToString() });
            var commit = CheckpointCommit.Create(run.Instance, run.Owner.Epoch, run.Instance.Revision);
            first ??= commit;
            var reply = await host.Endpoint.CheckpointAsync(new(run.Identity, commit));
            Assert.Equal(CheckpointReplyKind.Ack, reply.Kind);
            run.Instance.Revision = reply.Revision;
            if (index % 16 == 0)
            {
                Assert.Equal(reply, await host.Endpoint.CheckpointAsync(new(run.Identity, commit)));
                Assert.Equal(CheckpointReplyKind.Ack,
                    (await host.Endpoint.CheckpointAsync(new(run.Identity, first))).Kind);
            }

            var receipt = Receipts(host).Single();
            Assert.Equal(reply.Revision, Field<WorkflowInstance>(receipt, "Instance").Revision);
            Assert.Equal(run.Instance.WorkflowDataJson, Field<WorkflowInstance>(receipt, "Instance").WorkflowDataJson);
            // With tracing disabled, no receipt collection may grow with the number of Acks.
            foreach (var field in receipt.GetType().GetFields(Fields))
                if (field.GetValue(receipt) is IEnumerable entries and not string)
                    Assert.Empty(entries);
            Assert.Empty(Field<IDictionary>(run.Admission, "_outstanding"));
        }
        Assert.Equal(run.Instance.Revision, Field<WorkflowInstance>(run.Admission, "_saved").Revision);
    }

    [Fact]
    public async Task DuplicateAndOlderAcksDoNotRepeatTraceOrReplaceNewerState()
    {
        await using var host = new Harness();
        await using var run = await host.AssignAsync();
        var commits = new List<CheckpointCommit>();
        for (var index = 0; index < 2; index++)
        {
            run.Instance.WorkflowDataJson = JsonSerializer.Serialize(new Data { Value = index.ToString() });
            var commit = CheckpointCommit.Create(run.Instance, run.Owner.Epoch, run.Instance.Revision,
                traceEntries: [new ExecutionTraceEntry { TraceId = index.ToString(),
                    Timestamp = host.Time.GetUtcNow().UtcDateTime, EntryType = TraceEntryType.LoopIteration }]);
            commits.Add(commit);
            run.Instance.Revision = (await host.Endpoint.CheckpointAsync(new(run.Identity, commit))).Revision;
        }
        for (var index = 0; index < 3; index++)
            foreach (var commit in commits.AsEnumerable().Reverse())
                Assert.Equal(CheckpointReplyKind.Ack,
                    (await host.Endpoint.CheckpointAsync(new(run.Identity, commit))).Kind);

        var receipt = Receipts(host).Single();
        Assert.Equal(["0", "1"], Field<List<ExecutionTraceEntry>>(receipt, "Trace").Select(entry => entry.TraceId));
        Assert.Equal(run.Instance.Revision, Field<WorkflowInstance>(receipt, "Instance").Revision);
        Assert.Equal(run.Instance.WorkflowDataJson, Field<WorkflowInstance>(receipt, "Instance").WorkflowDataJson);
        Assert.Equal(run.Instance.Revision, Field<WorkflowInstance>(run.Admission, "_saved").Revision);
    }

    [Fact]
    [Trait("Spec", "B-09")]
    public async Task ManyFinishedInstancesLeaveNoCommitQueuesOnTheHost()
    {
        await using var host = new Harness();
        var definition = Workflow.Create<Data>("ManyInstances").Step<Noop>().Build();
        for (var index = 0; index < 128; index++)
        {
            var result = await host.Operations.StartAsync(definition, new Data(),
                new WorkflowOptions { InstanceId = "finished-" + index }, default).WaitAsync(Patience);
            Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        }
        Assert.Empty(Receipts(host));
        Assert.Empty(Field<IDictionary>(host.Operations, "_assignments"));
        // Inspect retained queue maps as well as the live assignments: a removed assignment must
        // not leave its queue rooted in a separate host dictionary.
        foreach (var field in host.Operations.GetType().GetFields(Fields))
            if (field.FieldType.IsGenericType &&
                field.FieldType.GetGenericArguments().Any(type => type.Name == "CommitQueue"))
                Assert.Empty((IEnumerable)field.GetValue(host.Operations)!);
    }

    [Theory]
    [InlineData("completed", false)]
    [InlineData("completed", true)]
    [InlineData("parked", false)]
    [InlineData("parked", true)]
    [InlineData("idle", false)]
    [InlineData("idle", true)]
    [Trait("Spec", "B-09")]
    public async Task AFinalUncertainReplyIsReplayedAfterRenewalFails(string ending, bool retry)
    {
        await using var host = new Harness();
        var definition = ending switch
        {
            "parked" => Workflow.Create<Data>("LostPark").Step<Noop>()
                .Saga(body => body.Step<Fail>(step => step.OnError<InvalidOperationException>(
                    handler => handler.ThenRetry(new RetryPolicy(TimeSpan.FromHours(1), TimeSpan.FromHours(1), 1, 1)))))
                .Build(),
            "idle" => Workflow.Create<Data>("LostIdle").Step<Noop>().Delay(TimeSpan.FromHours(1)).Build(),
            _ => Workflow.Create<Data>("LostComplete").Step<Noop>().Build()
        };
        var instance = await host.SeedAsync(definition);
        host.Store.LoseFinalReply = true;
        host.Store.ReturnRetry = retry;
        host.Store.CurrentOwner = () => Field<IDictionary>(host.Operations, "_assignments")
            .Values.Cast<WorkflowHost.InstanceOperations.HostedRun>().Single().Owner;

        var result = await host.Operations.RecoverAsync(instance.InstanceId, default).WaitAsync(Patience);

        var saved = (await host.Store.GetWorkflowInstanceAsync(instance.InstanceId))!;
        Assert.Equal([WorkflowCommitStatus.Applied, WorkflowCommitStatus.AlreadyApplied], host.Store.FinalStatuses);
        Assert.Equal(2, host.Store.FinalCommitIds.Count);
        Assert.Single(host.Store.FinalCommitIds.Distinct());
        Assert.Equal(1, host.Store.RejectedRenewals);
        Assert.True(host.Store.LossBeforeReply);
        Assert.Equal(ending == "completed" ? WorkflowExecutionStatus.Success :
            ending == "parked" ? WorkflowExecutionStatus.Scheduled : WorkflowExecutionStatus.Suspended, result.Status);
        Assert.Equal(ending == "completed" ? WorkflowStatus.Completed :
            ending == "parked" ? WorkflowStatus.Running : WorkflowStatus.Suspended, saved.Status);
        Assert.Equal("assigned", Assert.IsType<Data>(result.WorkflowData).Value);
        if (ending != "completed")
            await host.Time.WaitForTimerDueAtAsync(new DateTimeOffset(saved.RunnableAtUtc!.Value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Spec", "B-09")]
    public async Task AfterLossOnlyTheExactUnresolvedCommitCanReachTheStoreAndItRemainsFenced(bool retry)
    {
        await using var host = new Harness();
        await using var run = await host.AssignAsync();
        host.Store.UncertainNext = true;
        host.Store.ReturnRetry = retry;
        var commit = CheckpointCommit.Create(run.Instance, run.Owner.Epoch, run.Instance.Revision, "unresolved");
        Assert.Equal(retry ? CheckpointReplyKind.Retry : CheckpointReplyKind.Unknown,
            (await host.Endpoint.CheckpointAsync(new(run.Identity, commit))).Kind);
        host.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Throws<ExecutionOwnershipLostException>(run.Owner.ThrowIfLost);
        Assert.NotNull(await host.Store.TryAcquireExecutionLeaseAsync(run.Instance.InstanceId, "takeover", TimeSpan.FromHours(1), await StoreTestSupport.ClaimAsync(host.Services)));
        var calls = host.Store.Calls;

        run.Instance.WorkflowDataJson = "{\"Value\":\"changed\"}";
        var changed = CheckpointCommit.Create(run.Instance, run.Owner.Epoch, commit.ExpectedRevision, commit.CommitId);
        var fresh = CheckpointCommit.Create(run.Instance, run.Owner.Epoch, commit.ExpectedRevision);
        foreach (var request in new[] { new Checkpoint(run.Identity, changed), new(run.Identity, fresh),
                     new(Guid.NewGuid(), commit) })
            Assert.Equal(ConflictReason.NotAssigned, (await host.Endpoint.CheckpointAsync(request)).Conflict);
        Assert.Equal(calls, host.Store.Calls);

        var replay = await host.Endpoint.CheckpointAsync(new(run.Identity, commit));
        Assert.Equal(ConflictReason.StaleEpoch, replay.Conflict);
        Assert.Equal(calls + 1, host.Store.Calls);
        Assert.Equal(ConflictReason.NotAssigned, (await host.Endpoint.CheckpointAsync(new(run.Identity, commit))).Conflict);
        Assert.Equal(calls + 1, host.Store.Calls);
    }

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, Fields)!.GetValue(target)!;
    private static IEnumerable<object> Receipts(Harness host) => Field<IDictionary>(host.Operations, "_assignments")
        .Values.Cast<object>().Select(run => Field<object>(run, "Receipt"));

    private sealed class Harness : IAsyncDisposable
    {
        public TimerRecordingClock Time { get; } = new();
        public ScriptedStore Store { get; }
        public ServiceProvider Services { get; }
        public WorkflowHost.InstanceOperations Operations { get; }
        public IExecutionHost Endpoint => new WorkflowHost.InstanceOperations.ExecutionHost(Operations);
        private readonly CancellationWatch _watch;

        public Harness()
        {
            Store = new(Time);
            var services = new ServiceCollection().AddLogging();
            services.AddSingleton<TimeProvider>(Time);
            services.AddSingleton<IWorkflowStateRepository>(Store);
            services.AddIxIFlow();
            services.AddTransient<Noop>();
            services.AddTransient<Fail>();
            Services = services.BuildServiceProvider();
            var control = new RunControl();
            var scheduler = Services.GetRequiredService<WorkflowHost>();
            control.OnScheduled(scheduler.Schedule);
            control.OnUnscheduled(scheduler.Unschedule);
            _watch = new CancellationWatch(Store, Time);
            Operations = new(Services, Services.GetRequiredService<IActivityExecutor>(), Store,
                Services.GetRequiredService<IWorkflowCatalog>(), new InProcessInstanceGate(),
                timeProvider: Time, watch: _watch, control: control,
                limits: new ExecutionLimits { MaxTraceEntriesPerInstance = 0 });
        }

        public async Task<WorkflowInstance> SeedAsync(WorkflowDefinition definition)
        {
            await Services.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(definition);
            var (instance, checkpoint, _) = WorkflowInstances.NewInstance(definition,
                new Data { Value = "assigned" }, new(), Time.GetUtcNow().UtcDateTime);
            instance.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
            await Store.SeedAsync(instance);
            return (await Store.GetWorkflowInstanceAsync(instance.InstanceId))!;
        }

        public async Task<Assignment> AssignAsync()
        {
            var instance = await SeedAsync(Workflow.Create<Data>("Bookkeeping").Step<Noop>().Build());
            var owner = (await WorkflowExecutionLease.TryAcquireAsync(Store, instance,
                ExecutionLeaseSettings.Default, _watch, await StoreTestSupport.ClaimAsync(Services), _ => { }, _ => { }, _ => { }, Time))!;
            var identity = Guid.NewGuid();
            return new(instance, owner, identity, Operations.Admit(instance, owner, identity));
        }

        public async ValueTask DisposeAsync()
        {
            await _watch.DisposeAsync();
            await Services.DisposeAsync();
        }
    }

    private sealed record Assignment(WorkflowInstance Instance, WorkflowExecutionLease Owner, Guid Identity,
        WorkflowHost.InstanceOperations.HostedRun Admission) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Admission.Dispose();
            await Owner.DisposeAsync();
        }
    }

    private sealed class ScriptedStore(TimerRecordingClock time) : FaultInjectingRepository(time)
    {
        public bool LoseFinalReply;
        public bool UncertainNext;
        public bool ReturnRetry;
        public Func<WorkflowExecutionLease>? CurrentOwner;
        public bool LossBeforeReply;
        public int RejectedRenewals;
        public int Calls;
        public readonly List<WorkflowCommitStatus> FinalStatuses = [];
        public readonly List<string> FinalCommitIds = [];

        public override async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            Calls++;
            if (UncertainNext)
            {
                UncertainNext = false;
                return Uncertain();
            }
            var result = await base.CommitWorkflowInstanceAsync(commit);
            if (LoseFinalReply && (commit.Parks || commit.Instance.Status != WorkflowStatus.Running))
            {
                FinalStatuses.Add(result.Status);
                FinalCommitIds.Add(commit.CommitId);
                if (FinalStatuses.Count == 1)
                {
                    var owner = CurrentOwner!();
                    time.Advance(ExecutionLeaseSettings.Default.RenewalInterval);
                    // The renewal task ends only after it delivers the ownership-loss signal.
                    await Field<Task>(owner, "_renewal").WaitAsync(Patience);
                    try { owner.ThrowIfLost(); }
                    catch (ExecutionOwnershipLostException) { LossBeforeReply = true; }
                    return Uncertain();
                }
            }
            return result;
        }

        private WorkflowCommitResult Uncertain() => ReturnRetry
            ? new(WorkflowCommitStatus.Retry, 0)
            : throw new IOException("The commit reply was lost");

        public override async Task<IReadOnlyList<ExecutionLeaseRenewalResult>> RenewExecutionLeasesAsync(IReadOnlyList<ExecutionLeaseRenewal> leases)
        {
            var renewed = await base.RenewExecutionLeasesAsync(leases);
            Interlocked.Add(ref RejectedRenewals, renewed.Count(result => result.Status != ExecutionLeaseRenewalStatus.Renewed));
            return renewed;
        }
    }

    public sealed class Data { public string Value { get; set; } = ""; }
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public sealed class Fail : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("retry later");
    }
}
