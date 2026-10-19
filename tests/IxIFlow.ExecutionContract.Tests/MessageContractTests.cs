using System.Reflection;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;
using IxIFlow.Extensions;
using static IxIFlow.ContractTests.MessageHarness;

namespace IxIFlow.ContractTests;

[Trait("Feature", "host")]
[Trait("Spec", "A-02")]
public sealed class MessageContractTests
{
    internal static readonly Type[] MessageTypeList =
    [
        typeof(Assign), typeof(Ready), typeof(Cancel), typeof(Arrivals), typeof(Recycle),
        typeof(Checkpoint), typeof(CheckpointReply), typeof(StartChild), typeof(StartChildReply),
        typeof(ReadInbox), typeof(ReadInboxReply), typeof(ReadWait), typeof(ReadWaitReply),
        typeof(Breakpoint), typeof(RecoveryPreparation), typeof(AcknowledgedCommit), typeof(AssignmentOutcome)
    ];
    public static TheoryData<Type> MessageTypes => new(MessageTypeList);

    [Theory]
    [MemberData(nameof(MessageTypes))]
    public async Task EveryMessageRoundTripsThroughTheGeneratedContext(Type type)
    {
        using var harness = new MessageHarness();
        var assign = await harness.AssignAsync(Workflow.Create<Data>("contract").Step<Noop>().Build());
        var reason = new CancellationReason
        {
            ReasonCode = "cancel", RequestedBy = "operator", Description = "reason", RequestedAt = DateTime.UtcNow,
            Metadata = new() { ["number"] = JsonSerializer.SerializeToElement(42) }
        };
        assign.Instance.Properties["nested"] = JsonSerializer.SerializeToElement(new { Number = 42, Text = "value" });
        assign = assign with
        {
            Recovery = new(["root"], reason, true, true, "operator"),
            Breakpoints = [new("root/0", "value == 1", 3)]
        };
        var now = DateTime.UtcNow;
        var commit = CheckpointCommit.Create(assign.Instance, assign.Epoch, assign.Instance.Revision,
            openedWaits: [new("wait", WaitKind.Event, "go", "event-type", TimeSpan.FromMinutes(1))]);
        var child = new ChildStart("child", JsonSerializer.Serialize(new WorkflowInstance
        {
            InstanceId = "child", ParentInstanceId = assign.Instance.InstanceId, ParentWaitId = "child-wait",
            ExecutionStateJson = assign.Instance.ExecutionStateJson, WorkflowDataJson = "{\"Value\":42}"
        }), "child-wait");
        var page = new InboxPage([new(assign.Instance.InstanceId, "event", 7, "event-type", "go", null,
            "{\"Answer\":42}", now, EventOutcome.Pending)], 7, new Dictionary<string, long> { ["wait"] = 3 },
            new Dictionary<string, DateTime> { ["wait"] = now.AddMinutes(1) }, now);
        object[] messages =
        [
            assign, new Ready(assign.AssignmentId, new(AssignmentOutcomeKind.Interrupted, "error", "type", "stack"),
                new("commit", 5), ExecutorName: "executor", Capabilities: new Dictionary<string, string> { ["host"] = "local" }),
            new Cancel(assign.AssignmentId, CancelKind.Workflow, reason), new Arrivals(assign.AssignmentId, 7),
            new Recycle(assign.AssignmentId), new Checkpoint(assign.AssignmentId, commit),
            new CheckpointReply(assign.AssignmentId, CheckpointReplyKind.Unknown, 5, "lost reply", reason,
                true, true, ConflictReason.EventsChanged),
            new StartChild(assign.AssignmentId, assign.Instance.InstanceId, assign.Epoch, "root/0", child, "hash"),
            new StartChildReply(assign.AssignmentId, child, WorkflowErrorKind.ArtifactUnavailable, "refused", ConflictReason.NotAssigned),
            new ReadInbox(assign.AssignmentId, assign.Instance.InstanceId, 3, 20), new ReadInboxReply(assign.AssignmentId, page),
            new ReadWait(assign.AssignmentId, assign.Instance.InstanceId, "wait"),
            new ReadWaitReply(assign.AssignmentId, new(assign.Instance.InstanceId, "wait", WaitKind.Event,
                "go", "event-type", now.AddMinutes(1), 3, now, DefaultPayloadJson: "{\"Default\":true}")),
            assign.Breakpoints[0], assign.Recovery!, new AcknowledgedCommit("commit", 5),
            new AssignmentOutcome(AssignmentOutcomeKind.Interrupted, "error", "type", "stack", "root/0", "attempt")
        ];
        var message = Assert.Single(messages, value => value.GetType() == type);
        Assert.True(type.IsPublic && type.IsSealed);
        Assert.NotNull(type.GetMethod("<Clone>$")); // sealed records, not mutable message classes
        var info = ExecutionMessageContext.Default.GetTypeInfo(type)!;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, info);
        var copy = JsonSerializer.Deserialize(bytes, info)!;
        Assert.NotSame(message, copy);
        Assert.Equal(bytes, JsonSerializer.SerializeToUtf8Bytes(copy, info));
        if (copy is Assign assigned)
        {
            Assert.NotEqual(Guid.Empty, assigned.Incarnation);
            Assert.Equal(assign.Incarnation, assigned.Incarnation);
            Assert.Equal(42, assigned.Instance.Properties["nested"].GetProperty("Number").GetInt32());
        }
        if (copy is Checkpoint checkpoint) Assert.Equal(commit.ContentHash, checkpoint.Commit.ContentHash);
    }

    [Fact]
    public async Task RebuiltAndDeserializedCommitsKeepTheirHashAndRejectContradictoryMetadata()
    {
        using var harness = new MessageHarness();
        var assign = await harness.AssignAsync(Workflow.Create<Data>("hash").Step<Noop>().Build());
        var commit = CheckpointCommit.Create(assign.Instance, assign.Epoch, assign.Instance.Revision,
            settlements: [new("event", EventOutcome.Rejected, Rejection: EventRejection.WaitCancelled)],
            openedWaits: [new("wait", WaitKind.Event, "go", "event-type", TimeSpan.FromMinutes(1))],
            inboxReadThrough: 8, wakeAfter: TimeSpan.FromSeconds(15), parks: true,
            traceEntries: [new() { ActivityName = "activity" }]);
        Assert.Equal(commit.ContentHash, SerializedExecutorTests.Rebuild(commit).ContentHash);
        var copy = harness.Cross(new Checkpoint(assign.AssignmentId, commit)).Commit;
        Assert.Equal(commit.ContentHash, copy.ContentHash);
        Assert.Equal(commit.Payload, copy.Payload);
        Assert.Equal(commit.TraceEntries, copy.TraceEntries);
        Assert.Null(typeof(CheckpointCommit).GetProperty("StoredParts", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.Throws<ArgumentException>(() => SerializedExecutorTests.Rebuild(commit,
            metadata: commit.Instance with { Status = WorkflowStatus.Completed }));
        var json = JsonSerializer.SerializeToNode(new Checkpoint(assign.AssignmentId, commit), ExecutionMessageContext.Default.Checkpoint)!;
        json["Commit"]!["Instance"]!["Status"] = (int)WorkflowStatus.Completed;
        Assert.Throws<JsonException>(() => json.Deserialize(ExecutionMessageContext.Default.Checkpoint));
        json["Commit"]!["Instance"]!["Status"] = (int)commit.Instance.Status;
        json["Commit"]!["ContentHash"] = Convert.ToBase64String(new byte[32]);
        Assert.Throws<JsonException>(() => json.Deserialize(ExecutionMessageContext.Default.Checkpoint));
    }

    [Fact]
    public async Task EveryCommitFieldSurvivesReconstructionIncludingChildrenAndParentNotice()
    {
        using var harness = new MessageHarness();
        var assign = await harness.AssignAsync(Workflow.Create<Data>("rich-commit").Step<Noop>().Build());
        var child = new WorkflowInstance
        {
            InstanceId = "child", ParentInstanceId = assign.Instance.InstanceId, ParentWaitId = "child-wait",
            Status = WorkflowStatus.Running, ExecutionStateJson = assign.Instance.ExecutionStateJson,
            WorkflowDataJson = "{\"Value\":\"child-input\"}"
        };
        assign.Instance.Status = WorkflowStatus.Completed;
        assign.Instance.ParentInstanceId = "grandparent";
        assign.Instance.ParentWaitId = "grandparent-wait";
        var commit = CheckpointCommit.Create(assign.Instance, assign.Epoch, assign.Instance.Revision,
            clearsCrashCount: false, stopped: true, inboxReadThrough: 19,
            openedWaits: [new("child-wait", WaitKind.Child, "child", "child-completed", null)],
            armedWaits: [new("reply-wait", TimeSpan.FromSeconds(11), "{\"Default\":42}")],
            closedWaits: [new("old-wait", WaitCloseReason.Replaced)],
            childStarts: [new("child", JsonSerializer.Serialize(child), "child-wait")],
            childCancellations: [new("former-child", new() { ReasonCode = "parent-ended" }, "former-wait")],
            parentNotice: new("grandparent", "grandparent-wait", "notice", "child-completed", "{\"Result\":42}"),
            traceEntries: [new() { ActivityName = "completed" }]);
        var copy = harness.Cross(new Checkpoint(assign.AssignmentId, commit)).Commit;
        Assert.Equal(commit.ContentHash, copy.ContentHash);
        Assert.Equal(commit.ContentHash, SerializedExecutorTests.Rebuild(copy).ContentHash);
        Assert.Equal(commit.ChildStarts.Select(child => (child.ChildInstanceId, child.Payload, child.ParentWaitId, child.Instance)),
            copy.ChildStarts.Select(child => (child.ChildInstanceId, child.Payload, child.ParentWaitId, child.Instance)));
        Assert.Equal("parent-ended", Assert.Single(copy.ChildCancellations).Reason.ReasonCode);
        Assert.Equal(commit.ParentNotice, copy.ParentNotice);
        Assert.False(copy.ClearsCrashCount);
        Assert.True(copy.Stopped);
        Assert.Equal(commit.Incarnation, copy.Incarnation);
        Assert.Equal(commit.Epoch, copy.Epoch);
        Assert.Equal(commit.ExpectedRevision, copy.ExpectedRevision);
    }

    [Fact]
    public async Task VersionMismatchesAreRefusedBeforeExecutionOrReadyAdmission()
    {
        using var harness = new MessageHarness();
        var assign = await harness.AssignAsync(Workflow.Create<Data>("version").Step<Noop>().Build());
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() =>
            harness.ExecuteAsync(assign with { ContractVersion = ExecutionContract.Version + 1 }));
        Assert.Equal(WorkflowErrorKind.InvalidRequest, error.Kind);
        Assert.Empty(harness.Host.Requests);
        Assert.Equal(0, harness.Probe.Noops);
        var ready = harness.Cross(new Ready(Guid.Empty, null, null, ExecutionContract.Version + 1));
        Assert.Throws<WorkflowEngineException>(() => ExecutionContract.RequireVersion(ready.ContractVersion));
        var initial = harness.Cross(new Ready(Guid.Empty, null, null));
        Assert.Equal(ExecutionContract.Version, initial.ContractVersion);
        Assert.Null(initial.Outcome);
    }

    [Fact]
    public void NoMessageGraphContainsAnObjectOrLiveRuntimeMember()
    {
        var seen = new HashSet<Type>();
        foreach (var type in MessageTypeList) Inspect(type);
        void Inspect(Type type)
        {
            Assert.NotEqual(typeof(object), type);
            Assert.False(typeof(Exception).IsAssignableFrom(type) || typeof(Delegate).IsAssignableFrom(type) || type == typeof(Type), type.FullName);
            if (!seen.Add(type) || type == typeof(JsonElement) || type == typeof(string) || type.IsPrimitive || type.IsEnum) return;
            if (type.HasElementType) { Inspect(type.GetElementType()!); return; }
            if (type.IsGenericType) foreach (var argument in type.GetGenericArguments()) Inspect(argument);
            if (type.Namespace?.StartsWith("IxIFlow") != true) return;
            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public)) Inspect(property.PropertyType);
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public)) Inspect(field.FieldType);
        }
    }

    [Fact]
    public void RegistryIsTheDefinitionSourceWithoutAnAdapter()
    {
        using var services = new ServiceCollection().AddLogging().AddIxIFlow().Services.BuildServiceProvider();
        Assert.True(typeof(IWorkflowDefinitionSource).IsAssignableFrom(typeof(IWorkflowCatalog)));
        Assert.Same(services.GetRequiredService<IWorkflowCatalog>(), services.GetRequiredService<IWorkflowDefinitionSource>());
        Assert.DoesNotContain(typeof(WorkflowExecutor).Assembly.GetTypes(), type => type.Name == "RegisteredWorkflowDefinitionSource");
    }
}
