using System.Net;
using System.Net.Http.Json;
using IxIFlow.AspNetCore;
using IxIFlow.Builders;
using IxIFlow.Core;
using static IxIFlow.Tests.Conformance.OperationsHttpFactory;

namespace IxIFlow.Tests.Conformance;

public sealed class ReplyOperationsHttpTests
{
    [Fact]
    public async Task TokenListingsUseCapabilitiesReferencesAndPlainPagination()
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
            Assert.True(ReplyToken.TryRead(row.Token, out var instance, out var wait, out _));
            Assert.Equal(row.InstanceId, instance); Assert.Equal(WaitReference.Of(instance, wait), row.WaitRef);
            Assert.DoesNotContain("waitId", json, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain(wait, json);
            after = page.NextAfter;
            if (after != null)
            {
                Assert.True(WaitPageCursor.TryRead(after, out var boundary));
                Assert.Equal(row.WaitRef, boundary);
                Assert.DoesNotContain(wait, System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(after + "==")));
                Assert.False(ReplyToken.TryRead(after, out _, out _, out _));
            }
        } while (after != null);
        Assert.Equal(3, seen.Distinct().Count());
    }

    private static WorkflowDefinition Waiting() => Workflow.Create<Data>("HttpWait").Step<Noop>().WaitFor<Approval>("approval").Build();
}
