using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;

namespace IxIFlow.Tests.ExecutionTests;

public sealed partial class IncomingWebhookEndpointTests
{
    [Fact]
    public async Task TriggerPoliciesForbidAuthenticatedCallersOnStartStatusAndEvents()
    {
        await using var host = new Harness(
            registrations: triggers =>
            {
                var definition = Workflow.Create<Data>("DeniedTrigger").Step<Noop>().WaitFor<Approval>("approval").Build();

                triggers.Start<Data>("denied", "/denied", definition)
                    .FromBody<Data>((data, _) => data).Authorize("vendor");

                triggers.Status().Authorize("vendor").AuthorizeInstance("instance");
                triggers.Events("denied-events").Allow<Approval>("approved").Authorize("vendor").AuthorizeInstance("instance");
            });

        Assert.Equal(403, (await host.Send("/denied")).Status);
        Assert.Equal(403, (await host.Send("/workflows/{id}", instanceId: "denied")).Status);

        Assert.Equal(
            403, (await host.Send(
                "/workflows/{id}/events/{key}", Cloud(), instanceId: "denied",
                contentType: "application/cloudevents+json")).Status);

        Assert.Empty((await host.Store.QueryInstancesAsync(new InstanceQuery())).Items);
        Assert.Empty((await host.Store.GetPendingEventsAsync("denied", 0, 100)).Events);
    }

    [Theory]
    [Trait("Spec", "D-16")]
    [InlineData("UnknownWait")]
    [InlineData("SubjectMismatch")]
    [InlineData("WaitAnswered")]
    public async Task ReplyCapabilitiesReportTheirRefusalKindThroughTheWebhook(string refusal)
    {
        await using var host = new Slice4ReplyHarness(
            registrations: triggers =>
            {
                var definition = Workflow.Create<Data>("ReplyRefusals")
                    .Request<Slice4Request>("approval", subject: _ => "order")
                    .WaitFor<Approval>("approval")
                    .WaitFor<Approval>("finish")
                    .Build();

                triggers.Start<Data>("reply", "/reply", definition).FromBody<Data>((data, _) => data).Authorize("api");
                triggers.Status().Authorize("api").AuthorizeInstance("instance");
                triggers.Events("reply-events").Allow<Approval>("approved").Authorize("api").AuthorizeInstance("instance");
            });

        var start = await host.Send("/reply");
        Assert.Equal(202, start.Status);
        var suspended = await host.Client.AwaitAsync(start.Id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(suspended.Status == WorkflowExecutionStatus.Suspended, suspended.ErrorMessage);
        var issued = Assert.IsType<string>(host.Probe.Token);
        Assert.True(ReplyToken.TryRead(issued, out var instance, out var waitId, out var subject));
        Assert.Equal(ReplyToken.Digest("order"), subject);

        var token = refusal switch
        {
            "UnknownWait" => ReplyToken.Create(instance, "unknown-wait", subject),
            "SubjectMismatch" => ReplyToken.Create(instance, waitId, ReplyToken.Digest("another-order")),
            _ => issued
        };

        if (refusal == "WaitAnswered")
        {
            var accepted = await host.Send(
                "/workflows/{id}/events/{key}", Cloud("first"), instanceId: start.Id,
                contentType: "application/cloudevents+json", headers: new() { ["X-IxIFlow-Reply-Token"] = issued });

            Assert.Equal(202, accepted.Status);

            await IxIFlow.Tests.Infrastructure.TestEventually.UntilAsync(
                start.Id, "the first reply wait to be answered",
                () => host.Store.GetWaitAsync(start.Id, waitId), stored => stored?.CloseReason == WaitCloseReason.Answered);

            await host.Client.AwaitAsync(start.Id).WaitAsync(TimeSpan.FromSeconds(10));
        }

        var response = await host.Send(
            "/workflows/{id}/events/{key}", Cloud("refused"), instanceId: start.Id,
            contentType: "application/cloudevents+json", headers: new() { ["X-IxIFlow-Reply-Token"] = token });

        Assert.Equal(409, response.Status);
        using var problem = JsonDocument.Parse(response.Body);
        Assert.Equal(refusal, problem.RootElement.GetProperty("detail").GetString());
        Assert.DoesNotContain(token, response.Body);
        Assert.Equal(WorkflowExecutionStatus.Suspended, await host.Client.GetStatusAsync(start.Id));
    }

    public sealed class Slice4Request(Slice4ReplyProbe probe) : IRequestActivity<Approval>
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.Token = context.Reply!.Token;

            return Task.CompletedTask;
        }
    }

    public sealed class Slice4ReplyProbe
    {
        public string? Token { get; set; }
    }
}