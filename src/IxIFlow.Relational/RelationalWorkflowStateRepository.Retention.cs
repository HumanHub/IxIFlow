using System.Data.Common;
using Dapper;
using IxIFlow.Core;

namespace IxIFlow.Relational;

public abstract partial class RelationalWorkflowStateRepository
{
    private sealed class TombstoneRow
    {
        public string InstanceId { get; set; } = "";
        public DateTime RemovedAtUtc { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
    }

    // Advisory identity read precedes digest locks; recheck it after taking the instance lock.
    private async Task<Guid?> DeletionIncarnationAsync(DbConnection connection, DbTransaction transaction, string id,
        CancellationToken cancellationToken = default) =>
        ReadOptionalGuid(await connection.ExecuteScalarAsync<object>(new CommandDefinition(
            $"SELECT {SearchColumn("incarnation")} FROM {InstanceTable} WHERE {SearchColumn("instance_id")}=@InstanceId",
            new { InstanceId = id }, transaction, cancellationToken: cancellationToken)));

    private static PayloadOwner CheckpointOwner(string id, Guid incarnation) => new("checkpoint", $"{id}/{incarnation:N}");

    private async Task<PayloadReference[]> DeletionReferencesAsync(DbConnection connection, DbTransaction transaction,
        string id, Guid incarnation, CancellationToken cancellationToken = default)
    {
        var sqlServer = PayloadSqlDialect == PayloadDialect.SqlServer;
        var table = sqlServer ? "dbo.IxIFlowWorkflowPayloads" : "ixiflow_workflow_payloads";
        var refs = sqlServer ? "dbo.IxIFlowWorkflowPayloadRefs" : "ixiflow_workflow_payload_refs";
        var digest = sqlServer ? "Digest" : "digest";
        var bytes = sqlServer ? "Bytes" : "bytes";
        var kind = sqlServer ? "OwnerKind" : "owner_kind";
        var ownerId = sqlServer ? "OwnerId" : "owner_id";
        var filter = PayloadSqlDialect == PayloadDialect.PostgreSql ? "= ANY(@Kinds)" : "IN @Kinds";

        var rows = await connection.QueryAsync<(string Digest, long Bytes)>(new CommandDefinition(
            $"SELECT DISTINCT p.{digest} AS Digest, p.{bytes} AS Bytes FROM {table} p JOIN {refs} r ON r.{digest}=p.{digest} " +
            $"WHERE r.{ownerId}=@OwnerId AND r.{kind} {filter} ORDER BY p.{digest}",
            new { OwnerId = $"{id}/{incarnation:N}", Kinds = new[] { "checkpoint", "inbox" } }, transaction,
            cancellationToken: cancellationToken));

        return rows.Select(row => new PayloadReference(row.Digest, row.Bytes)).ToArray();
    }

    private async Task<WorkflowTombstone?> TombstoneAsync(DbConnection connection, DbTransaction? transaction,
        string instanceId, DateTime now, CancellationToken cancellationToken = default)
    {
        var row = await connection.QuerySingleOrDefaultAsync<TombstoneRow>(new CommandDefinition(RequiredSql(Statement.Tombstone),
            WithTimes(new { InstanceId = instanceId }, ("Now", now)), transaction, cancellationToken: cancellationToken));

        return row == null ? null : new WorkflowTombstone(row.InstanceId, ReadTime(row.RemovedAtUtc), ReadTime(row.ExpiresAtUtc));
    }

    private async Task RefuseRemovedAsync(DbConnection connection, DbTransaction? transaction, string id, DateTime now,
        CancellationToken cancellationToken = default)
    {
        if (await TombstoneAsync(connection, transaction, id, now, cancellationToken: cancellationToken) != null)
            throw new WorkflowEngineException(WorkflowErrorKind.InstanceRemoved,
                $"Workflow instance '{id}' was removed by retention; its ID is reserved");
    }

    public async Task<WorkflowTombstone?> GetTombstoneAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InstanceIds.Validate(instanceId);

