using System.Reflection;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

public sealed partial class DebugPayloadFixTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task H_TraceHistoryRemainsReadableWhenABranchPayloadIsCorrupt(bool sqlite)
    {
        await using var h = new Harness(sqlite);
        var started = await h.Client.RunAsync(Workflow.Create<Data>("trace-without-payload").Step<Noop>()
            .Parallel(p => p.Do(b => b.WaitFor<Go>("go")).Do(b => b.Step<Noop>())).Build(), new Data());
        var saved = (await h.Store.GetWorkflowInstanceAsync(started.InstanceId))!;
        var branch = saved.SavedParts!.First(p => p.Key.StartsWith("c/")).Value.Reference!;
        var wait = Assert.Single(await h.Store.GetOpenWaitsAsync(new(InstanceId: started.InstanceId)));
        await CorruptPayload(h, branch, sqlite);
        Assert.Equal(WorkflowErrorKind.PayloadUnavailable, (await h.Client.ResumeAsync(started.InstanceId, "go", new Go())).ErrorKind);
        var trace = await h.Services.GetRequiredService<IWorkflowOperations>().GetTraceAsync(started.InstanceId);
        Assert.NotEmpty(trace);
        Assert.DoesNotContain(wait.WaitId, JsonSerializer.Serialize(trace), StringComparison.Ordinal);
    }

    [Fact]
    public async Task I_MemoryDiscoveryAllocatesOnlyABoundedCandidatePage()
    {
        await using var h = new Harness(false);
        for (var index = 0; index < 1000; index++)
            await h.Store.PayloadStore.PutAsync(System.Text.Encoding.UTF8.GetBytes(index.ToString()));
        await h.Store.PayloadStore.FindCollectionCandidatesAsync(1, TimeSpan.Zero);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var page = await h.Store.PayloadStore.FindCollectionCandidatesAsync(1, TimeSpan.Zero);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Single(page);
        Assert.True(allocated < 8000, $"A one-row candidate page allocated {allocated} bytes");
    }

    [Fact]
    public async Task I_SqliteDiscoveryDoesNotMaterializeMetadataBeyondItsLimit()
    {
        await using var h = new Harness(true);
        var references = new List<PayloadReference>();
        for (var index = 0; index < 20; index++)
            references.Add(await h.Store.PayloadStore.PutAsync(System.Text.Encoding.UTF8.GetBytes(index.ToString())));
        var ordered = references.OrderBy(r => r.Digest, StringComparer.Ordinal).ToArray();
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(h.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        // Preserve the fields needed for eligibility, but poison a later row's deserialization.
        command.CommandText = "UPDATE ixiflow_workflow_payloads SET metadata_json=json_set(metadata_json, '$.Reference.Bytes', 'invalid') WHERE digest=$digest";
        command.Parameters.AddWithValue("$digest", ordered[^1].Digest);
        await command.ExecuteNonQueryAsync();
        Assert.Equal(ordered.Take(1), await h.Store.PayloadStore.FindCollectionCandidatesAsync(1, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task M_StepTableResolutionDoesNotInspectCheckpointPayloads(bool sqlite)
    {
        await using var h = new Harness(sqlite);
        var started = await h.Client.RunAsync(Workflow.Create<Data>("definition-without-inspection").Step<Noop>()
            .Parallel(p => p.Do(b => b.WaitFor<Go>("go")).Do(b => b.Step<Noop>())).Build(), new Data());
        var saved = (await h.Store.GetWorkflowInstanceAsync(started.InstanceId))!;
        await CorruptPayload(h, saved.SavedParts!.First(p => p.Key.StartsWith("c/")).Value.Reference!, sqlite);
        Assert.NotEmpty(await h.Services.GetRequiredService<IWorkflowOperations>().GetStepTableAsync(started.InstanceId));
    }

    private static async Task CorruptPayload(Harness h, PayloadReference reference, bool sqlite)
    {
        if (sqlite)
        {
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(h.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE ixiflow_workflow_payloads SET body=zeroblob(bytes) WHERE digest=$digest";
            command.Parameters.AddWithValue("$digest", reference.Digest);
            await command.ExecuteNonQueryAsync();
        }
        else
        {
            var payloads = h.Store.PayloadStore;
            var rows = (Dictionary<string, PayloadRecord>)payloads.GetType().GetField("_rows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(payloads)!;
            rows[reference.Digest].Body = new byte[(int)reference.Bytes];
        }
    }
}
