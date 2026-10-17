using System.Text.Json;
using IxIFlow.Core;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// What a commit carries (A-08): the execution state as JSON inside the instance, not an escaped string, and
/// the fields a store applies the commit by, so a store need not parse the payload.
/// </summary>
[Trait("Feature", "durability")]
public sealed class CheckpointPayloadTests
{
    [Fact]
    [Trait("Spec", "A-08")]
    public void TheExecutionStateIsWrittenAsJsonAndReadBackAsItsText()
    {
        var instance = new WorkflowInstance
        {
            InstanceId = "payload", WorkflowName = "Payload", ExecutionStateJson = "{\"Continuations\":[{\"Id\":\"a\"}]}"
        };

        var json = JsonSerializer.Serialize(instance);
        var read = JsonSerializer.Deserialize<WorkflowInstance>(json)!;

        Assert.Contains("\"ExecutionStateJson\":{\"Continuations\":[{\"Id\":\"a\"}]}", json);
        Assert.Equal(instance.ExecutionStateJson, read.ExecutionStateJson);
    }

    [Fact]
    [Trait("Spec", "A-08")]
    public void AStateSavedAsAnEscapedStringStillReads()
    {
        var saved = "{\"InstanceId\":\"old\",\"ExecutionStateJson\":\"{\\\"Continuations\\\":[]}\"}";

        Assert.Equal("{\"Continuations\":[]}", JsonSerializer.Deserialize<WorkflowInstance>(saved)!.ExecutionStateJson);
    }

    [Fact]
    [Trait("Spec", "A-08")]
    public void AStateThatIsNotAnObjectStaysAString()
    {
        var instance = new WorkflowInstance { InstanceId = "empty", ExecutionStateJson = "" };

        var read = JsonSerializer.Deserialize<WorkflowInstance>(JsonSerializer.Serialize(instance))!;

        Assert.Equal("", read.ExecutionStateJson);
    }

    [Theory]
    [Trait("Spec", "A-08")]
    [InlineData(63, true)]
    [InlineData(64, false)]
    public void AStateTooDeepToSitInsideItsInstanceIsWrittenEscapedAndReadsBack(int depth, bool raw)
    {
        var state = new string('[', depth) + new string(']', depth);
        var instance = new WorkflowInstance { InstanceId = "deep", ExecutionStateJson = state };

        var json = JsonSerializer.Serialize(instance);
        var read = JsonSerializer.Deserialize<WorkflowInstance>(json)!;

        Assert.Equal(raw, json.Contains("\"ExecutionStateJson\":[", StringComparison.Ordinal));
        Assert.Equal(state, read.ExecutionStateJson);
    }

    [Theory]
    [Trait("Spec", "A-08")]
    [InlineData("{\"unclosed\":")]
    [InlineData("{} trailing")]
    [InlineData("  {\"Padded\":true}  ")]
    [InlineData("[1, 2, 3]")]
    [InlineData("null")]
    [InlineData("42")]
    public void AnyStateReadsBackAsTheTextItWas(string state)
    {
        var instance = new WorkflowInstance { InstanceId = "text", ExecutionStateJson = state };

        var read = JsonSerializer.Deserialize<WorkflowInstance>(JsonSerializer.Serialize(instance))!;

        // Raw JSON keeps its content, though not the blanks around it.
        Assert.Equal(state.Trim().StartsWith('{') && state.Trim().EndsWith('}') || state.StartsWith('[')
            ? state.Trim() : state, read.ExecutionStateJson);
    }

    [Fact]
    [Trait("Spec", "A-08")]
    public void ALargeStateRoundTripsThroughACommit()
    {
        var items = string.Join(",", Enumerable.Range(0, 100_000).Select(index => $"{{\"Id\":\"item-{index}\"}}"));
        var instance = new WorkflowInstance
        {
            InstanceId = "large", WorkflowName = "Large", ExecutionStateJson = $"{{\"Items\":[{items}]}}"
        };

        var commit = CheckpointCommit.Create(instance, 0, 0);

        Assert.True(commit.Payload.Length > 1_000_000);
        Assert.Equal(instance.ExecutionStateJson, commit.ReadInstance().ExecutionStateJson);
    }

    [Fact]
    [Trait("Spec", "A-08")]
    public void ACommitCarriesWhatAStoreAppliesItByAndReadsAtItsNewRevision()
    {
        var instance = new WorkflowInstance
        {
            InstanceId = "header", WorkflowName = "Header", CorrelationId = "c-1", Status = WorkflowStatus.Suspended,
            CancellationReason = new CancellationReason { ReasonCode = "operator" }, Revision = 3
        };

        var commit = CheckpointCommit.Create(instance, 0, 3);

        Assert.Equal(new CommittedInstance("header", "Header", "c-1", WorkflowStatus.Suspended, true), commit.Instance);
        Assert.Equal(4, commit.ReadInstance().Revision);
    }
}