        return await ExecuteStoreAsync<WorkflowTombstone?>(async () =>
        {
            await EnsureSchemaAsync(cancellationToken: cancellationToken);
            await using var connection = await OpenConnectionAsync(cancellationToken: cancellationToken);

            return await TombstoneAsync(connection, null, instanceId,
                await OutboxNowAsync(connection, null, cancellationToken: cancellationToken), cancellationToken: cancellationToken);
        }, cancellationToken);
    }

    public async Task<RetentionResult> PurgeAsync(TimeSpan finishedRetention, TimeSpan tombstoneRetention,
        int batchSize, CancellationToken cancellationToken = default)
    {
        RetentionSettings.Validate(finishedRetention, tombstoneRetention, batchSize);
        cancellationToken.ThrowIfCancellationRequested();
        await EnsureSchemaAsync(cancellationToken: cancellationToken);

        return await ExecuteOperationAsync(Operation.Purge, async () =>
        {
            await using var connection = await OpenConnectionAsync(cancellationToken: cancellationToken);

            // Maintenance covers subscriptions with no active host. Commit the bounded sweep before
            // selecting retention candidates, so newly given-up deliveries no longer pin instances.
            await using (var maintenance =
                         (await BeginTransactionAsync(connection, Operation.Purge, cancellationToken: cancellationToken))!)
            {
                await ExecuteOptionalAsync(connection, maintenance, Statement.RetentionLock,
                    cancellationToken: cancellationToken);

                var now = await OutboxNowAsync(connection, maintenance, cancellationToken: cancellationToken);

                await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.ExpireUnattemptedDeliveries),
                    WithTimes(new { Limit = batchSize, Error = OutboxRetry.NeverClaimedError }, ("Cutoff", now - _retryHorizon)),
                    maintenance, cancellationToken: cancellationToken));

                await maintenance.CommitAsync(cancellationToken);
            }

            // Selection is bounded and advisory. Every candidate is rechecked under its normal write
            // lock below. Each deletion has its own transaction to preserve row -> publisher lock order.
            string[] ids;
            Dictionary<string, int> blocked;

            await using (var selection =
                         await BeginTransactionAsync(connection, Operation.RetentionRead, cancellationToken: cancellationToken))
            {
                var selectedAt = await OutboxNowAsync(connection, selection, cancellationToken: cancellationToken);

                ids = (await connection.QueryAsync<string>(new CommandDefinition(
                    RequiredSql(Statement.RetentionCandidates),
                    WithTimes(new { Limit = batchSize }, ("Cutoff", selectedAt - finishedRetention)),
                    selection, cancellationToken: cancellationToken))).ToArray();

                blocked = (await connection.QueryAsync<(string PublisherName, int Instances)>(new CommandDefinition(
                    RequiredSql(Statement.RetentionBlockedPublishers), WithTimes(new { }, ("Cutoff", selectedAt - finishedRetention)),
                    selection, cancellationToken: cancellationToken))).ToDictionary(row => row.PublisherName, row => row.Instances,
                    StringComparer.Ordinal);
            }

            var removed = 0;

