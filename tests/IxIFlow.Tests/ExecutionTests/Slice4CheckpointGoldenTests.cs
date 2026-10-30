using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using Xunit.Abstractions;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
[Trait("Spec", "A-08")]
[Trait("Spec", "F-27")]
public sealed class Slice4CheckpointGoldenTests(ITestOutputHelper output)
{
    [Fact]
    public void CheckpointVersionOnePinsTheTrunkAndEveryPartKindAndCanReadThem()
    {
        var checkpoint = Example();
        var stored = StoredCheckpoint.Split(checkpoint, StoredJson.Inline("{\"Value\":7}"));
        var actual = new Fixture(stored.Trunk, stored.Parts.ToDictionary(pair => pair.Key, pair => pair.Value.InlineText));
        output.WriteLine("CHECKPOINT_GOLDEN=" + JsonSerializer.Serialize(actual));
        var expected = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(FixturePath()))!;
        Assert.Equal(Encoding.UTF8.GetBytes(expected.Trunk), Encoding.UTF8.GetBytes(stored.Trunk));
        Assert.Equal(expected.Parts.Keys.Order(), stored.Parts.Keys.Order());

        foreach (var (key, value) in expected.Parts)
            Assert.Equal(Encoding.UTF8.GetBytes(value), Encoding.UTF8.GetBytes(stored.Parts[key].InlineText));

        var read = ExecutionCheckpoint.Read(
            StoredCheckpoint.AssembleState(
                expected.Trunk,
                expected.Parts.ToDictionary(pair => pair.Key, pair => StoredJson.Inline(pair.Value))));

        Assert.Equal(1, read.SchemaVersion);
        Assert.Equal("ixiflow-1", read.Runtime);
        Assert.Equal(9, read.Continuations.Single(c => c.Id == "branch").Sequence);
        Assert.Equal("join", Assert.Single(read.Joins).Id);
        Assert.Equal("wait", Assert.Single(read.Waits).Id);
        Assert.Equal("event", Assert.Single(read.BoundReplies).EventId);
        Assert.Equal("{\"Inner\":3}", read.InlineFrames["frame"].Data.InlineText);
        var loop = read.Continuations[0].Stack[0].ForEach!;
        Assert.Equal("1", loop.Items[0].InlineText);
        Assert.Equal("2", loop.Results![0]!.InlineText);
        Assert.Equal(new[] { 0 }, loop.Finished);
        Assert.Equal("invocation", Assert.Single(read.InvocationRecords).Value.Id);
    }

    private static ExecutionCheckpoint Example()
    {
        var now = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
        var finished = new ItemSet();
        finished.Add(0);

        return new ExecutionCheckpoint
        {
            StructureHash = "golden-structure", BranchesStarted = 9,
            Continuations =
            [
                new()
                {
                    Id = "root", Status = ContinuationStatus.Joining,
                    Stack =
                    [
                        new()
                        {
                            ScopeId = "root", ActivationId = "root-frame",
                            ForEach = new()
                            {
                                Id = "loop", Items = [StoredJson.Inline("1")], Next = 1,
                                Results = [StoredJson.Inline("2")], Finished = finished
                            }
                        }
                    ]
                },
                new()
                {
                    Id = "branch", ParentJoinId = "join", Sequence = 9, Status = ContinuationStatus.Waiting,
                    Stack = [new() { ScopeId = "root/0/branch/0", ActivationId = "branch-frame" }]
                }
            ],
            Joins = [new() { Id = "join", StepId = "root/0", ParentContinuationId = "root" }],
            Waits = [new() { Id = "wait", ContinuationId = "branch", StepId = "root/0/branch/0/0", Key = "go", RegisteredAtUtc = now }],
            BoundReplies =
            [
                new()
                {
                    WaitId = "reply", EventId = "event", ContinuationId = "branch", StepId = "root/0/branch/0/1",
                    ScopeActivationId = "branch-frame", Event = new() { Type = "System.String", Json = StoredJson.Inline("\"answer\"") }
                }
            ],
            InlineFrames = new()
            {
                ["frame"] = new() { OwnerContinuationId = "root", DataType = "GoldenData", Data = StoredJson.Inline("{\"Inner\":3}") }
            },
            InvocationIndex = [new("invocation", "root/0", "root", 1, false, true)],
            InvocationRecords = new()
            {
                ["invocation"] = new()
                {
                    Id = "invocation", ContinuationId = "root", FrameActivationId = "root-frame", StepId = "root/0",
                    WorkflowName = "Golden", WorkflowVersion = 1, StartedAtUtc = now,
                    Attempts = [new() { Number = 1, PreparedAtUtc = now, Host = "golden-host" }]
                }
            }
        };
    }

    private static string FixturePath([CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "Fixtures", "checkpoint-v1.json");

    private sealed record Fixture(string Trunk, Dictionary<string, string> Parts);
}