using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "host")]
[Trait("Spec", "H-04")]
public sealed class Slice4OperationsHttpScenarios
{
    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ResourceDenialPrecedesEveryPostOperationAndWritesNoTrace(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var repository = database.NewRepository();
        await using var factory = new OperationsHttpFactory(repository);
        using var http = factory.Operator();

        var definition = Workflow.Create<Data>("DeniedPostOperations")
            .Step<Noop>().WaitFor<string>("answer").Build();

        var id = (await factory.Engine.RunAsync(definition, new Data())).InstanceId;
        factory.DenyInstance = id;
        var before = await repository.GetTraceAsync(id, 0, 100);
        var row = (await repository.GetWorkflowInstanceAsync(id))!;

        foreach (var suffix in new[] { "resolutions", "release", "cancel", "terminate" })
        {
            using var response = await http.PostAsJsonAsync($"/ops/instances/{id}/{suffix}", new { reasonCode = "denied" });
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await repository.GetTraceAsync(id, 0, 100)));
            Assert.Equal(row.Revision, (await repository.GetWorkflowInstanceAsync(id))!.Revision);
            Assert.Null(await repository.GetCancellationRequestAsync(id));
        }
    }

    public sealed class Data;

    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}