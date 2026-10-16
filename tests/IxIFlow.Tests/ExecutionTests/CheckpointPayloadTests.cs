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
