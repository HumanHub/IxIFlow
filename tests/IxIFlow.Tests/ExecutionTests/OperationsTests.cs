using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
public sealed class OperationsTests
{
    private static ServiceProvider Services(IWorkflowStateRepository store, Probe? probe = null, bool shortLease = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(store);
        services.AddSingleton(probe ?? new Probe());
        services.AddTransient<Noop>(); services.AddTransient<Block>(); services.AddTransient<FailPrivate>();
        services.AddIxIFlow(options => options.Actor = "default-operator");
        services.AddSingleton(new InProcessInstanceGate());
        if (shortLease) services.AddSingleton(new ExecutionLeaseSettings(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(100)));
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task SearchGetWaitsAndTracePagingExposeOnlyOperatorProjections()
    {
        var store = new InMemoryWorkflowStateRepository();
        await using var services = Services(store);
        var client = services.GetRequiredService<IWorkflowClient>();
        var operations = services.GetRequiredService<IWorkflowOperations>();
        var definition = Workflow.Create<Data>("OperationsRead").Step<Noop>().WaitFor<Approval>("approve").Build();
        var first = await client.RunAsync(definition, new Data { Secret = "private workflow value" });
        var second = await client.RunAsync(definition, new Data());
        var page = await operations.SearchAsync(new InstanceQuery(WorkflowName: definition.Name, Statuses: [WorkflowStatus.Suspended]));
        Assert.Equal(2, page.Items.Count);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => operations.SearchAsync(new InstanceQuery(Limit: 1001)));
        Assert.Equal(WorkflowStatus.Suspended, (await operations.GetAsync(first.InstanceId))!.Status);
        Assert.Null(await operations.GetAsync("unknown"));
        var waits = await operations.GetWaitsAsync([first.InstanceId, second.InstanceId]);
        Assert.Equal(2, waits.Count);
        Assert.All(waits, wait => Assert.Equal("approve", wait.Key));
        Assert.Empty(await operations.GetWaitsAsync([]));
        var json = JsonSerializer.Serialize(new { page, waits });
        foreach (var forbidden in new[] { "private workflow value", "WorkflowData", "ExecutionState", "StackTrace", "WaitId", "Token" })
            Assert.DoesNotContain(forbidden, json);
        var trace = await client.GetTraceAsync(first.InstanceId);
        var reconstructed = new List<ExecutionTraceEntry>();
        long after = 0;
        while (true)
        {
            var part = await operations.GetTraceAsync(first.InstanceId, after, 2);
            if (part.Count == 0) break;
            Assert.All(part, entry => Assert.True(entry.Sequence > after));
            reconstructed.AddRange(part.Select(entry => entry.Entry));
            after = part[^1].Sequence;
        }
        Assert.Equal(JsonSerializer.Serialize(trace), JsonSerializer.Serialize(reconstructed));
        var many = await SeedAsync(store, WorkflowStatus.Suspended);
        var saved = (await store.GetWorkflowInstanceAsync(many.InstanceId))!;
        await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(saved,
            (await StoreTestSupport.LeaseAsync(store, saved.InstanceId)).Epoch, saved.Revision,
            openedWaits: Enumerable.Range(0, 120).Select(index =>
                new OpenedWait(Guid.NewGuid().ToString("N"), WaitKind.Event, "wait-" + index, "approval", null)).ToArray()));
        Assert.Equal(120, (await operations.GetWaitsAsync([many.InstanceId])).Count);
    }

    [Fact]
    public async Task IdleTerminationPreservesSavedDataClosesWaitsSkipsFinallyAndAcknowledgesActor()
    {
        var store = new InMemoryWorkflowStateRepository();
        await using var services = Services(store);
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<Data>("IdleTermination").Step<Noop>()
            .Try(body => body.WaitFor<Approval>("approve"))
            .Finally(body => body.Step<Noop>()).Build();
        var start = await client.RunAsync(definition, new Data { Secret = "kept" });
        var before = (await store.GetWorkflowInstanceAsync(start.InstanceId))!;
        var result = await services.GetRequiredService<IWorkflowOperations>().TerminateAsync(start.InstanceId, "stop", "operator request", "alice");
        Assert.Equal(WorkflowStatus.Terminated, result.Status);
        Assert.Equal(before.Revision + 1, result.Revision);
        Assert.Empty(await store.GetOpenWaitsAsync(new WaitQuery(InstanceId: start.InstanceId)));
        var saved = (await store.GetWorkflowInstanceAsync(start.InstanceId))!;
        Assert.Equal(before.WorkflowDataJson, saved.WorkflowDataJson);
        Assert.Equal(WorkflowControlMode.Terminate, saved.CancellationReason!.Mode);
        Assert.Equal((await store.GetCancellationRequestAsync(start.InstanceId))!.CommandVersion, saved.CancellationReason.CommandVersion);
        var trace = await store.GetTraceAsync(start.InstanceId);
        Assert.Equal("alice", Assert.IsType<CancellationTrace>(Assert.Single(trace, item => item.Entry.EntryType == TraceEntryType.WorkflowTerminated).Entry.Payload).Actor);
        Assert.Equal(1, services.GetRequiredService<Probe>().Noops);
        Assert.Equal(WorkflowExecutionStatus.Terminated, (await client.AwaitAsync(start.InstanceId, WorkflowAwaitCondition.Finished)).Status);
    }

    [Fact]
    public async Task RunningOwnerOnAnotherHostAcknowledgesAtItsNextCommitWithoutCleanup()
    {
        var store = new InMemoryWorkflowStateRepository();
        var probe = new Probe();
        await using var owner = Services(store, probe);
        await using var remote = Services(store);
        var definition = Workflow.Create<Data>("RemoteTermination").Step<Noop>()
            .Try(body => body.Step<Block>()).Finally(body => body.Step<Noop>()).Build();
        var running = owner.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data(), new WorkflowOptions { InstanceId = "remote-owner" });
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var terminating = remote.GetRequiredService<IWorkflowOperations>().TerminateAsync("remote-owner", "stop");
        try
        {
            await UntilAsync(async () => (await store.GetCancellationRequestAsync("remote-owner"))?.Mode == WorkflowControlMode.Terminate);
            Assert.False(terminating.IsCompleted);
            probe.Release.TrySetResult();
            Assert.Equal(WorkflowStatus.Terminated, (await terminating.WaitAsync(TimeSpan.FromSeconds(5))).Status);
            Assert.Equal(WorkflowExecutionStatus.Terminated, (await running.WaitAsync(TimeSpan.FromSeconds(5))).Status);
            Assert.Equal(1, probe.Noops);
        }
        finally { probe.Release.TrySetResult(); }
    }

    [Fact]
    public async Task TerminationUpgradesAnAlreadyAcknowledgedCancellation()
    {
        var store = new InMemoryWorkflowStateRepository();
        await using var services = Services(store);
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<Data>("UpgradeCancellation").Step<Noop>()
            .Try(body => body.WaitFor<Approval>("forward"))
            .Finally(body => body.WaitFor<Approval>("cleanup")).Build();
        var start = await client.RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Suspended, (await client.CancelAsync(start.InstanceId, new CancellationReason { ReasonCode = "graceful" })).Status);
        var previous = (await store.GetWorkflowInstanceAsync(start.InstanceId))!;
        var result = await services.GetRequiredService<IWorkflowOperations>().TerminateAsync(start.InstanceId, "hard");
        Assert.Equal(WorkflowStatus.Terminated, result.Status);
        var command = (await store.GetCancellationRequestAsync(start.InstanceId))!;
        Assert.True(command.CommandVersion > previous.CancellationReason!.CommandVersion);
        Assert.Equal("hard", command.ReasonCode);
        Assert.Equal("default-operator", command.RequestedBy);
        Assert.Empty(await store.GetOpenWaitsAsync(new WaitQuery(InstanceId: start.InstanceId)));
    }

    [Fact]
    public async Task ParentCommitDurablyTerminatesAllNonterminalChildrenIncludingDetachedChildren()
    {
        var store = new InMemoryWorkflowStateRepository();
        await using var services = Services(store);
        var parent = await SeedAsync(store, WorkflowStatus.Suspended);
        var children = new List<string>();
        foreach (var status in new[] { WorkflowStatus.Running, WorkflowStatus.Suspended, WorkflowStatus.NeedsResolution })
            children.Add((await SeedAsync(store, status, parent.InstanceId)).InstanceId);
        var completed = await SeedAsync(store, WorkflowStatus.Completed, parent.InstanceId);
        children.Add((await SeedAsync(store, WorkflowStatus.Suspended, completed.InstanceId)).InstanceId);
        await services.GetRequiredService<IWorkflowOperations>().TerminateAsync(parent.InstanceId, "subtree");
        foreach (var child in children)
        {
            Assert.Equal(WorkflowControlMode.Terminate, (await store.GetCancellationRequestAsync(child))!.Mode);
            var ended = await services.GetRequiredService<IWorkflowClient>().RecoverAsync(child);
            Assert.Equal(WorkflowExecutionStatus.Terminated, ended.Status);
        }
        Assert.Equal(WorkflowStatus.Completed, (await store.GetWorkflowInstanceAsync(completed.InstanceId))!.Status);
    }

    [Fact]
    public async Task TerminatedChildNotifiesItsWaitingParentInTheSameCommit()
    {
        var store = new InMemoryWorkflowStateRepository();
        await using var services = Services(store);
        var parent = await SeedAsync(store, WorkflowStatus.Suspended);
        var full = (await store.GetWorkflowInstanceAsync(parent.InstanceId))!;
        await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(full, (await StoreTestSupport.LeaseAsync(store, full.InstanceId)).Epoch, full.Revision,
            openedWaits: [new OpenedWait("child-wait", WaitKind.Child, "child", "child", null)]));
        var child = await SeedAsync(store, WorkflowStatus.Suspended, parent.InstanceId, "child-wait");
        await services.GetRequiredService<IWorkflowOperations>().TerminateAsync(child.InstanceId, "stop");
        var inbox = await store.GetPendingEventsAsync(parent.InstanceId, 0, 10);
        Assert.Equal(WorkflowStatus.Terminated, JsonSerializer.Deserialize<ChildCompleted>(Assert.Single(inbox.Events).PayloadJson)!.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletionAndTerminationReportTheLosingTransition(bool terminateFirst)
    {
        var store = new InMemoryWorkflowStateRepository();
        var instance = await SeedAsync(store, WorkflowStatus.Running);
        var lease = await StoreTestSupport.LeaseAsync(store, instance.InstanceId);
        var saved = (await store.GetWorkflowInstanceAsync(instance.InstanceId))!;
        saved.Status = WorkflowStatus.Completed;
        var complete = CheckpointCommit.Create(saved, lease.Epoch, saved.Revision);
        if (terminateFirst)
        {
            Assert.True((await store.RequestTerminationAsync(instance.InstanceId, new CancellationReason { ReasonCode = "stop" })).Accepted);
            Assert.Equal(WorkflowCommitStatus.TerminationRequested, (await store.CommitWorkflowInstanceAsync(complete)).Status);
            await StoreTestSupport.ReleaseAsync(store, instance.InstanceId);
            await using var services = Services(store);
            Assert.Equal(WorkflowExecutionStatus.Terminated, (await services.GetRequiredService<IWorkflowClient>().RecoverAsync(instance.InstanceId)).Status);
        }
        else
        {
            Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(complete)).Status);
            Assert.False((await store.RequestTerminationAsync(instance.InstanceId, new CancellationReason { ReasonCode = "stop" })).Accepted);
            await using var services = Services(store);
            await Assert.ThrowsAsync<WorkflowEngineException>(() => services.GetRequiredService<IWorkflowOperations>().TerminateAsync(instance.InstanceId, "stop"));
        }
    }

    [Fact]
    public async Task ExpiredLeaseLetsAnotherHostTerminateAnActivityThatIgnoresCancellation()
    {
        var store = new InMemoryWorkflowStateRepository();
        var probe = new Probe();
        await using var owner = Services(store, probe, shortLease: true);
        await using var remote = Services(store);
        var definition = Workflow.Create<Data>("HungTermination").Step<Block>().Build();
        var running = owner.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data(), new WorkflowOptions { InstanceId = "hung" });
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var stopped = await remote.GetRequiredService<IWorkflowOperations>().TerminateAsync("hung", "hung").WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(WorkflowStatus.Terminated, stopped.Status);
            Assert.False(probe.Release.Task.IsCompleted);
            Assert.False(running.IsCompleted);
        }
        finally { probe.Release.TrySetResult(); Assert.Equal(WorkflowExecutionStatus.Terminated, (await running.WaitAsync(TimeSpan.FromSeconds(6))).Status); }
    }

    [Fact]
    public async Task TerminationReachesAnOwnerWhoseCancellationWatchAlreadyDeliveredGracefulCancellation()
    {
        var store = new InMemoryWorkflowStateRepository();
        var probe = new Probe();
        await using var owner = Services(store, probe, shortLease: true);
        await using var remote = Services(store);
        var client = owner.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<Data>("HungCancellationCleanup").Step<Noop>()
            .Try(body => body.WaitFor<Approval>("forward"))
            .Finally(body => body.Step<Block>()).Build();
        var start = await client.RunAsync(definition, new Data());
        var cancelling = client.CancelAsync(start.InstanceId, new CancellationReason { ReasonCode = "graceful" });
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var stopped = await remote.GetRequiredService<IWorkflowOperations>().TerminateAsync(start.InstanceId, "upgrade")
                .WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(WorkflowStatus.Terminated, stopped.Status);
            Assert.False(probe.Release.Task.IsCompleted);
        }
        finally { probe.Release.TrySetResult(); await cancelling.WaitAsync(TimeSpan.FromSeconds(6)); }
    }

    [Fact]
    public async Task AcceptedTerminationSurvivesCallerCancellationAndIsRecoveredAtLeaseExpiry()
    {
        var clock = new FakeTimeProvider();
        var store = new InMemoryWorkflowStateRepository(clock);
        var instance = await SeedAsync(store, WorkflowStatus.Running);
        Assert.NotNull(await store.TryAcquireExecutionLeaseAsync(instance.InstanceId, "hung-owner", TimeSpan.FromSeconds(10)));
        await using var caller = Services(store);
        using var stopWaiting = new CancellationTokenSource();
        var terminating = caller.GetRequiredService<IWorkflowOperations>().TerminateAsync(instance.InstanceId, "durable", cancellationToken: stopWaiting.Token);
        await UntilAsync(async () => (await store.GetCancellationRequestAsync(instance.InstanceId))?.Mode == WorkflowControlMode.Terminate);
        stopWaiting.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => terminating);
        Assert.Equal(WorkflowStatus.Running, (await store.GetWorkflowInstanceAsync(instance.InstanceId))!.Status);
        clock.Advance(TimeSpan.FromSeconds(11));
        await using var recovery = Services(store);
        Assert.Equal(WorkflowExecutionStatus.Terminated,
            (await recovery.GetRequiredService<IWorkflowClient>().RecoverAsync(instance.InstanceId)).Status);
    }

    [Fact]
    public async Task SagaPolicyTerminationSurvivesAJoinCheckpointWithAnUnrestorableOriginalError()
    {
        var store = new InMemoryWorkflowStateRepository();
        var probe = new Probe();
        await using var services = Services(store, probe);
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<Data>("DurablePolicyTermination").Step<Noop>()
            .Parallel(parallel => parallel
                .Do(branch => branch.Saga(body => body.Step<Block>().Step<FailPrivate>())
                    .OnError<PrivateFailure>(handler => handler.CompensateNone().ThenTerminate()))
                .Do(branch => branch.Try(body => body.WaitFor<Approval>("forward"))
                    .Finally(body => body.WaitFor<Approval>("cleanup"))))
            .Build();
        var id = Guid.NewGuid().ToString("N");
        var running = client.RunAsync(definition, new Data(), new WorkflowOptions { InstanceId = id });
        try
        {
            await UntilAsync(async () => (await store.GetOpenWaitsAsync(new WaitQuery(InstanceId: id, Key: "forward"))).Count > 0);
            probe.Release.TrySetResult();
            Assert.Equal(WorkflowExecutionStatus.Suspended, (await running.WaitAsync(TimeSpan.FromSeconds(5))).Status);
            Assert.Equal(WorkflowExecutionStatus.Terminated, (await client.ResumeAsync(id, "cleanup", new Approval())).Status);
        }
        finally { probe.Release.TrySetResult(); }
    }

    [Fact]
    public async Task LostTerminationAcknowledgementResendsTheSameCommitAndDoesNotDuplicateTheTrace()
    {
        var store = new LostAcknowledgementStore();
        var instance = await SeedAsync(store, WorkflowStatus.Suspended);
        var before = (await store.GetWorkflowInstanceAsync(instance.InstanceId))!.Revision;
        await using var services = Services(store);
        var stopped = await services.GetRequiredService<IWorkflowOperations>().TerminateAsync(instance.InstanceId, "stop");
        Assert.Equal(before + 1, stopped.Revision);
        Assert.Equal(2, store.TerminationCommitIds.Count);
        Assert.Equal(store.TerminationCommitIds[0], store.TerminationCommitIds[1]);
        Assert.Single(await store.GetTraceAsync(instance.InstanceId), item => item.Entry.EntryType == TraceEntryType.WorkflowTerminated);
    }

    [Fact]
    public async Task ChildCompletionBetweenDiscoveryAndTerminalCommitCannotLeaveNewDetachedWorkRunning()
    {
        var store = new CompletingChildStore();
        var parent = await SeedAsync(store, WorkflowStatus.Suspended);
        var child = await SeedAsync(store, WorkflowStatus.Running, parent.InstanceId);
        store.ParentId = parent.InstanceId;
        store.ChildId = child.InstanceId;
        await using var services = Services(store);
        Assert.Equal(WorkflowStatus.Terminated,
            (await services.GetRequiredService<IWorkflowOperations>().TerminateAsync(parent.InstanceId, "stop")).Status);
        Assert.Equal(WorkflowStatus.Completed, (await store.GetWorkflowInstanceAsync(child.InstanceId))!.Status);
        Assert.Equal(WorkflowControlMode.Terminate, (await store.GetCancellationRequestAsync(store.DetachedId!))!.Mode);
        Assert.Equal(WorkflowExecutionStatus.Terminated,
            (await services.GetRequiredService<IWorkflowClient>().RecoverAsync(store.DetachedId!)).Status);
    }

    internal static async Task<WorkflowInstance> SeedAsync(IWorkflowStateRepository store, WorkflowStatus status,
        string? parent = null, string? parentWait = null)
    {
        var instance = new WorkflowInstance { InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "Unregistered",
            WorkflowVersion = 1, Status = status, CreatedAt = DateTime.UtcNow, WorkflowDataJson = "{}",
            WorkflowDataType = "unavailable type", ExecutionStateJson = StoreTestSupport.CheckpointJson,
            ParentInstanceId = parent, ParentWaitId = parentWait };
        var lease = await StoreTestSupport.LeaseAsync(store, instance.InstanceId);
        var result = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, lease.Epoch, 0));
        Assert.Equal(WorkflowCommitStatus.Applied, result.Status);
        await StoreTestSupport.ReleaseAsync(store, instance.InstanceId);
        return instance;
    }

    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await condition()) await Task.Delay(10, stop.Token);
    }

    public sealed class Data { public string Secret { get; set; } = ""; }
    public sealed class Approval;
    public sealed class Probe
    {
        public int Noops;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public sealed class Noop(Probe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref probe.Noops); return Task.CompletedTask; }
    }
    public sealed class Block(Probe probe) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { probe.Started.TrySetResult(); await probe.Release.Task; }
    }
    public sealed class PrivateFailure : Exception
    {
        private PrivateFailure() : base("private failure") { }
        public static PrivateFailure Create() => new();
    }
    public sealed class FailPrivate : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw PrivateFailure.Create();
    }
    private sealed class LostAcknowledgementStore : InMemoryWorkflowStateRepository, IWorkflowStateRepository
    {
        public List<string> TerminationCommitIds { get; } = [];
        public new async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            var result = await base.CommitWorkflowInstanceAsync(commit);
            if (commit.Instance.Status == WorkflowStatus.Terminated)
            {
                TerminationCommitIds.Add(commit.CommitId);
                if (TerminationCommitIds.Count == 1) throw new IOException("Commit applied, acknowledgement lost");
            }
            return result;
        }
    }
    private sealed class CompletingChildStore : InMemoryWorkflowStateRepository, IWorkflowStateRepository
    {
        public string? ParentId;
        public string? ChildId;
        public string? DetachedId;
        public new async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            if (commit.InstanceId == ParentId && commit.Instance.Status == WorkflowStatus.Terminated && DetachedId == null)
            {
                DetachedId = (await SeedAsync(this, WorkflowStatus.Suspended, ChildId)).InstanceId;
                var child = (await GetWorkflowInstanceAsync(ChildId!))!;
                child.Status = WorkflowStatus.Completed;
                await base.CommitWorkflowInstanceAsync(CheckpointCommit.Create(child,
                    (await StoreTestSupport.LeaseAsync(this, child.InstanceId)).Epoch, child.Revision));
            }
            return await base.CommitWorkflowInstanceAsync(commit);
        }
    }
}
