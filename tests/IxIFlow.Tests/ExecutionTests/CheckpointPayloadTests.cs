using IxIFlow.Tests.Infrastructure;
using System.Text.Json;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;

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
            InstanceId = "payload", WorkflowName = "Payload", ExecutionStateJson = StoreTestSupport.CheckpointJson
        };

        var json = JsonSerializer.Serialize(instance);
        var read = JsonSerializer.Deserialize<WorkflowInstance>(json)!;

        Assert.Contains("\"ExecutionStateJson\":" + instance.ExecutionStateJson, json);
        Assert.Equal(instance.ExecutionStateJson, read.ExecutionStateJson);
    }

    [Theory]
    [Trait("Spec", "A-08")]
    [InlineData(63, true)]
    [InlineData(64, false)]
    public void CheckpointDepthRoundTripsOrIsRejectedExplicitly(int depth, bool supported)
    {
        var nested = new string('[', depth - 1) + new string(']', depth - 1);
        var state = StoreTestSupport.CheckpointJson[..^1] + ",\"DepthProbe\":" + nested + "}";
        var instance = new WorkflowInstance { InstanceId = "deep", ExecutionStateJson = state };
        if (!supported)
        {
            Assert.Throws<JsonException>(() => JsonSerializer.Serialize(instance));
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<WorkflowInstance>(
                "{\"ExecutionStateJson\":" + state + "}"));
            return;
        }
        var json = JsonSerializer.Serialize(instance);
        Assert.Contains("\"ExecutionStateJson\":{", json);
        Assert.Equal(state, JsonSerializer.Deserialize<WorkflowInstance>(json)!.ExecutionStateJson);
    }

    [Fact]
    [Trait("Spec", "A-08")]
    public async Task ALargeStateRoundTripsThroughACommit()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var checkpoint = new ExecutionCheckpoint { DefinitionFingerprint = new string('x', 1_100_000) };
        var instance = new WorkflowInstance
        {
            InstanceId = "large", WorkflowName = "Large", ExecutionStateJson = JsonSerializer.Serialize(checkpoint)
        };

        var commit = CheckpointCommit.Create(instance, (await StoreTestSupport.LeaseAsync(repository, instance.InstanceId)).Epoch, 0);

        Assert.True(commit.Payload.Length > 1_000_000);
        Assert.Equal(instance.ExecutionStateJson, commit.ReadInstance().ExecutionStateJson);
    }

    [Fact]
    [Trait("Spec", "A-08")]
    public async Task ACommitCarriesWhatAStoreAppliesItByAndReadsAtItsNewRevision()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var instance = new WorkflowInstance
        { ExecutionStateJson = StoreTestSupport.CheckpointJson,
            InstanceId = "header", WorkflowName = "Header", CorrelationId = "c-1", Status = WorkflowStatus.Suspended,
            CancellationReason = new CancellationReason { ReasonCode = "operator" }, Revision = 3
        };

        var commit = CheckpointCommit.Create(instance, (await StoreTestSupport.LeaseAsync(repository, instance.InstanceId)).Epoch, 3);

        Assert.Equal(new CommittedInstance("header", "Header", "c-1", WorkflowStatus.Suspended, true), commit.Instance);
        Assert.Equal(4, commit.ReadInstance().Revision);
    }
}
