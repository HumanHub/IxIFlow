using System.Net;
using System.Text.Json;
using IxIFlow.BundleFixtures;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

[Trait("Spec", "H-04")]
[Trait("Feature", "registration")]
public sealed class BundleInspectionHttpTests
{
    [Theory]
    [InlineData("snapshot")]
    [InlineData("waits?tokens=true")]
    public async Task AnOversizedBundleOnInspectionReportsTheServerBundleLimit(string route)
    {
        var root = Path.Combine(Path.GetTempPath(), "IxIFlow.BundleInspection", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new InMemoryWorkflowStateRepository();
            var version = await PublishedBundleScenario.PublishAsync(store);
            string id;
            await using (var starter = PublishedBundleScenario.Host(store, Path.Combine(root, "starter")))
            {
                id = await starter.GetRequiredService<IWorkflowClient>().StartAsync(version.Name, new FirstData());
                await PublishedBundleScenario.UntilAsync(store, id, WorkflowStatus.Suspended);
            }
            var checkpoint = (await store.GetWorkflowInstanceAsync(id))!.ExecutionStateJson;
            using var catalog = new WorkflowCatalog(store, artifacts: store, bundles: new(Path.Combine(root, "limited"))
            { ReadLimits = new() { MaxArchiveBytes = 1 } });
            await using var app = new OperationsHttpFactory(store, catalog);
            using var client = app.Operator();
            using var response = await client.GetAsync($"/ops/instances/{id}/{route}");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("Workflow bundle exceeds the configured read limit", problem.RootElement.GetProperty("title").GetString());
            Assert.Equal(checkpoint, (await store.GetWorkflowInstanceAsync(id))!.ExecutionStateJson);
            Assert.Equal(WorkflowStatus.Suspended, (await store.GetWorkflowInstanceSummaryAsync(id))!.Status);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
