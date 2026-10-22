using System.Text.Json;
using IxIFlow.Core;
using IxIFlow.Builders;

namespace IxIFlow.ContractTests;

[Trait("Feature", "children")]
public sealed class WorkflowContractMessageTests
{
    [Fact]
    public async Task ChildStartCarriesTheCallersContractHash()
    {
        var contract = WorkflowContract.OfType(typeof(Data));
        using var harness = new MessageHarness();
        var assignment = await harness.AssignAsync(Workflow.Create<MessageHarness.Data>("HashTransport").Step<MessageHarness.Noop>().Build());
        var message = new StartChild(Guid.NewGuid(), "parent", 2, "root/0", new("child", JsonSerializer.Serialize(assignment.Instance), "wait"), "structure", contract.Hash);
        var json = JsonSerializer.SerializeToUtf8Bytes(message, ExecutionMessageContext.Default.StartChild);
        var restored = JsonSerializer.Deserialize(json, ExecutionMessageContext.Default.StartChild)!;
        Assert.Equal(message.Start.Payload, restored.Start.Payload);
        Assert.Equal(message.StructureHash, restored.StructureHash);
        Assert.Equal(message.AssignmentId, restored.AssignmentId);
        Assert.Equal(contract.Hash, restored.ContractHash);
        Assert.Equal(12, ExecutionContract.Version);
    }

    public sealed class Data { public int Value { get; set; } }
}
