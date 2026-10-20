using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

public sealed partial class DebugPayloadFixTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task J_DelayedCommandCannotResumeAReusedIdAtTheSamePauseGeneration(bool sqlite)
    {
        await using var h = new Harness(sqlite);
        var ops = h.Services.GetRequiredService<IWorkflowOperations>();
        var workflow = Workflow.Create<Data>("reused-debug-id").Step<Noop>().WaitFor<Go>("go").Build();
        var started = await h.Client.RunAsync(workflow, new Data());
        await ops.PauseAsync(started.InstanceId, "owner");
        var old = (await h.Store.GetWorkflowInstanceAsync(started.InstanceId))!;
        var delayed = JsonSerializer.Deserialize<DebugCommandRequest>(JsonSerializer.Serialize(new
        {
            Id = "delayed", Kind = DebugCommandKind.Continue, PauseGeneration = 1, Actor = "owner", ExpectedIncarnation = old.Incarnation
        }))!;
        await h.Store.DeleteWorkflowInstanceAsync(started.InstanceId, TimeSpan.FromSeconds(1));
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        await h.Client.RunAsync(workflow, new Data(), new() { InstanceId = started.InstanceId });
        await ops.PauseAsync(started.InstanceId, "owner");
        var replacement = (await h.Store.GetWorkflowInstanceAsync(started.InstanceId))!;
        Assert.NotEqual(old.Incarnation, replacement.Incarnation);
        Assert.Equal(DebugCommandStatus.InvalidState, (await h.Store.RequestDebugCommandAsync(started.InstanceId, delayed)).Status);
        await Assert.ThrowsAsync<WorkflowEngineException>(() => ops.ContinueAsync(started.InstanceId, "owner", expectedIncarnation: old.Incarnation));
        Assert.Equal(WorkflowStatus.Paused, (await h.Store.GetWorkflowInstanceAsync(started.InstanceId))!.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task K_PauseIsRefusedForAnOperatorStoppedInstance(bool sqlite)
    {
        await using var h = new Harness(sqlite);
        var started = await h.Client.RunAsync(Workflow.Create<Data>("stopped-debug").Step<Noop>().WaitFor<Go>("go").Build(), new Data());
        var saved = (await h.Store.GetWorkflowInstanceAsync(started.InstanceId))!;
        var lease = (await h.Store.TryAcquireExecutionLeaseAsync(started.InstanceId, "stop", TimeSpan.FromSeconds(30)))!;
        Assert.Equal(WorkflowCommitStatus.Applied, (await h.Store.CommitWorkflowInstanceAsync(
            CheckpointCommit.PayloadStop(saved, lease.Epoch, "operator stop", h.Clock.GetUtcNow().UtcDateTime))).Status);
        Assert.Equal(DebugCommandStatus.InvalidState, (await h.Store.RequestDebugCommandAsync(started.InstanceId,
            new("pause-stop", DebugCommandKind.Pause, 0, "owner"))).Status);
        Assert.Empty((await h.Store.GetDebugStatesAsync([started.InstanceId]))[started.InstanceId].Commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task L_ControlWatchReadsCommandsAndCancellationWithoutTheTrunk(bool sqlite)
    {
        await using var h = new Harness(sqlite);
        var started = await h.Client.RunAsync(Workflow.Create<Data>("controls-without-trunk").Step<Noop>().WaitFor<Go>("go").Build(), new Data());
        var operations = h.Services.GetRequiredService<IWorkflowOperations>();
        await operations.PauseAsync(started.InstanceId, "owner");
        await operations.ContinueAsync(started.InstanceId, "owner");
        Assert.Equal(1, (await h.Store.GetDebugStatesAsync([started.InstanceId]))[started.InstanceId].PauseGeneration);
        await h.Store.RequestDebugCommandAsync(started.InstanceId, new("pending-pause", DebugCommandKind.Pause, 1, "owner"));
        await h.Store.RequestCancellationAsync(started.InstanceId, new() { ReasonCode = "cancel", RequestedBy = "owner" });
        if (sqlite)
        {
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(h.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE ixiflow_workflow_instances SET state_json='unreadable' WHERE instance_id=$id";
            command.Parameters.AddWithValue("$id", started.InstanceId);
            await command.ExecuteNonQueryAsync();
        }
        else
        {
            var rows = (System.Collections.Concurrent.ConcurrentDictionary<string, WorkflowInstance>)h.Store.GetType()
                .GetField("_instances", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(h.Store)!;
            rows[started.InstanceId].ExecutionStateJson = "unreadable";
        }
        var controls = await h.Store.GetControlRequestsAsync([started.InstanceId]);
        Assert.Equal("cancel", controls.Cancellations[started.InstanceId].ReasonCode);
        Assert.Equal("pending-pause", Assert.Single(controls.DebugStates[started.InstanceId].Commands,
            c => c.AcknowledgedRevision == null).Id);
        Assert.Equal(5, controls.DebugStates[started.InstanceId].Version);
        if (!sqlite) Assert.Equal(1, controls.DebugStates[started.InstanceId].PauseGeneration);
    }
}
