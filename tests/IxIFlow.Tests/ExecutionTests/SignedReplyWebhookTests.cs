using IxIFlow.Core;
using Moq;

namespace IxIFlow.Tests.ExecutionTests;

public sealed partial class IncomingWebhookEndpointTests
{
    [Theory]
    [InlineData(false), InlineData(true)]
    public async Task AnInvalidReplyTokenIsRefusedBeforeTheWebhookReadsTheStore(bool signedWebhook)
    {
        var store = new Mock<IWorkflowStateRepository>(MockBehavior.Strict);
        await using var host = new Harness(store.Object);
        var wrongRing = new ReplyTokenKeyRing(Enumerable.Repeat((byte)81, 32).ToArray());
        foreach (var token in new[] { "not-a-token", ReplyToken.Sign(wrongRing, "instance", "wait"),
                     ReplyToken.Sign(TestReplyKeys.Ring, "another-instance", "wait") })
        {
            var response = await host.Send(signedWebhook ? "/signed/workflows/{id}/events/{key}" : "/workflows/{id}/events/{key}",
                Cloud(), instanceId: "instance", contentType: "application/cloudevents+json",
                secret: signedWebhook ? EventOldSecret : null,
                headers: new() { ["X-IxIFlow-Reply-Token"] = token });
            Assert.Equal(400, response.Status);
            Assert.DoesNotContain(token, response.Body);
        }
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task TheWebhookAnswersASignedTokenThroughEngineIngress()
    {
        await using var host = new Harness();
        var start = await host.Send("/orders/{orderId}");
        await host.Client.AwaitAsync(start.Id).WaitAsync(TimeSpan.FromSeconds(10));
        var wait = Assert.Single(await host.Client.GetOpenWaitsAsync(new(InstanceId: start.Id)));
        var response = await host.Send("/workflows/{id}/events/{key}", Cloud(), instanceId: start.Id,
            eventKey: "a-key-that-does-not-match", contentType: "application/cloudevents+json",
            headers: new() { ["X-IxIFlow-Reply-Token"] = wait.Token });
        Assert.Equal(202, response.Status);
        Assert.Equal(WorkflowExecutionStatus.Success, (await host.Client.AwaitAsync(start.Id, WorkflowAwaitCondition.Finished)
            .WaitAsync(TimeSpan.FromSeconds(10))).Status);
    }
}
