using System.Net;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.Time.Testing;
using static IxIFlow.Tests.Conformance.OperationsHttpFactory;

namespace IxIFlow.Tests.Conformance;

public sealed class CoordinatorUnschedulableHttpTests
{
    [Theory]
    [InlineData(StoreProfiles.LocalMemory)]
    [InlineData(StoreProfiles.LocalSqlite)]
    public async Task ReadPolicyScopeAndResourceAuthorizationPrecedeResultPaging(string profile)
    {
        var time = new FakeTimeProvider();
        await using var database = await StoreProfiles.CreateAsync(profile, time);
        var store = database.NewRepository(time);
        foreach (var id in new[] { "00-hidden", "01-first", "02-denied", "03-second" })
        {
            var definition = Workflow.Create<Data>("Unplaced").Demand("region", "eu").Step<Noop>().Build();
            var (instance, checkpoint, scopes) = WorkflowInstances.NewInstance(definition, new Data(),
                new() { InstanceId = id, CorrelationId = id == "00-hidden" ? "hidden" : "visible" }, time.GetUtcNow().UtcDateTime);
            ExecutionCheckpointWriter.Prepare(instance, checkpoint, new Data(), scopes, time.GetUtcNow().UtcDateTime);
            await store.SeedAsync(instance, TimeSpan.Zero);
        }
        await using var factory = new OperationsHttpFactory(store) { DenyInstance = "02-denied" };
        factory.Scope.Instances = query => query with { CorrelationId = "visible" };
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/ops/unschedulable")).StatusCode);
        using var writer = factory.Operator("write");
        Assert.Equal(HttpStatusCode.Forbidden, (await writer.GetAsync("/ops/unschedulable")).StatusCode);
        using var reader = factory.Operator("read");
        using var first = await reader.GetAsync("/ops/unschedulable?limit=1");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var page = JsonDocument.Parse(await first.Content.ReadAsStringAsync()).RootElement;
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal("01-first", item.GetProperty("instanceId").GetString());
        Assert.Contains(item.GetProperty("unmetDemands").EnumerateArray(), d => d.GetProperty("key").GetString() == "region");
        Assert.True(page.GetProperty("storeTimeUtc").GetDateTime() != default);
        var cursor = page.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(cursor));
        using var next = await reader.GetAsync("/ops/unschedulable?limit=1&cursor=" + Uri.EscapeDataString(cursor!));
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        var last = JsonDocument.Parse(await next.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("03-second", Assert.Single(last.GetProperty("items").EnumerateArray()).GetProperty("instanceId").GetString());
        Assert.Equal(JsonValueKind.Null, last.GetProperty("nextCursor").ValueKind);
        foreach (var query in new[] { "limit=0", "limit=1001", "cursor=invalid", "cursor=e30=", "limit=1&limit=2" })
            Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync("/ops/unschedulable?" + query)).StatusCode);
    }
}
