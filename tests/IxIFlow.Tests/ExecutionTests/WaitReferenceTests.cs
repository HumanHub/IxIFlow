using IxIFlow.Core;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "tracing")]
[Trait("Spec", "G-01")]
public sealed class WaitReferenceTests
{
    [Theory]
    [InlineData("instance", "wait", "66f7b4a9d600b9f7")]
    [InlineData("workflow-实例", "wait-ø", "d33e219df914c826")]
    [InlineData("other", "wait", "cd0f6ea61472bf6c")]
    [InlineData("instance", "", "2e646a54666d41f8")]
    [InlineData("instance", " ", "8830a8bc957fb21b")]
    public void ReferenceIsTheFirstSixteenLowercaseHexCharactersOfTheUtf8Sha256(
        string instanceId, string waitId, string expected)
    {
        Assert.Equal(expected, WaitReference.Of(instanceId, waitId));
        var refused = WorkflowStoreRules.RefusedEventTrace(
            new WorkflowEvent(instanceId, "refused", "go", "{}", TargetWaitId: waitId),
            EventRejection.UnknownWait, DateTime.UnixEpoch);
        Assert.Equal(expected, Assert.IsType<ReceivedEventTrace>(refused.Payload).WaitRef);
    }

    [Fact]
    public void ReferenceRequiresNonNullIdentifiers()
    {
        Assert.Throws<ArgumentNullException>(() => WaitReference.Of(null!, "wait"));
        Assert.Throws<ArgumentNullException>(() => WaitReference.Of("instance", null!));
    }
}
