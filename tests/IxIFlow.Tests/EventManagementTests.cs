using IxIFlow.Core;
using Microsoft.Extensions.Logging;
using Moq;

namespace IxIFlow.Tests;

[Trait("Feature", "waits")]
public class EventManagementTests
{
    [Fact]
    public async Task TargetedEventReportsBusyInstanceForRetry()
    {
        var engine = new Mock<IWorkflowEngine>();
        engine.Setup(item => item.ResumeWorkflowAsync(
                "busy-instance", It.IsAny<TestEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowExecutionResult
            {
                InstanceId = "busy-instance",
                Status = WorkflowExecutionStatus.Running,
                EventAccepted = false
            });
        var manager = new SuspensionManager(new InMemoryWorkflowStateRepository(),
            Mock.Of<IEventCorrelator>(), engine.Object, Mock.Of<ILogger<SuspensionManager>>());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.ResumeInstanceAsync("busy-instance", new TestEvent()));
    }

    [Fact]
    public async Task CorrelatedEventReportsBusyInstanceForRetry()
    {
        var engine = new Mock<IWorkflowEngine>();
        engine.Setup(item => item.ResumeWorkflowAsync(
                "busy-instance", It.IsAny<TestEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowExecutionResult
            {
                InstanceId = "busy-instance",
                Status = WorkflowExecutionStatus.Running,
                EventAccepted = false
            });
        var correlator = new Mock<IEventCorrelator>();
        correlator.Setup(item => item.FindMatchingWorkflowsAsync(
                It.IsAny<TestEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new WorkflowInstance { InstanceId = "busy-instance" }]);
        var manager = new SuspensionManager(new InMemoryWorkflowStateRepository(),
            correlator.Object, engine.Object, Mock.Of<ILogger<SuspensionManager>>());

        var error = await Assert.ThrowsAsync<WorkflowEventDeliveryException>(() =>
            manager.ProcessEventAsync(new TestEvent()));
        Assert.Equal(["busy-instance"], error.PendingInstanceIds);
        Assert.Empty(error.AcceptedInstanceIds);
    }

    // Test event classes
    public class TestEvent
    {
        public string ApprovalStatus { get; set; } = "";
    }
}
