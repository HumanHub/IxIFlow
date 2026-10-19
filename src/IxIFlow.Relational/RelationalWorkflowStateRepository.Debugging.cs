using System.Data.Common;
using System.Text.Json;
using Dapper;
using IxIFlow.Core;
namespace IxIFlow.Relational;

public abstract partial class RelationalWorkflowStateRepository
{
    private bool DebugSqlServer => PayloadSqlDialect == PayloadDialect.SqlServer;
    private string DebugTable => DebugSqlServer ? "dbo.IxIFlowWorkflowDebugCommands" : "ixiflow_workflow_debug_commands";
    private string DebugInstanceTable => DebugSqlServer ? "dbo.IxIFlowWorkflowInstances" : "ixiflow_workflow_instances";
    private string DebugColumn(string name) => DebugSqlServer ? name : name switch
    {
        "InstanceId" => "instance_id", "CommandId" => "command_id", "CommandOrder" => "command_order",
        "CommandJson" => "command_json", "StateJson" => "state_json", "CancellationJson" => "cancellation_json", "RunnableAtUtc" => "runnable_at_utc", _ => throw new ArgumentException(name)
    };
    private async Task<IReadOnlyList<DebugCommand>> ReadDebugCommandsAsync(DbConnection connection, DbTransaction? transaction, string id) =>
        (await connection.QueryAsync<string>($"SELECT {DebugColumn("CommandJson")} FROM {DebugTable} WHERE {DebugColumn("InstanceId")} = @InstanceId ORDER BY {DebugColumn("CommandOrder")}",
            new { InstanceId = id }, transaction)).Select(json => JsonSerializer.Deserialize<DebugCommand>(json)!).ToArray();

    public async Task<DebugCommandResult> RequestDebugCommandAsync(string instanceId, DebugCommandRequest command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(command);
        await EnsureSchemaAsync();
        return await ExecuteOperationAsync(Operation.Commit, async () =>
        {
            await using var connection = await OpenConnectionAsync(ownership: true);
            await using var transaction = (await BeginTransactionAsync(connection, Operation.Commit))!;
            await ExecuteOptionalAsync(connection, transaction, Statement.LockIdentity, new { InstanceId = instanceId });
            var (current, now) = await LockAsync<CommitState>(connection, transaction, Statement.CommitState, instanceId);
            var instance = current?.StateJson == null ? null : JsonSerializer.Deserialize<WorkflowInstance>(current.StateJson);
            var commands = await ReadDebugCommandsAsync(connection, transaction, instanceId);
            var result = WorkflowDebugRules.Validate(instance, commands, command);
            if (result.Status == DebugCommandStatus.Accepted)
            {
                var accepted = result.Command!;
                await connection.ExecuteAsync($"INSERT INTO {DebugTable} ({DebugColumn("InstanceId")}, {DebugColumn("CommandId")}, {DebugColumn("CommandOrder")}, {DebugColumn("CommandJson")}) VALUES (@InstanceId, @Id, @Order, @Json)",
                    new { InstanceId = instanceId, accepted.Id, accepted.Order, Json = JsonSerializer.Serialize(accepted) }, transaction);
                if (WorkflowDebugRules.NeedsWake(instance!.Status, commands.Append(accepted).ToArray()))
                {
                    await connection.ExecuteAsync($"UPDATE {DebugInstanceTable} SET {DebugColumn("RunnableAtUtc")} = @Now WHERE {DebugColumn("InstanceId")} = @InstanceId",
                        WithTimes(new { InstanceId = instanceId }, ("Now", now)), transaction);
                    await NotifyRunnableAsync(connection, transaction);
                }
            }
            await transaction.CommitAsync();
            return result;
        });
    }

    public async Task<IReadOnlyDictionary<string, WorkflowDebugState>> GetDebugStatesAsync(IReadOnlyCollection<string> instanceIds) =>
        (await GetControlRequestsAsync(instanceIds)).DebugStates;

    public async Task<WorkflowControlRequests> GetControlRequestsAsync(IReadOnlyCollection<string> instanceIds)
    {
        ArgumentNullException.ThrowIfNull(instanceIds);
        var states = new Dictionary<string, WorkflowDebugState>(StringComparer.Ordinal);
        var cancellations = new Dictionary<string, CancellationReason>(StringComparer.Ordinal);
        if (instanceIds.Count == 0) return new(cancellations, states);
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync(ownership: true);
        var filter = PayloadSqlDialect == PayloadDialect.PostgreSql ? "= ANY(@Ids)" : "IN @Ids";
        foreach (var batch in instanceIds.Distinct(StringComparer.Ordinal).Chunk(OwnershipReadBatchSize))
        {
            using var rows = await connection.QueryMultipleAsync($"SELECT {DebugColumn("InstanceId")} AS InstanceId, {DebugColumn("StateJson")} AS StateJson, {DebugColumn("CancellationJson")} AS CancellationJson FROM {DebugInstanceTable} WHERE {DebugColumn("InstanceId")} {filter}; SELECT {DebugColumn("InstanceId")} AS InstanceId, {DebugColumn("CommandJson")} AS CommandJson FROM {DebugTable} WHERE {DebugColumn("InstanceId")} {filter} ORDER BY {DebugColumn("CommandOrder")}", new { Ids = batch });
            var instances = (await rows.ReadAsync<(string InstanceId, string StateJson, string? CancellationJson)>()).ToArray();
            var commands = (await rows.ReadAsync<(string InstanceId, string CommandJson)>()).ToLookup(r => r.InstanceId, r => JsonSerializer.Deserialize<DebugCommand>(r.CommandJson)!);
            foreach (var row in instances)
            {
                var instance = JsonSerializer.Deserialize<WorkflowInstance>(row.StateJson)!;
                if (row.CancellationJson is { } reason) cancellations[row.InstanceId] = JsonSerializer.Deserialize<CancellationReason>(reason)!;
                var items = commands[row.InstanceId].ToArray();
                states[row.InstanceId] = new(WorkflowDebugRules.Version(items), WorkflowDebugRules.Checkpoint(instance).PauseGeneration, items);
            }
        }
        return new(cancellations, states);
    }

    private async Task WriteDebugAcknowledgementsAsync(DbConnection connection, DbTransaction transaction, string id,
        IReadOnlyList<DebugCommand> before, IReadOnlyList<DebugCommand> after)
    {
        foreach (var command in after.Where(c => c.AcknowledgedRevision != null && before.Any(old => old.Id == c.Id && old.AcknowledgedRevision == null)))
            await connection.ExecuteAsync($"UPDATE {DebugTable} SET {DebugColumn("CommandJson")} = @Json WHERE {DebugColumn("InstanceId")} = @InstanceId AND {DebugColumn("CommandId")} = @Id",
                new { InstanceId = id, command.Id, Json = JsonSerializer.Serialize(command) }, transaction);
    }
}
