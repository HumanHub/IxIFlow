using System.Data.Common;
using System.Text.Json;
using Dapper;
using IxIFlow.Core;

namespace IxIFlow.Relational;

public abstract partial class RelationalWorkflowStateRepository
{
    private async Task<bool> TimersFrozenAsync(DbConnection connection, DbTransaction transaction, string id)
    {
        var json = await connection.QuerySingleOrDefaultAsync<string>(RequiredSql(Statement.DeleteState),
            new { InstanceId = id }, transaction);
        return json != null && WorkflowTimerRules.Frozen(JsonSerializer.Deserialize<WorkflowInstance>(json)!);
    }

    private async Task<Dictionary<string, StoredJson>> TimerPartsAsync(DbConnection connection, DbTransaction transaction, string id)
    {
        using var read = await connection.QueryMultipleAsync(RequiredSql(Statement.ReadParts), new { Ids = new[] { id } }, transaction);
        return (await read.ReadAsync<StoredPart>()).ToDictionary(p => p.PartKey, p => StoredJson.Inline(p.Json));
    }

    private async Task<Dictionary<string, StoredWait>> TimerWaitsAsync(DbConnection connection, DbTransaction transaction, string id)
    {
        var sql = DebugSqlServer
            ? "SELECT InstanceId, WaitId, Kind, WaitKey, EventType, DeadlineUtc, OpenedSequence, OpenedAtUtc, CloseReason, ClosedAtUtc, DefaultPayloadJson FROM dbo.IxIFlowWorkflowWaits WHERE InstanceId = @InstanceId AND CloseReason IS NULL"
            : $"SELECT {WaitColumns} FROM ixiflow_workflow_waits WHERE instance_id = @InstanceId AND close_reason IS NULL";
        return (await connection.QueryAsync<WaitRow>(sql, new { InstanceId = id }, transaction))
            .Select(w => w.Read(ReadTime)).ToDictionary(w => w.WaitId);
    }

    private async Task<TimerCommitResult> ReadTimersAsync(DbConnection connection, DbTransaction transaction, WorkflowInstance instance) =>
        WorkflowTimerRules.Read(instance, await TimerPartsAsync(connection, transaction, instance.InstanceId),
            (await TimerWaitsAsync(connection, transaction, instance.InstanceId)).Values);

    private async Task<TimerCommitResult> ApplyTimersAsync(DbConnection connection, DbTransaction transaction,
        WorkflowInstance? previous, WorkflowInstance next, IReadOnlyList<DebugCommand> commands, DateTime now, List<string> traces)
    {
        var parts = await TimerPartsAsync(connection, transaction, next.InstanceId);
        var originalParts = new Dictionary<string, StoredJson>(parts);
        var waits = await TimerWaitsAsync(connection, transaction, next.InstanceId);
        var originalWaits = new Dictionary<string, StoredWait>(waits);
        var result = WorkflowTimerRules.Apply(previous, next, parts, waits, commands, now, traces);
        foreach (var wait in waits.Values.Where(w => w.DeadlineUtc != originalWaits[w.WaitId].DeadlineUtc))
            await connection.ExecuteAsync(DebugSqlServer
                    ? "UPDATE dbo.IxIFlowWorkflowWaits SET DeadlineUtc = @Deadline WHERE InstanceId = @InstanceId AND WaitId = @WaitId"
                    : "UPDATE ixiflow_workflow_waits SET deadline_utc = @Deadline WHERE instance_id = @InstanceId AND wait_id = @WaitId",
                WithTimes(new { next.InstanceId, wait.WaitId }, ("Deadline", wait.DeadlineUtc)), transaction);
        var changed = parts.Where(p => !originalParts.TryGetValue(p.Key, out var old) || old != p.Value)
            .Select(p => new CheckpointPart(p.Key, p.Value)).ToArray();
        foreach (var batch in changed.Chunk(StatementBatchSize))
        {
            await connection.ExecuteAsync(RequiredSql(Statement.DeleteParts), new { next.InstanceId, Keys = batch.Select(p => p.Key).ToArray() }, transaction);
            await InsertPartsAsync(connection, transaction, next.InstanceId, batch);
        }
        return result;
    }
}
