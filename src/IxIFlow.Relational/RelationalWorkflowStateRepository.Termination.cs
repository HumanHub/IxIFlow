using System.Data.Common;
using System.Text.Json;
using Dapper;
using IxIFlow.Core;

namespace IxIFlow.Relational;

public abstract partial class RelationalWorkflowStateRepository
{
    public async Task<WorkflowControlReceipt> RequestTerminationAsync(string instanceId, CancellationReason reason, Guid? expectedIncarnation = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(reason);
        await EnsureSchemaAsync();
        return await ExecuteOperationAsync(Operation.Termination, async () =>
        {
            await using var connection = await OpenConnectionAsync(ownership: true);
            await using var transaction = await BeginTransactionAsync(connection, Operation.Termination)
                ?? throw new InvalidOperationException("Termination requires a store transaction");
            var receipt = await RequestTerminationAsync(connection, transaction, instanceId, reason, expectedIncarnation: expectedIncarnation);
            if (receipt.Accepted) await NotifyRunnableAsync(connection, transaction);
            await transaction.CommitAsync();
            return receipt;
        });
    }

    private async Task<WorkflowControlReceipt> RequestTerminationAsync(DbConnection connection,
        DbTransaction transaction, string instanceId, CancellationReason reason,
        ICollection<string>? madeRunnable = null, HashSet<string>? visited = null, Guid? expectedIncarnation = null)
    {
        visited ??= new HashSet<string>(StringComparer.Ordinal);
        if (!visited.Add(instanceId)) return new(false, 0, Guid.Empty);
        var (row, now) = await LockAsync<TerminationRow>(connection, transaction, Statement.TerminationState, instanceId);
        if (row == null) return new(false, 0, Guid.Empty);
        if (expectedIncarnation is { } identity && row.Incarnation != identity)
            return new(false, 0, row.Incarnation);
        if (WorkflowStoreRules.IsTerminal(row.Status))
        {
            // Only child propagation follows finished intermediates. A direct operator request on
            // an already finished root still loses to completion and is refused.
            if (madeRunnable != null)
            {
                var children = await connection.QueryAsync<string>(RequiredSql(Statement.TerminationChildren), new { InstanceId = instanceId }, transaction);
                foreach (var child in children)
                    await RequestTerminationAsync(connection, transaction, child, reason, madeRunnable, visited);
            }
            return new(false, 0, row.Incarnation);
        }
        var pending = ReadReason(row.CancellationJson);
        if (pending?.Mode == WorkflowControlMode.Terminate)
        {
            madeRunnable?.Add(instanceId);
            return new(true, pending.CommandVersion, row.Incarnation);
        }
        var command = JsonSerializer.Deserialize<CancellationReason>(JsonSerializer.Serialize(reason))!;
        command.Mode = WorkflowControlMode.Terminate;
        command.CommandVersion = checked((pending?.CommandVersion ?? 0) + 1);
        var values = WithTimes(new { InstanceId = instanceId, ReasonJson = JsonSerializer.Serialize(command) }, ("Now", now));
        await connection.ExecuteAsync(RequiredSql(Statement.TerminationRequest), values, transaction);
        madeRunnable?.Add(instanceId);
        return new(true, command.CommandVersion, row.Incarnation);
    }

    private sealed class TerminationRow
    {
        public Guid Incarnation { get; set; }
        public WorkflowStatus Status { get; set; }
        public string? CancellationJson { get; set; }
    }
}
