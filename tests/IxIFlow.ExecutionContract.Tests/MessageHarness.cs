using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.ContractTests;

// This assembly has no friend access. Both ends exchange UTF-8 JSON; only the executor end has services
// and definitions, and only the fake host end has a store. The probe coordinates workflow code in tests.
internal sealed class MessageHarness : IDisposable
{
    internal static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private readonly ServiceProvider _services;
    private readonly WorkflowCatalog _definitions = new();
    public HostEnd Host { get; } = new();
    public ActivityProbe Probe { get; } = new();
    public WorkflowExecutor Executor { get; }
    public Assign Assignment { get; private set; } = null!;
    public List<Type> Crossed { get; } = [];
    public List<ReadPayloadReply> PayloadReplies { get; } = [];
    public Func<Checkpoint, Checkpoint>? ChangeCheckpoint { get; set; }

    public MessageHarness()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow();
        services.AddSingleton<IWorkflowDefinitionSource>(_definitions);
        services.AddSingleton(Probe);
        services.AddTransient<Noop>();
        services.AddTransient<Block>();
        services.AddTransient<Cleanup>();
        _services = services.BuildServiceProvider();
        Executor = new(_services, new ExecutorEnd(this));
    }

    public Task RegisterAsync(WorkflowDefinition definition) => _definitions.RegisterWorkflowAsync(definition);

    public async Task<Assign> AssignAsync(WorkflowDefinition definition)
    {
        await _definitions.RegisterWorkflowAsync(definition);
        var hash = (await _definitions.GetWorkflowStructureHashAsync(definition.Name, definition.Version))!;
        // A public stored-checkpoint fixture, rather than a live execution graph injected into the executor.
        var now = DateTime.UtcNow;
        var instance = new WorkflowInstance
        {
            InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = definition.Name,
            WorkflowVersion = definition.Version, WorkflowDataType = typeof(Data).AssemblyQualifiedName!,
            WorkflowDataJson = StoredJson.Inline("{\"Value\":\"input\"}"), Status = WorkflowStatus.Running,
            CorrelationId = "correlation", CreatedAt = now, StartedAt = now,
            ExecutionStateJson = JsonSerializer.Serialize(new
            {
                Runtime = "structured-v2", SchemaVersion = 4, StructureHash = hash,
                Continuations = new[] { new { Id = "root", Stack = new[] { new { ScopeId = "root" } } } }
            })
        };
        var lease = await Host.Store.TryAcquireExecutionLeaseAsync(instance.InstanceId, "message-harness", TimeSpan.FromHours(1), new ClaimQuery(Guid.Empty, new Dictionary<string, string>()));
        var initial = CheckpointCommit.Create(instance, lease!.Epoch, 0);
        Assert.Equal(WorkflowCommitStatus.Applied, (await Host.Store.CommitWorkflowInstanceAsync(initial)).Status);
        var saved = (await Host.Store.GetWorkflowInstanceAsync(instance.InstanceId))!;
        Assignment = new(Guid.NewGuid(), saved, initial.ApplyParts(null), hash, lease.Epoch,
            0, ExecutionLimits.Default with { PayloadThresholdBytes = 256 * 1024 }, false, []);
        Host.Assignment = Assignment;
        Host.Active = true;
        return Assignment;
    }

    public async Task<Ready> ExecuteAsync(Assign? assign = null)
    {
        var ready = await Executor.ExecuteAsync(Cross(assign ?? Assignment), CancellationToken.None);
        ready = Cross(ready);
        ExecutionContract.RequireVersion(ready.ContractVersion);
        Assert.Equal((assign ?? Assignment).AssignmentId, ready.AssignmentId);
        Host.Active = false;
        return ready;
    }

    public void Deliver(Cancel value) => Executor.Deliver(Cross(value));
    public void Deliver(Arrivals value) => Executor.Deliver(Cross(value));
    public void Deliver(Recycle value) => Executor.Deliver(Cross(value));
    public T Cross<T>(T value) where T : class
    {
        var info = ExecutionMessageContext.Default.GetTypeInfo(typeof(T))!;
        Crossed.Add(typeof(T));
        return (T)JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(value, info), info)!;
    }
    public Task<CheckpointReply> SendAsync(Checkpoint value) => new ExecutorEnd(this).CheckpointAsync(value);
    public void Dispose() { Probe.Release.TrySetResult(); _services.Dispose(); }

    private sealed class ExecutorEnd(MessageHarness harness) : IExecutionHost
    {
        public async Task<ReadPayloadReply> ReadPayloadAsync(ReadPayload request)
        {
            var reply = harness.Cross(await harness.Host.ReadPayloadAsync(harness.Cross(request)));
            harness.PayloadReplies.Add(reply);
            return reply;
        }
        public async Task<CheckpointReply> CheckpointAsync(Checkpoint request)
        {
            request = harness.Cross(request);
            if (harness.ChangeCheckpoint != null) request = harness.ChangeCheckpoint(request);
            return harness.Cross(await harness.Host.CheckpointAsync(request));
        }
        public async Task<StartChildReply> StartChildAsync(StartChild request) =>
            harness.Cross(await harness.Host.StartChildAsync(harness.Cross(request)));
        public async Task<ReadInboxReply> ReadInboxAsync(ReadInbox request) =>
            harness.Cross(await harness.Host.ReadInboxAsync(harness.Cross(request)));
        public async Task<ReadWaitReply> ReadWaitAsync(ReadWait request) =>
            harness.Cross(await harness.Host.ReadWaitAsync(harness.Cross(request)));
    }

    internal sealed class HostEnd : IExecutionHost
    {
        public InMemoryWorkflowStateRepository Store { get; } = new();
        private ExecutionPayloadTransfer? _payloads;
        public ExecutionPayloadTransfer Payloads => _payloads ??= new(Store.PayloadStore, Assignment.Limits);
        public Task<ReadPayloadReply> ReadPayloadAsync(ReadPayload request) =>
            Admits(request.AssignmentId, request.InstanceId) ? Payloads.ReadAsync(request) :
                Task.FromResult(new ReadPayloadReply(request.AssignmentId, Conflict: ConflictReason.NotAssigned));
        public Assign Assignment { get; set; } = null!;
        public bool Active { get; set; }
        public bool LoseFirstReply { get; set; }
        public Func<Checkpoint, bool>? LoseReply { get; set; }
        public int StoreCalls { get; private set; }
        public List<Checkpoint> Requests { get; } = [];
        public List<WorkflowCommitStatus> Receipts { get; } = [];
        public List<StartChild> Children { get; } = [];
        public bool RefuseChildren { get; set; }
        public WorkflowInstance? Saved { get; private set; }
        public CheckpointReply? LastReply { get; private set; }
        private bool Admits(Guid identity, string instance) =>
            Active && identity == Assignment.AssignmentId && instance == Assignment.Instance.InstanceId;

        public async Task<CheckpointReply> CheckpointAsync(Checkpoint request)
        {
            Requests.Add(request);
            if (!Admits(request.AssignmentId, request.Commit.InstanceId))
                return LastReply = new(request.AssignmentId, CheckpointReplyKind.Conflict, Conflict: ConflictReason.NotAssigned);
            if (request.Commit.Epoch != Assignment.Epoch || request.Commit.Incarnation != Assignment.Incarnation)
                return LastReply = new(request.AssignmentId, CheckpointReplyKind.Conflict, Conflict: ConflictReason.StaleEpoch);
            var commit = request.Payloads is { Count: > 0 } payloads
                ? Payloads.PrepareCommit(request.Commit, payloads) : request.Commit;
            StoreCalls++;
            var receipt = await Store.CommitWorkflowInstanceAsync(commit);
            Receipts.Add(receipt.Status);
            Saved = await Store.GetWorkflowInstanceAsync(request.Commit.InstanceId);
            if (LoseFirstReply && StoreCalls == 1 || LoseReply?.Invoke(request) == true)
                return LastReply = new(request.AssignmentId, CheckpointReplyKind.Unknown, Error: "Reply lost after apply");
            return LastReply = receipt.Status is WorkflowCommitStatus.Applied or WorkflowCommitStatus.AlreadyApplied
                ? new(request.AssignmentId, CheckpointReplyKind.Ack, receipt.Revision)
                : new(request.AssignmentId, CheckpointReplyKind.Conflict, receipt.Revision, Conflict: ConflictReason.StaleEpoch);
        }
        public Task<StartChildReply> StartChildAsync(StartChild request)
        {
            Children.Add(request);
            return Task.FromResult(!Admits(request.AssignmentId, request.InstanceId)
                ? new StartChildReply(request.AssignmentId, Conflict: ConflictReason.NotAssigned)
                : RefuseChildren ? new(request.AssignmentId, Refusal: WorkflowErrorKind.ArtifactUnavailable, Message: "Child unavailable")
                : new(request.AssignmentId, request.Start));
        }
        public async Task<ReadInboxReply> ReadInboxAsync(ReadInbox request) =>
            Admits(request.AssignmentId, request.InstanceId)
                ? new(request.AssignmentId, await Store.GetPendingEventsAsync(request.InstanceId, request.AfterSequence, request.Limit))
                : new(request.AssignmentId, Conflict: ConflictReason.NotAssigned);
        public async Task<ReadWaitReply> ReadWaitAsync(ReadWait request) =>
            Admits(request.AssignmentId, request.InstanceId)
                ? new(request.AssignmentId, await Store.GetWaitAsync(request.InstanceId, request.WaitId))
                : new(request.AssignmentId, Conflict: ConflictReason.NotAssigned);
    }

    public static WorkflowDefinition Cancellable() => Workflow.Create<Data>("serialized-cancellation")
        .Step<Noop>().Try(body => body.Step<Block>()).Finally(body => body.Step<Cleanup>()).Step<Noop>().Build();
    public sealed class Data { public string Value { get; set; } = ""; }
    public sealed class ActivityProbe
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Cleanups;
        public int Noops;
    }
    public sealed class Noop(ActivityProbe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref probe.Noops); return Task.CompletedTask; }
    }
    public sealed class Block(ActivityProbe probe) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { probe.Started.TrySetResult(); await probe.Release.Task.WaitAsync(cancellationToken); }
    }
    public sealed class Cleanup(ActivityProbe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref probe.Cleanups); return Task.CompletedTask; }
    }
}
