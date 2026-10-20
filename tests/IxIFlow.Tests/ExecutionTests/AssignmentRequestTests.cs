using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "host")]
[Trait("Spec", "A-02")]
public sealed class AssignmentRequestTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task EarlierAssignmentsAndExpiredLeasesRefuseEveryRequestWithoutStoreCalls()
    {
        await using var run = await Assigned.CreateAsync();
        var oldId = run.Identity;
        run.Admission.Dispose();
        var nextId = Guid.NewGuid();
        using var next = run.Operations.Admit(run.Instance, run.Owner, nextId);
        await RefusedAsync(oldId);
        // The clock can expire the lease before its renewal continuation has run.
        run.Time.Advance(TimeSpan.FromHours(2));
        await RefusedAsync(nextId);

        async Task RefusedAsync(Guid identity)
        {
            var calls = run.Probe.RequestCalls;
            var commit = CheckpointCommit.Create(run.Instance, run.Owner.Epoch, run.Instance.Revision);
            Assert.Equal(ConflictReason.NotAssigned,
                (await run.Host.CheckpointAsync(new(identity, commit))).Conflict);
            Assert.Equal(ConflictReason.NotAssigned,
                (await run.Host.StartChildAsync(new(identity, run.Instance.InstanceId, run.Owner.Epoch,
                    "root/0", new("child", "{}", "wait"), "hash"))).Conflict);
            Assert.Equal(ConflictReason.NotAssigned,
                (await run.Host.ReadInboxAsync(new(identity, run.Instance.InstanceId, 0, 10))).Conflict);
            Assert.Equal(ConflictReason.NotAssigned,
                (await run.Host.ReadWaitAsync(new(identity, run.Instance.InstanceId, "wait"))).Conflict);
            Assert.Equal(ConflictReason.NotAssigned, run.Operations.AdmitReady(new(identity,
                new(AssignmentOutcomeKind.Idle), null)));
            Assert.Equal(calls, run.Probe.RequestCalls);
        }
    }

    [Fact]
    public async Task OnlyOutstandingIdenticalCommitsShareAStoreCallAndLaterRequestsKeepOrder()
    {
        var store = new HeldCommitStore();
        await using var run = await Assigned.CreateAsync(store);
        var commit = CheckpointCommit.Create(run.Instance, run.Owner.Epoch, run.Instance.Revision, "same");
        var first = run.Host.CheckpointAsync(new(run.Identity, commit));
        await store.Entered.Task.WaitAsync(Patience);
        var duplicate = run.Host.CheckpointAsync(new(run.Identity, commit));
        Assert.Same(first, duplicate);
        var changed = JsonSerializer.Deserialize<WorkflowInstance>(JsonSerializer.Serialize(run.Instance))!;
        changed.Incarnation = run.Instance.Incarnation;
        changed.WorkflowDataJson = "{\"Value\":\"changed\"}";
        var collision = CheckpointCommit.Create(changed, run.Owner.Epoch, run.Instance.Revision, "same");
        Assert.Equal(ConflictReason.Revision,
            (await run.Host.CheckpointAsync(new(run.Identity, collision))).Conflict);
        var later = run.Host.CheckpointAsync(new(run.Identity,
            CheckpointCommit.Create(run.Instance, run.Owner.Epoch, run.Instance.Revision + 1, "later")));
        Assert.Equal(1, store.Calls);
        store.Release.TrySetResult();
        Assert.Equal(CheckpointReplyKind.Ack, (await first).Kind);
        Assert.Equal(CheckpointReplyKind.Ack, (await duplicate).Kind);
        Assert.Equal(CheckpointReplyKind.Ack, (await later).Kind);
        Assert.Equal(["same", "later"], store.Order);
        // A completed request reaches the store receipt/fencing path again.
        Assert.Equal(CheckpointReplyKind.Ack, (await run.Host.CheckpointAsync(new(run.Identity, commit))).Kind);
        Assert.Equal(3, store.Calls);
    }

    [Fact]
    public async Task ACompletedRunningReceiptIsStillFencedAfterTakeover()
    {
        await using var run = await Assigned.CreateAsync();
        var commit = CheckpointCommit.Create(run.Instance, run.Owner.Epoch, run.Instance.Revision);
        Assert.Equal(CheckpointReplyKind.Ack, (await run.Host.CheckpointAsync(new(run.Identity, commit))).Kind);
        // Another store owner has taken over, even though this host has not observed the loss yet.
        await run.ReleaseOwnerAsync();
        var grant = await run.Store.TryAcquireExecutionLeaseAsync(run.Instance.InstanceId, "takeover", TimeSpan.FromHours(1), await StoreTestSupport.ClaimAsync(run.Services));
        Assert.NotNull(grant);
        Assert.Equal(ConflictReason.NotAssigned, (await run.Host.CheckpointAsync(new(run.Identity, commit))).Conflict);
        Assert.Equal(WorkflowCommitStatus.StaleEpoch, (await run.Store.CommitWorkflowInstanceAsync(commit)).Status);
    }

    [Fact]
    public async Task HostRefusesAnOversizedCheckpointEvenWhenTheExecutorBypassesItsOwnLimit()
    {
        await using var run = await Assigned.CreateAsync(limits: new() { MaxCheckpointBytes = 1_500 });
        run.Instance.WorkflowDataJson = JsonSerializer.Serialize(new Data { Value = new string('x', 5_000) });
        var commit = CheckpointCommit.Create(run.Instance, run.Owner.Epoch, run.Instance.Revision);
        var before = run.Probe.RequestCalls;
        await Assert.ThrowsAsync<CheckpointLimitExceededException>(() => run.Host.CheckpointAsync(new(run.Identity, commit)));
        Assert.Equal(before, run.Probe.RequestCalls);
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("hash")]
    [InlineData("size")]
    public async Task ChildStartIsValidatedByTheHost(string variant)
    {
        await using var run = await Assigned.CreateAsync(limits: new() { MaxCheckpointBytes = 2_000 });
        var childDefinition = Definition("child-definition");
        await run.Registry.RegisterWorkflowAsync(childDefinition);
        var child = WorkflowInstances.Start(childDefinition,
            new Data { Value = variant == "size" ? new string('x', 5_000) : "input" }, run.Instance, "child", "wait", DateTime.UtcNow);
        var hash = WorkflowDefinitionValidator.ForExecution(childDefinition).StructureHash;
        var reply = await run.Host.StartChildAsync(new(run.Identity, run.Instance.InstanceId, run.Owner.Epoch,
            "root/0", child, variant == "hash" ? "wrong" : hash));
        Assert.Equal(run.Identity, reply.AssignmentId);
        if (variant == "accepted") Assert.Equal(child, reply.Start);
        else
        {
            Assert.Null(reply.Start);
            Assert.Equal(variant == "hash" ? WorkflowErrorKind.ArtifactUnavailable : WorkflowErrorKind.ResourceExhausted, reply.Refusal);
        }
        // A preparation request never creates a child: only the parent's checkpoint can do that.
        Assert.Null(await run.Store.GetWorkflowInstanceAsync("child"));
    }

    [Fact]
    public async Task InboxAndWaitReadsUseAssignedRequestsAndKeepTheStorePage()
    {
        await using var run = await Assigned.CreateAsync();
        var opened = new OpenedWait("wait", WaitKind.Event, "go", IxIFlow.Core.Runtime.WorkflowTypeIdentity.SavedName(typeof(Go)), null);
        var commit = CheckpointCommit.Create(run.Instance, run.Owner.Epoch, run.Instance.Revision,
            openedWaits: [opened]);
        Assert.Equal(CheckpointReplyKind.Ack, (await run.Host.CheckpointAsync(new(run.Identity, commit))).Kind);
        await run.Store.AppendEventAsync(new WorkflowEvent(run.Instance.InstanceId, "event",
            IxIFlow.Core.Runtime.WorkflowTypeIdentity.SavedName(typeof(Go)), "{}", "go"));
        var page = await run.Host.ReadInboxAsync(new(run.Identity, run.Instance.InstanceId, 0, 1));
        Assert.Equal(run.Identity, page.AssignmentId);
        Assert.Equal("event", Assert.Single(page.Page!.Events).EventId);
        Assert.NotNull(page.Page.StoreTimeUtc);
        var wait = await run.Host.ReadWaitAsync(new(run.Identity, run.Instance.InstanceId, "wait"));
        Assert.Equal(run.Identity, wait.AssignmentId);
        Assert.True(wait.Wait!.IsOpen);
        Assert.Null((await run.Host.ReadWaitAsync(new(run.Identity, run.Instance.InstanceId, "absent"))).Wait);
    }

    [Fact]
    public async Task UnknownEndsInterruptedWithTheLastAckAndRecoveryReadsTheAppliedState()
    {
        var store = new LostRepliesStore();
        using var services = Services(store);
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = Definition("unknown");
        await EngineAssert.StoreFailureAsync(() => client.RunAsync(definition, new Data { Value = "kept" },
            new WorkflowOptions { InstanceId = "uncertain" }));
        Assert.Equal(3, store.Unknowns);
        Assert.Single(store.CommitIds.Distinct());
        var saved = await store.GetWorkflowInstanceAsync("uncertain");
        Assert.Equal(WorkflowStatus.Completed, saved!.Status);
        var reads = 0;
        store.FailRead = _ => { reads++; return false; };
        var recovered = await client.RecoverAsync("uncertain");
        Assert.True(reads > 0);
        Assert.Equal(WorkflowExecutionStatus.Success, recovered.Status);
        Assert.Equal("kept", Assert.IsType<Data>(recovered.WorkflowData).Value);

        var endpoint = new ExecutorHost { UnknownAfterFirst = true };
        var executor = new WorkflowExecutor(services, endpoint);
        var assign = NewAssign(definition);
        var ready = await executor.ExecuteAsync(assign, CancellationToken.None);
        Assert.Equal(assign.AssignmentId, ready.AssignmentId);
        Assert.Equal(AssignmentOutcomeKind.Interrupted, ready.Outcome.Kind);
        Assert.Equal(endpoint.Acknowledged, ready.LastAcknowledgedCommit);
        Assert.NotNull(ready.LastAcknowledgedCommit);
    }

    [Fact]
    public async Task ExecutorAppliesRecoveryReactivationRunningAndCancellation()
    {
        using var services = Services();
        var definition = Definition("preparation");
        await services.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(definition);
        foreach (var cancelled in new[] { false, true })
        {
            var assign = NewAssign(definition);
            var checkpoint = ExecutionCheckpoint.Read(assign.Instance.ExecutionStateJson);
            var root = checkpoint.Continuations[0];
            root.Status = ContinuationStatus.WaitingResolution;
            root.PendingActivity = new ActivityInvocationState { StepId = "root/0",
                Resolution = new ActivityResolutionState { Kind = ActivityResolutionKind.Completed } };
            assign.Instance.Status = WorkflowStatus.NeedsResolution;
            assign.Instance.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
            assign = assign with { Recovery = new([root.Id], cancelled
                ? new CancellationReason { ReasonCode = "pending" } : null) };
            var endpoint = new ExecutorHost();
            var ready = await new WorkflowExecutor(services, endpoint).ExecuteAsync(assign, CancellationToken.None);
            Assert.Equal(AssignmentOutcomeKind.Idle, ready.Outcome.Kind);
            Assert.Equal(cancelled ? WorkflowStatus.Cancelled : WorkflowStatus.Completed, endpoint.Saved!.Status);
            if (cancelled) Assert.Equal("pending", endpoint.Saved.CancellationReason!.ReasonCode);
            else Assert.Equal(WorkflowStatus.Running, endpoint.Statuses[0]);
            Assert.Equal(WorkflowStatus.NeedsResolution, assign.Instance.Status);
        }
    }

    [Fact]
    public async Task NonEmptyBreakpointsAreRefusedBeforeAnyHostRequest()
    {
        using var services = Services();
        var endpoint = new ExecutorHost();
        var assign = NewAssign(Definition("breakpoints")) with { Breakpoints = [new("root/0")] };
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() =>
            new WorkflowExecutor(services, endpoint).ExecuteAsync(assign, CancellationToken.None));
        Assert.Equal(WorkflowErrorKind.InvalidRequest, error.Kind);
        Assert.Empty(endpoint.Statuses);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AChildCompletionUsesItsSavedDataTypeAndAnUnavailableTypeKeepsTheAnswer(bool unavailable)
    {
        var parent = Workflow.Create<Data>("parent-completion").Step<Noop>().Invoke<Data>("child-completion", 1).Build();
        using var services = Services();
        var registry = services.GetRequiredService<IWorkflowCatalog>();
        await registry.RegisterWorkflowAsync(parent);
        await registry.RegisterWorkflowAsync(Definition("child-completion"));
        var endpoint = new ExecutorHost();
        await new WorkflowExecutor(services, endpoint).ExecuteAsync(NewAssign(parent), CancellationToken.None);
        var instance = endpoint.Saved!;
        var checkpoint = ExecutionCheckpoint.Read(instance.ExecutionStateJson);
        var continuation = Assert.Single(checkpoint.Continuations);
        var pending = continuation.PendingActivity!;
        Assert.Equal(typeof(Data).AssemblyQualifiedName, pending.ChildDataType);
        if (unavailable) pending.ChildDataType = "missing-child-data, missing-assembly";
        continuation.Status = ContinuationStatus.Active;
        continuation.AcceptedWait = new AcceptedWaitState { StepId = pending.StepId,
            Event = SerializedValue.From(new ChildCompleted { ChildInstanceId = pending.ChildWorkflowId!,
                Status = WorkflowStatus.Completed, DataJson = "{}" })! };
        checkpoint.Waits.Clear();
        instance.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
        var before = endpoint.Statuses.Count;
        var collection = new ServiceCollection().AddLogging();
        collection.AddIxIFlow();
        collection.AddSingleton<IWorkflowDefinitionSource>(new ParentOnlySource(parent));
        using var restarted = collection.BuildServiceProvider();
        var assignment = new Assign(Guid.NewGuid(), instance, instance.SavedParts, checkpoint.StructureHash,
            2, 0, ExecutionLimits.Default, false, []);
        if (unavailable)
        {
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() =>
                new WorkflowExecutor(restarted, endpoint).ExecuteAsync(assignment, CancellationToken.None));
            Assert.Equal(WorkflowErrorKind.ArtifactUnavailable, error.Kind);
            Assert.Equal(before, endpoint.Statuses.Count);
            Assert.NotNull(ExecutionCheckpoint.Read(instance.ExecutionStateJson).Continuations[0].AcceptedWait);
        }
        else
        {
            var ready = await new WorkflowExecutor(restarted, endpoint).ExecuteAsync(assignment, CancellationToken.None);
            Assert.Equal(AssignmentOutcomeKind.Idle, ready.Outcome.Kind);
            Assert.Equal(WorkflowStatus.Completed, endpoint.Saved!.Status);
        }
    }

    [Fact]
    public async Task AChildRequestThatIsNotAssignedEndsOwnershipWithoutEnteringWorkflowCatch()
    {
        using var services = Services();
        var parent = Workflow.Create<Data>("child-ownership").Step<Noop>()
            .Try(body => body.Invoke<Data>("refused-child", 1))
            .Catch<Exception>(handler => handler.Step<Noop>()).Build();
        var registry = services.GetRequiredService<IWorkflowCatalog>();
        await registry.RegisterWorkflowAsync(parent);
        await registry.RegisterWorkflowAsync(Definition("refused-child"));
        var endpoint = new ExecutorHost { NotAssignedAtChild = true };
        var ready = await new WorkflowExecutor(services, endpoint).ExecuteAsync(NewAssign(parent), CancellationToken.None);
        Assert.Equal(AssignmentOutcomeKind.OwnershipLost, ready.Outcome.Kind);
        Assert.Equal(endpoint.BeforeChild, endpoint.Statuses.Count);
        Assert.DoesNotContain(WorkflowStatus.Completed, endpoint.Statuses);
    }

    private sealed class ParentOnlySource(WorkflowDefinition parent) : IWorkflowDefinitionSource
    {
        public async Task<WorkflowDefinitionLease> AcquireWorkflowDefinitionAsync(string name, int version, ArtifactDigest? digest) =>
            new((await GetWorkflowDefinitionAsync(name, version))!);
        public Task<WorkflowDefinitionLease> ResolveWorkflowDefinitionAsync(string name, int version) =>
            AcquireWorkflowDefinitionAsync(name, version, null);

        public Task<WorkflowDefinition?> GetWorkflowDefinitionAsync(string name, int version)
        {
            Assert.Equal(parent.Name, name);
            Assert.Equal(parent.Version, version);
            return Task.FromResult<WorkflowDefinition?>(parent);
        }
    }

    private static WorkflowDefinition Definition(string name) => Workflow.Create<Data>(name).Step<Noop>().Build();
    private static Assign NewAssign(WorkflowDefinition definition)
    {
        var (instance, checkpoint, scopes) = WorkflowInstances.NewInstance(definition, new Data(), new(), DateTime.UtcNow);
        instance.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
        return new(Guid.NewGuid(), instance, null, scopes.StructureHash, 1, 0, ExecutionLimits.Default, false, []);
    }
    private static ServiceProvider Services(IWorkflowStateRepository? store = null)
    {
        var collection = new ServiceCollection().AddLogging();
        collection.AddIxIFlow();
        if (store != null) collection.AddSingleton(store);
        return collection.BuildServiceProvider();
    }

    private sealed class Assigned : IAsyncDisposable
    {
        public Guid Identity => _identity;
        public FakeTimeProvider Time => _time!;
        public required ServiceProvider Services;
        public required IWorkflowStateRepository Store;
        public required StoreProbe Probe;
        public required WorkflowInstance Instance;
        public required WorkflowExecutionLease Owner;
        public required WorkflowHost.InstanceOperations Operations;
        public required WorkflowHost.InstanceOperations.HostedRun Admission;
        public required CancellationWatch Watch;
        public IWorkflowCatalog Registry => Services.GetRequiredService<IWorkflowCatalog>();
        public IExecutionHost Host => new WorkflowHost.InstanceOperations.ExecutionHost(Operations);
        public static async Task<Assigned> CreateAsync(IWorkflowStateRepository? store = null, ExecutionLimits? limits = null)
        {
            var time = new FakeTimeProvider();
            store ??= new InMemoryWorkflowStateRepository(time);
            var proxy = DispatchProxy.Create<IWorkflowStateRepository, StoreProbe>();
            var probe = (StoreProbe)proxy;
            probe.Inner = store;
            var services = AssignmentRequestTests.Services(proxy);
            var definition = Definition("assigned");
            await services.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(definition);
            var assign = NewAssign(definition);
            await proxy.SeedAsync(assign.Instance);
            if (store is HeldCommitStore held) held.Hold = true;
            var instance = (await proxy.GetWorkflowInstanceAsync(assign.Instance.InstanceId))!;
            var watch = new CancellationWatch(proxy, time);
            var owner = (await WorkflowExecutionLease.TryAcquireAsync(proxy, instance,
                new(TimeSpan.FromHours(1), TimeSpan.FromMinutes(30)), watch, await StoreTestSupport.ClaimAsync(services), _ => { }, _ => { }, _ => { }, time))!;
            var operations = new WorkflowHost.InstanceOperations(services, services.GetRequiredService<IActivityExecutor>(), proxy,
                services.GetRequiredService<IWorkflowCatalog>(), new InProcessInstanceGate(), timeProvider: time, limits: limits);
            var identity = Guid.NewGuid();
            var admission = operations.Admit(instance, owner, identity);
            var result = new Assigned { Services = services, Store = proxy, Probe = probe, Instance = instance,
                Owner = owner, Operations = operations, Admission = admission, Watch = watch };
            result._identity = identity;
            result._time = time;
            return result;
        }
        private Guid _identity;
        private FakeTimeProvider? _time;
        private bool _ownerDisposed;
        public async Task ReleaseOwnerAsync()
        {
            if (_ownerDisposed) return;
            _ownerDisposed = true;
            await Owner.DisposeAsync();
        }
        public async ValueTask DisposeAsync()
        {
            Admission.Dispose();
            await ReleaseOwnerAsync();
            await Watch.DisposeAsync();
            await Services.DisposeAsync();
        }
    }

    public class StoreProbe : DispatchProxy
    {
        public IWorkflowStateRepository Inner = null!;
        private int _requestCalls;
        public int RequestCalls => Volatile.Read(ref _requestCalls);
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name is "CommitWorkflowInstanceAsync" or "GetPendingEventsAsync" or "GetWaitAsync")
                Interlocked.Increment(ref _requestCalls);
            return method.Invoke(Inner, args);
        }
    }

    private sealed class HeldCommitStore : FaultInjectingRepository
    {
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public bool Hold;
        public List<string> Order = [];
        public override async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            // SeedAsync uses an admission commit, before requests are held.
            if (!Hold) return await base.CommitWorkflowInstanceAsync(commit);
            Interlocked.Increment(ref Calls);
            Entered.TrySetResult();
            await Release.Task;
            Order.Add(commit.CommitId);
            return await base.CommitWorkflowInstanceAsync(commit);
        }
    }

    private sealed class LostRepliesStore : FaultInjectingRepository
    {
        public int Unknowns;
        public List<string> CommitIds = [];
        public override async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            var result = await base.CommitWorkflowInstanceAsync(commit);
            if (commit.Instance.Status == WorkflowStatus.Completed)
            {
                Unknowns++;
                CommitIds.Add(commit.CommitId);
                throw new IOException("Applied, but the reply was lost");
            }
            return result;
        }
    }

    private sealed class ExecutorHost : IExecutionHost
    {
        public bool UnknownAfterFirst;
        public bool NotAssignedAtChild;
        public int BeforeChild;
        public WorkflowInstance? Saved;
        public List<WorkflowStatus> Statuses = [];
        public AcknowledgedCommit? Acknowledged;
        public Task<CheckpointReply> CheckpointAsync(Checkpoint request)
        {
            if (UnknownAfterFirst && Acknowledged != null)
                return Task.FromResult(new CheckpointReply(request.AssignmentId, CheckpointReplyKind.Unknown));
            Saved = request.Commit.ReadInstance(Saved);
            Statuses.Add(Saved.Status);
            Acknowledged = new(request.Commit.CommitId, request.Commit.ExpectedRevision + 1);
            return Task.FromResult(new CheckpointReply(request.AssignmentId, CheckpointReplyKind.Ack, Acknowledged.Revision));
        }
        public Task<ReadInboxReply> ReadInboxAsync(ReadInbox request) => Task.FromResult(new ReadInboxReply(request.AssignmentId,
            new InboxPage([], request.AfterSequence, new Dictionary<string, long>())));
        public Task<ReadWaitReply> ReadWaitAsync(ReadWait request) => Task.FromResult(new ReadWaitReply(request.AssignmentId));
        public Task<StartChildReply> StartChildAsync(StartChild request)
        {
            BeforeChild = Statuses.Count;
            return Task.FromResult(NotAssignedAtChild
                ? new StartChildReply(request.AssignmentId, Conflict: ConflictReason.NotAssigned)
                : new StartChildReply(request.AssignmentId, request.Start));
        }
    }
    public sealed class Data { public string Value { get; set; } = ""; }
    public sealed class Go;
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
