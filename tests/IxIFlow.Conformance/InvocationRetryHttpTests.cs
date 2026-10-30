using System.Net;
using System.Net.Http.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

[Trait("Spec", "D-18")]
public sealed class InvocationRetryHttpTests
{
    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ResolutionHttpRequiresTheObservedPolicyAttemptAndRejectsStaleFailAndComplete(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();

        await using var factory = new OperationsHttpFactory(store)
        {
            ConfigureServices = services =>
            {
                services.AddIxIFlow(options => options.MaxCheckpointBytes = 18_000);
                database.ConfigureHost(services);
            },
            Wait = TimeSpan.FromSeconds(5)
        };

        using var http = factory.Operator();
        await database.StartHostLinksAsync(factory.Services);
        var child = Workflow.Create<Data>("HttpRefusedChild").Build();
        await factory.Services.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(child);

        var parent = new WorkflowBuilder<Data, object>([], "HttpInvocationRetry")
            .Invoke<Data>(child.Name, 1, setup => setup.Input(data => data.Value).From(_ => new string('x', 100_000))
                .OnError(error => error.ThenRetry(new RetryPolicy(1)))).Build();

        var stopped = await factory.Engine.RunAsync(parent, new Data());
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, stopped.Status);

        var first = Assert.Single(
            (await http.GetFromJsonAsync<PendingActivityInfo[]>($"/ops/instances/{stopped.InstanceId}/pending-activities"))!);

        Assert.NotNull(first.PolicyAttemptId);
        var url = $"/ops/instances/{stopped.InstanceId}/resolutions";
        var missing = await http.PostAsJsonAsync(url, new { first.InvocationId, Kind = "Failed", Note = "observed" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        var decided = await http.PostAsJsonAsync(url,
            new { first.InvocationId, Kind = "Failed", Note = "observed", ExpectedPolicyAttemptId = first.PolicyAttemptId });

        Assert.True(decided.IsSuccessStatusCode, await decided.Content.ReadAsStringAsync());
        var second = Assert.Single(await factory.Engine.GetPendingActivitiesAsync(stopped.InstanceId));
        Assert.NotEqual(first.PolicyAttemptId, second.PolicyAttemptId);
        var revision = (await store.GetWorkflowInstanceAsync(stopped.InstanceId))!.Revision;

        foreach (var kind in new[] { "Failed", "Completed" })
        {
            var stale = await http.PostAsJsonAsync(url, new
            {
                first.InvocationId, Kind = kind, Note = "stale", ExpectedPolicyAttemptId = first.PolicyAttemptId,
                InvocationResult = new Data { Value = "stale result" }
            });

            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            Assert.Equal(revision, (await store.GetWorkflowInstanceAsync(stopped.InstanceId))!.Revision);
        }

        var completed = await http.PostAsJsonAsync(url, new
        {
            second.InvocationId, Kind = "Completed", Note = "observed", ExpectedPolicyAttemptId = second.PolicyAttemptId,
            InvocationResult = new Data { Value = "observed result" }
        });

        Assert.True(completed.IsSuccessStatusCode, await completed.Content.ReadAsStringAsync());
        Assert.Equal(WorkflowStatus.Completed, (await store.GetWorkflowInstanceAsync(stopped.InstanceId))!.Status);
    }

    public sealed class Data
    {
        public string Value { get; set; } = "";
    }
}