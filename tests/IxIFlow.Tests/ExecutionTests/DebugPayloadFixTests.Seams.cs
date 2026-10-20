using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

public sealed partial class DebugPayloadFixTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task G_InboxAdmissionStoresAndRetainsAReferenceUntilInstanceDeletion(bool sqlite)
    {
        await using var h = new Harness(sqlite);
        var started = await h.Client.RunAsync(Workflow.Create<Data>("inbox-seam").Step<Noop>().WaitFor<Data>("data").Build(), new Data());
        var result = await h.Client.ResumeAsync(started.InstanceId, "data", new Data { Text = Large }, "large-event");
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var stored = (await h.Store.GetEventAsync(started.InstanceId, "large-event"))!;
        Assert.NotNull(stored.PayloadJson.Reference);
        Assert.False(await h.Store.PayloadStore.CollectAsync(stored.PayloadJson.Reference!, TimeSpan.Zero));
        await h.Store.DeleteWorkflowInstanceAsync(started.InstanceId);
        Assert.True(await h.Store.PayloadStore.CollectAsync(stored.PayloadJson.Reference!, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task G_LargeChildInputAndCompletionStayReferencedAndSurviveChildDeletion(bool sqlite)
    {
        await using var h = new Harness(sqlite);
        var catalog = h.Services.GetRequiredService<IWorkflowCatalog>();
        await catalog.RegisterWorkflowAsync(Workflow.Create<Data>("large-child").Step<Noop>().Build());
        var parent = Workflow.Create<Data>("large-parent").Step<Noop>()
            .Invoke<Data>("large-child", 1, s => s.Input(d => d.Text).From(c => c.WorkflowData.Text)
                .Output(d => d.Text).To(c => c.WorkflowData.Text)).WaitFor<Go>("parent-end").Build();
        var first = await h.Client.RunAsync(parent, new Data { Text = new string('y', 8 * 1024 * 1024) });
        var child = Assert.Single((await h.Store.QueryInstancesAsync(new InstanceQuery(ParentInstanceId: first.InstanceId))).Items);
        var savedChild = (await h.Store.GetWorkflowInstanceAsync(child.InstanceId))!;
        for (var attempt = 0; attempt < 200 && !WorkflowStoreRules.IsTerminal(savedChild.Status); attempt++)
        {
            await h.Client.RecoverAsync(child.InstanceId);
            await Task.Delay(10);
            savedChild = (await h.Store.GetWorkflowInstanceAsync(child.InstanceId))!;
        }
        Assert.NotNull(savedChild.WorkflowDataJson.Reference);
        Assert.Equal(WorkflowStatus.Completed, savedChild.Status);
        var notice = (await h.Store.GetEventAsync(first.InstanceId, "child-completed-" + child.InstanceId))!;
        var reader = new PayloadReader(async r => (await h.Store.PayloadStore.GetAsync(r))!.Value, new(32 * 1024 * 1024));
        var completion = JsonSerializer.Deserialize<ChildCompleted>(await reader.ReadAsync(notice.PayloadJson))!;
        Assert.Equal(savedChild.WorkflowDataJson.Reference, completion.DataJson.Reference);
        await h.Client.RecoverAsync(first.InstanceId);
        await h.Store.DeleteWorkflowInstanceAsync(child.InstanceId);
        Assert.False(await h.Store.PayloadStore.CollectAsync(completion.DataJson.Reference!, TimeSpan.Zero));
        var done = await h.Client.ResumeAsync(first.InstanceId, "parent-end", new Go());
        Assert.Equal(WorkflowExecutionStatus.Success, done.Status);
        Assert.Equal(8 * 1024 * 1024, Assert.IsType<Data>(done.WorkflowData).Text.Length);
    }

    [Fact]
    public async Task G_LegacyInlineInboxSchemaRequiresRecreation()
    {
        await using var h = new Harness(true);
        await h.Store.GetWorkflowInstanceSummaryAsync("absent");
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(h.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM pragma_table_info('ixiflow_workflow_events') WHERE name='stored_payload_json'";
            Assert.Equal(1L, await command.ExecuteScalarAsync());
            command.CommandText = "ALTER TABLE ixiflow_workflow_events RENAME COLUMN stored_payload_json TO payload_json";
            await command.ExecuteNonQueryAsync();
        }
        await ((SqliteWorkflowStateRepository)h.Store).DisposeAsync();
        await using var reopened = new SqliteWorkflowStateRepository(h.ConnectionString);
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => reopened.GetWorkflowInstanceSummaryAsync("absent"));
        Assert.Equal(WorkflowErrorKind.InvalidState, error.Kind);
        Assert.Contains("recreated", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task G_ConflictOrMissingInstanceDoesNotPublishEventBodies(bool sqlite)
    {
        await using var h = new Harness(sqlite);
        var firstBytes = JsonSerializer.Serialize(new Data { Text = Large });
        var arrival = new WorkflowEvent("absent", "event", typeof(Data).AssemblyQualifiedName!, firstBytes, "data")
            { PayloadThresholdBytes = 64 };
        Assert.Equal(EventAppendStatus.NotFound, (await h.Store.AppendEventAsync(arrival)).Status);
        Assert.Null(await h.Store.PayloadStore.GetAsync(PayloadReference.Of(System.Text.Encoding.UTF8.GetBytes(firstBytes))));
        var started = await h.Client.RunAsync(Workflow.Create<Data>("event-rollback").Step<Noop>().WaitFor<Data>("data").Build(), new Data());
        arrival = arrival with { InstanceId = started.InstanceId };
        Assert.Equal(EventAppendStatus.Stored, (await h.Store.AppendEventAsync(arrival)).Status);
        var conflictingBytes = JsonSerializer.Serialize(new Data { Text = Large + "conflict" });
        Assert.Equal(EventAppendStatus.Conflict, (await h.Store.AppendEventAsync(arrival with { PayloadJson = conflictingBytes })).Status);
        Assert.Null(await h.Store.PayloadStore.GetAsync(PayloadReference.Of(System.Text.Encoding.UTF8.GetBytes(conflictingBytes))));
    }
}
