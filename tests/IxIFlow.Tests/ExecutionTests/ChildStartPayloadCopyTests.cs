using System.Text.Json;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
public sealed class ChildStartPayloadCopyTests
{
    [Theory]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    public void CopiedPayloadNameIsValidatedBeforeParentSerialization(string name)
    {
        var child = Instance("child", "Original");
        var original = new ChildStart(child.InstanceId, JsonSerializer.Serialize(child), "child");
        Assert.Equal("Original", original.Instance!.WorkflowName);
        child.WorkflowName = name;
        var copy = original with { Payload = JsonSerializer.Serialize(child) };
        Assert.Equal(name, copy.Instance!.WorkflowName);
        Assert.Equal("Original", original.Instance!.WorkflowName);

        var parent = Instance("parent", "Parent");
        Assert.ThrowsAny<ArgumentException>(() => CheckpointCommit.Create(parent, 1, 0, childStarts: [copy]));
        // Create stamps the incarnation immediately before serializing the parent.
        Assert.Equal(Guid.Empty, parent.Incarnation);
    }

    [Theory]
    [InlineData("D\ufffd")]
    [InlineData("D\U00010000")]
    [InlineData("")]
    public void CopyFromRejectedNameUsesTheNewValidPayload(string name)
    {
        var child = Instance("child", " old-invalid ");
        var original = new ChildStart(child.InstanceId, JsonSerializer.Serialize(child), "child");
        child.WorkflowName = name;
        var copy = original with { Payload = JsonSerializer.Serialize(child) };
        var commit = CheckpointCommit.Create(Instance("parent", "Parent"), 1, 0, childStarts: [copy],
            openedWaits: [new("child", WaitKind.Child, "child", ChildCompleted.EventType, null)]);
        Assert.Equal(name, Assert.Single(commit.ChildStarts).Instance!.WorkflowName);
        Assert.Equal(name, JsonSerializer.Deserialize<WorkflowInstance>(copy.Payload)!.WorkflowName);
        Assert.Equal(" old-invalid ", original.Instance!.WorkflowName);
        Assert.Equal(original.Incarnation, copy.Incarnation);
        Assert.Null((copy with { Payload = "null" }).Instance);
        Assert.Null((copy with { Payload = "invalid JSON" }).Instance);
    }

    private static WorkflowInstance Instance(string id, string name) => new()
    {
        InstanceId = id, WorkflowName = name, WorkflowVersion = 1, Status = WorkflowStatus.Running,
        ParentInstanceId = id == "child" ? "parent" : null, ParentWaitId = id == "child" ? "child" : null,
        ExecutionStateJson = StoreTestSupport.CheckpointJson, WorkflowDataJson = StoredJson.Inline("{}"),
        WorkflowDataType = WorkflowTypeIdentity.SavedName(typeof(object))
    };
}