            foreach (var id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await using var transaction =
                    (await BeginTransactionAsync(connection, Operation.Purge, cancellationToken: cancellationToken))!;

                var incarnation = await DeletionIncarnationAsync(connection, transaction, id, cancellationToken: cancellationToken);

                if (incarnation == null)
                    continue;

                var pins = PayloadStore.Enlist(transaction);
                var owner = CheckpointOwner(id, incarnation.Value);
                var references = await DeletionReferencesAsync(connection, transaction, id, incarnation.Value, cancellationToken);
                await pins.LockAsync(owner, references, cancellationToken);
                var inboxOwner = InboxPayloads.Owner(id, incarnation.Value);

                async Task Execute(Statement statement, object? parameters = null) =>
                    await connection.ExecuteAsync(new CommandDefinition(RequiredSql(statement), parameters,
                        transaction, cancellationToken: cancellationToken));

                // The provider's transaction lock serializes retention writers across hosts. It is
                // acquired before row locks and released after each bounded deletion, also on shutdown.
                await ExecuteOptionalAsync(connection, transaction, Statement.RetentionLock,
                    cancellationToken: cancellationToken);

                await ExecuteOptionalAsync(connection, transaction, Statement.LockIdentity,
                    new { InstanceId = id }, cancellationToken);

                var (row, now) = await LockAsync<CommitState>(connection, transaction, Statement.CommitState, id,
                    cancellationToken: cancellationToken);

                if (row == null || row.Incarnation != incarnation)
                    continue;

                // Take the lease lock after the instance and before any outbox locks, as commits do.
                await connection.QuerySingleOrDefaultAsync<LeaseRecord>(new CommandDefinition(
                    RequiredSql(Statement.Lease), LeaseParameters(id, now), transaction,
                    cancellationToken: cancellationToken));

                // Serialize registration/replay with deletion so eligibility cannot gain a delivery.
                // SQLite already holds its writer transaction and needs no separate publisher lock.
                await LockPublishersAsync(connection, transaction, cancellationToken: cancellationToken);
                now = await OutboxNowAsync(connection, transaction, cancellationToken: cancellationToken);

                var eligible = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                    RequiredSql(Statement.RetentionEligible),
                    WithTimes(new { InstanceId = id }, ("Cutoff", now - finishedRetention)), transaction,
                    cancellationToken: cancellationToken));

                if (eligible != 1)
                    continue;

                var currentReferences = await DeletionReferencesAsync(connection, transaction, id, incarnation.Value, cancellationToken);

                if (!references.SequenceEqual(currentReferences))
                    continue;

                await pins.ReleaseAsync(owner, cancellationToken);
                await pins.ReleaseAsync(inboxOwner, cancellationToken);

                await Execute(Statement.WriteTombstone, WithTimes(new { InstanceId = id },
                    ("Now", now), ("Expires", now + tombstoneRetention)));

                await Execute(Statement.DeleteLease, new { InstanceId = id });
                await Execute(Statement.DeleteRows, new { InstanceId = id });
                await transaction.CommitAsync(cancellationToken);
                removed++;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await using var expiry = (await BeginTransactionAsync(connection, Operation.Purge, cancellationToken: cancellationToken))!;

            await ExecuteOptionalAsync(connection, expiry, Statement.RetentionLock,
                cancellationToken: cancellationToken);

            // Creators hold this lifecycle lock shared; collection holds it exclusively.
            // No creator can observe a set and then lose it before inserting its reference.
            await connection.ExecuteAsync(new CommandDefinition(CollectDemandSetsLockSql, transaction: expiry,
                cancellationToken: cancellationToken));

            await connection.ExecuteAsync(new CommandDefinition(CollectDemandSetsSql, new { Limit = batchSize }, expiry,
                cancellationToken: cancellationToken));

            var expiryNow = await OutboxNowAsync(connection, expiry, cancellationToken: cancellationToken);

            var expired = await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.ExpireTombstones),
                WithTimes(new { Limit = batchSize }, ("Now", expiryNow)), expiry,
                cancellationToken: cancellationToken));

            await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.AdmissionExpire),
                WithTimes(new { Limit = batchSize }, ("Now", expiryNow)), expiry, cancellationToken: cancellationToken));

            await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.OwnershipExpire),
                WithTimes(new { Limit = batchSize }, ("Now", expiryNow)), expiry, cancellationToken: cancellationToken));

            await connection.ExecuteAsync(new CommandDefinition(RequiredSql(Statement.WebhookDeliveryExpire),
                WithTimes(new { Limit = batchSize }, ("Now", expiryNow)), expiry, cancellationToken: cancellationToken));

            await expiry.CommitAsync(cancellationToken);

            return new RetentionResult(removed, expired) { SkippedUnattemptedByPublisher = blocked };
        }, cancellationToken: cancellationToken);
    }
}