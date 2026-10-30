using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using Xunit.Abstractions;

namespace IxIFlow.ContractTests;

[Trait("Feature", "host")]
[Trait("Spec", "A-02")]
public sealed class Slice4MessageGoldenTests(ITestOutputHelper output)
{
    [Fact]
    public void EveryHostMessageHasAPinnedJsonShape()
    {
        var instance = new WorkflowInstance
        {
            InstanceId = "instance", WorkflowName = "Golden", WorkflowVersion = 1,
            WorkflowDataJson = StoredJson.Inline("{\"Value\":42}"),
            ExecutionStateJson = "{\"Runtime\":\"ixiflow-1\",\"SchemaVersion\":1,\"StructureHash\":\"golden\",\"Continuations\":[]}",
            CreatedAt = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc)
        };

        var assign = new Assign(
            Guid.Parse("22222222-2222-2222-2222-222222222222"), instance,
            null, "golden", 7, 9, new ExecutionLimits(), true, []);

        var reason = new CancellationReason
        {
            ReasonCode = "cancel", RequestedBy = "operator", Description = "reason",
            RequestedAt = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc),
            Metadata = new() { ["number"] = JsonSerializer.SerializeToElement(42) }
        };

        assign.Instance.Properties["nested"] = JsonSerializer.SerializeToElement(new { Number = 42, Text = "value" });

        assign = assign with
        {
            Recovery = new(["root"], reason, true, true, "operator"),
            Breakpoints =
            [
                new("stop", "root/0", new("flow.yaml", 4), ">= 3")
                {
                    StructureHash = assign.StructureHash, VerifiedSource = new("flow.yaml", 7), Condition = "context.WorkflowData.Value > 0"
                }
            ]
        };

        var now = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

        var commit = CheckpointCommit.Create(
            assign.Instance, assign.Epoch, assign.Instance.Revision, commitId: "golden-commit",
            openedWaits: [new("wait", WaitKind.Event, "go", "event-type", TimeSpan.FromMinutes(1))]);

        commit = CheckpointCommit.FromFields(
            commit.Instance, commit.Payload, commit.Parts, commit.DeletedParts,
            commit.ReplacesParts, Guid.Parse("11111111-1111-1111-1111-111111111111"), commit.Epoch, commit.CommitId,
            commit.ExpectedRevision, commit.ClearsCrashCount, commit.Parks, commit.Settlements, commit.InboxReadThrough,
            commit.OpenedWaits, commit.ArmedWaits, commit.ClosedWaits, commit.WakeAfter, commit.Stopped,
            commit.ChildStarts, commit.ChildCancellations, commit.ParentNotice, commit.TraceEntries,
            commit.Diagnostics, commit.RetainedDiagnosticIds);

        var child = new ChildStart(
            "child", JsonSerializer.Serialize(
                new WorkflowInstance
                {
                    InstanceId = "child", ParentInstanceId = assign.Instance.InstanceId, ParentWaitId = "child-wait",
                    ExecutionStateJson = assign.Instance.ExecutionStateJson, WorkflowDataJson = StoredJson.Inline("{\"Value\":42}")
                }), "child-wait") { Incarnation = Guid.Parse("33333333-3333-3333-3333-333333333333") };

        var page = new InboxPage(
            [
                new(
                    assign.Instance.InstanceId, "event", 7, "event-type", "go", null,
                    StoredJson.Inline("{\"Answer\":42}"), now, EventOutcome.Pending)
            ], 7, new Dictionary<string, long> { ["wait"] = 3 },
            new Dictionary<string, DateTime> { ["wait"] = now.AddMinutes(1) }, now);

        object[] messages =
        [
            .. InspectionMessageExamples.Create(now),
            assign, new Ready(
                assign.AssignmentId, new(AssignmentOutcomeKind.Interrupted, "error", "type", "stack"),
                new("commit", 5), ExecutorName: "executor", Capabilities: new Dictionary<string, string> { ["host"] = "local" }),
            new Cancel(assign.AssignmentId, CancelKind.Workflow, reason), new Arrivals(assign.AssignmentId, 7),
            new Recycle(assign.AssignmentId), new Checkpoint(assign.AssignmentId, commit),
            new CheckpointReply(
                assign.AssignmentId, CheckpointReplyKind.Unknown, 5, "lost reply", reason,
                true, true, ConflictReason.EventsChanged),
            new StartChild(
                assign.AssignmentId, assign.Instance.InstanceId, assign.Epoch, "root/0", child, "hash",
                WorkflowContract.OfType(typeof(GoldenData)).Hash),
            new StartChildReply(assign.AssignmentId, child, WorkflowErrorKind.ArtifactUnavailable, "refused", ConflictReason.NotAssigned),
            new ReadInbox(assign.AssignmentId, assign.Instance.InstanceId, 3, 20), new ReadInboxReply(assign.AssignmentId, page),
            new ReadWait(assign.AssignmentId, assign.Instance.InstanceId, "wait"),
            new ReadWaitReply(
                assign.AssignmentId, new(
                    assign.Instance.InstanceId, "wait", WaitKind.Event,
                    "go", "event-type", now.AddMinutes(1), 3, now, DefaultPayloadJson: StoredJson.Inline("{\"Default\":true}"))),
            assign.Breakpoints[0], assign.Recovery!, new AcknowledgedCommit("commit", 5),
            new BreakpointSource("flow.yaml", 4), new BreakpointStop("root", "root/0", "stop", "activation", 2, 3, now)
                { Reason = "ConditionCompilationFailed", Diagnostics = [new("BPC001", "Refused", 0, 3, 1, 1)] },
            new BreakpointHitTrace("root", "root/0", "stop", 3, 1)
                { Reason = "ConditionCompilerUnavailable" },
            new DataEditTarget(3, "branch"),
            new DataPatch(["Number"], JsonSerializer.SerializeToElement(true)),
            new DataEditRequest(5, new(3), [new(["Number"], JsonSerializer.SerializeToElement(7))]),
            new DataEditResult("instance", "edit", 6, new(3)),
            new DataEditedTrace("editor", "edit", new(3), 6, [["Number"]]),
            new DebugStepPosition("activation", 2, 3, 4, true),
            new DebugStepping(
                "root", DebugStepKind.Over, new("activation", 2, 3, 4, true),
                ["outer", "activation"], ["root", "forked"], "step", "actor", true),
            new BreakpointEvaluation("root", "root/0", "activation", 2, now),
            new BreakpointConditionDiagnostic("BPC001", "Refused member", 4, 3, 1, 5),
            new AssignmentOutcome(AssignmentOutcomeKind.Interrupted, "error", "type", "stack", "root/0", "attempt")
        ];

        Assert.Equal(MessageContractTests.MessageTypeList.OrderBy(t => t.Name), messages.Select(m => m.GetType()).OrderBy(t => t.Name));

        var differences = new List<string>();

        foreach (var message in messages)
        {
            var type = message.GetType();
            var info = ExecutionMessageContext.Default.GetTypeInfo(type)!;
            var actual = JsonSerializer.SerializeToUtf8Bytes(message, info);
            output.WriteLine("MESSAGE_GOLDEN=" + type.Name + "=" + Encoding.UTF8.GetString(actual));
            var path = FixturePath(type.Name);

            if (!File.Exists(path))
            {
                differences.Add(type.Name + ": missing fixture");

                continue;
            }

            var expected = File.ReadAllBytes(path);

            if (!expected.SequenceEqual(actual))
                differences.Add(type.Name + ": wire format changed at ExecutionContract.Version 14");

            Assert.Equal(expected, JsonSerializer.SerializeToUtf8Bytes(JsonSerializer.Deserialize(expected, info), info));
        }

        Assert.Empty(differences);
    }

    private static string FixturePath(string name, [CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "Fixtures", "messages-v14", name + ".json");

    public sealed class GoldenData
    {
        public int Value { get; set; }
    }
}