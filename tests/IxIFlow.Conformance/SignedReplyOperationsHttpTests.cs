using System.Net;
using System.Net.Http.Json;
using IxIFlow.AspNetCore;
using IxIFlow.Builders;
using IxIFlow.Core;
using static IxIFlow.Tests.Conformance.OperationsHttpFactory;

namespace IxIFlow.Tests.Conformance;

public sealed class SignedReplyOperationsHttpTests
{
    [Fact]
    public async Task InspectAloneCanReadTraceAndSnapshotButCannotDiscoverTokens()
    {
        await using var factory = new OperationsHttpFactory(new InMemoryWorkflowStateRepository());
        using var http = factory.Operator("inspect");
        var id = (await factory.Engine.RunAsync(Waiting(), new Data())).InstanceId;
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"/ops/instances/{id}/trace")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"/ops/instances/{id}/snapshot")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"/ops/instances/{id}/waits?tokens=true")).StatusCode);
        factory.DenyInstance = id;
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"/ops/instances/{id}/trace")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync($"/ops/instances/{id}/snapshot")).StatusCode);
    }

    [Fact]
    public async Task TokenListingsSignEveryRowAndUseReferencesAndAuthenticatedPagination()
    {
        await using var factory = new OperationsHttpFactory(new InMemoryWorkflowStateRepository());
        using var http = factory.Operator("reply");
        for (var i = 0; i < 3; i++) await factory.Engine.RunAsync(Waiting(), new Data());
        var seen = new List<string>();
        string? after = null;
        do
        {
            using var response = await http.GetAsync("/ops/waits?limit=1" + (after == null ? "" : "&after=" + after));
            var json = await response.Content.ReadAsStringAsync();
            var page = (await response.Content.ReadFromJsonAsync<WorkflowWaitPage>())!;
            var row = Assert.Single(page.Items); seen.Add(row.InstanceId);
            Assert.True(ReplyToken.TryVerify(TestReplyKeys.Ring, row.Token, out var instance, out var wait, out _));
            Assert.Equal(row.InstanceId, instance); Assert.Equal(WaitReference.Of(instance, wait), row.WaitRef);
            Assert.DoesNotContain("waitId", json, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain(wait, json);
            after = page.NextAfter;
            if (after != null)
            {
                Assert.True(WaitPageCursor.TryRead(TestReplyKeys.Ring, after, out var boundary));
                Assert.Equal(wait, boundary);
                Assert.False(ReplyToken.TryVerify(TestReplyKeys.Ring, after, out _, out _, out _));
            }
        } while (after != null);
        Assert.Equal(3, seen.Distinct().Count());
    }

    private static WorkflowDefinition Waiting() => Workflow.Create<Data>("SignedHttpWait").Step<Noop>().WaitFor<Approval>("approval").Build();
}
