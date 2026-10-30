using System.Data.Common;
using System.Text.Json;
using Dapper;
using IxIFlow.Core;

namespace IxIFlow.Relational;

public abstract partial class RelationalWorkflowStateRepository
{
    public async Task<WorkflowControlReceipt> RequestTerminationAsync(string instanceId, CancellationReason reason,
        Guid? expectedIncarnation = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InstanceIds.Validate(instanceId);
        ArgumentNullException.ThrowIfNull(reason);
        await EnsureSchemaAsync(cancellationToken: cancellationToken);

        return await ExecuteOperationAsync(Operation.Termination, async () =>
        {
            await using var connection = await OpenConnectionAsync(ownership: true, cancellationToken: cancellationToken);

            await using var transaction =
                await BeginTransactionAsync(connection, Operation.Termination, cancellationToken: cancellationToken)
                ?? throw new InvalidOperationException("Termination requires a store transaction");

            var receipt = await RequestTerminationAsync(connection, transaction, instanceId, reason,
                expectedIncarnation: expectedIncarnation, cancellationToken: cancellationToken);

            if (receipt.Accepted)
                await NotifyRunnableAsync(connection, transaction, cancellationToken: cancellationToken);

            await transaction.CommitAsync(cancellationToken: cancellationToken);

            return receipt;
        }, cancellationToken: cancellationToken);
    }

    private async Task<WorkflowControlReceipt> RequestTerminationAsync(DbConnection connection,
        DbTransaction transaction, string instanceId, CancellationReason reason,
        ICollection<string>? madeRunnable = null, HashSet<string>? visited = null, Guid? expectedIncarnation = null,
        CancellationToken cancellationToken = default)
    {
        visited ??= new HashSet<string>(StringComparer.Ordinal);

        if (!visited.Add(instanceId))
            return new(false, 0, Guid.Empty);

        var (row, now) = await LockAsync<TerminationRow>(connection, transaction, Statement.TerminationState, instanceId,
            cancellationToken: cancellationToken);

        if (row == null)
            return new(false, 0, Guid.Empty);

        if (expectedIncarnation is { } identity && row.Incarnation != identity)
            return new(false, 0, row.Incarnation);

        if (WorkflowStoreRules.IsTerminal(WorkflowStatuses.Read(row.Status)))
        {
            // Only child propagation follows finished intermediates. A direct operator request on
            // an already finished root still loses to completion and is refused.
            if (madeRunnable != null)
            {
                var children = await connection.QueryAsync<string>(new CommandDefinition(RequiredSql(Statement.TerminationChildren),
                    new { InstanceId = instanceId }, transaction, cancellationToken: cancellationToken));

                foreach (var child in children)
                    await RequestTerminationAsync(connection, transaction, child, reason, madeRunnable, visited,
                        cancellationToken: cancellationToken);
            }

            return new(false, 0, row.Incarnation);
        }

        var pending = ReadReason(row.CancellationJson);

        if (pending?.Mode == WorkflowControlMode.Terminate)
        {
            madeRunnable?.Add(instanceId);

            return new(true, pending.CommandVersion, row.Incarnation)
            {
                Notice = await NoticeAsync(connection, transaction, instanceId, now, cancellationToken: cancellationToken) is
                    { Claimable: true } replay
                    ? replay
                    : null
            };
        }

        var command = JsonSerializer.Deserialize<CancellationReason>(JsonSerializer.Serialize(reason))!;
        command.Mode = WorkflowControlMode.Terminate;
        command.CommandVersion = checked((pending?.CommandVersion ?? 0) + 1);
        var values = WithTimes(new { InstanceId = instanceId, ReasonJson = JsonSerializer.Serialize(command) }, ("Now", now));

        await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.TerminationRequest), values, transaction,
            cancellationToken: cancellationToken));

        madeRunnable?.Add(instanceId);

        return new(true, command.CommandVersion, row.Incarnation)
        {
            Notice = await NoticeAsync(connection, transaction, instanceId, now, cancellationToken: cancellationToken) is
                { Claimable: true } notice
                ? notice
                : null
        };
    }

    private sealed class TerminationRow
    {
        public Guid Incarnation { get; set; }

        public object? IncarnationValue
        {
            set => Incarnation = ReadGuid(value);
        }

        public string Status { get; set; } = "";
        public string? CancellationJson { get; set; }
    }
}