using System.Data.Common;
using System.Text.Json;
using Dapper;
using IxIFlow.Core;

namespace IxIFlow.Relational;

public abstract partial class RelationalWorkflowStateRepository
{
    internal enum PayloadDialect { SqlServer, PostgreSql, Sqlite }
    internal abstract PayloadDialect PayloadSqlDialect { get; }
    private IPayloadStore? _payloadStore;
    private PayloadStoreSettings _payloadSettings = new();
    internal IPayloadStore Payloads => LazyInitializer.EnsureInitialized(ref _payloadStore,
        () => new RelationalPayloadStore(this, _payloadSettings));

    // The catalog uses a coarse transaction lock initially. Every digest still has its own durable state and
    // generation; serializing the small metadata operations also gives dependency closures one lock order.
    // Referencing transactions MUST take this lock before their instance/inbox locks in step 2.
    private sealed class RelationalPayloadStore(RelationalWorkflowStateRepository repository, PayloadStoreSettings settings) : CatalogPayloadStore(settings)
    {
        private bool SqlServer => repository.PayloadSqlDialect == PayloadDialect.SqlServer;
        private string Table => SqlServer ? "dbo.IxIFlowWorkflowPayloads" : "ixiflow_workflow_payloads";
        private string Refs => SqlServer ? "dbo.IxIFlowWorkflowPayloadRefs" : "ixiflow_workflow_payload_refs";
        private string C(string pascal, string snake) => SqlServer ? pascal : snake;

        internal override async ValueTask<T> Execute<T>(Func<PayloadCatalog, CancellationToken, ValueTask<T>> action, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            await repository.EnsureSchemaAsync();
            return await repository.ExecuteOperationAsync(Operation.Commit, async () =>
            {
                ct.ThrowIfCancellationRequested();
                await using var connection = await repository.OpenConnectionAsync();
                await using var transaction = (await repository.BeginTransactionAsync(connection, Operation.Commit))!;
                var catalog = CreateCatalog(transaction);
                try
                {
                    var result = await action(catalog, ct);
                    await transaction.CommitAsync(ct);
                    return result;
                }
                finally { catalog.End(); }
            });
        }

        public override IPayloadTransaction Enlist(DbTransaction transaction)
        {
            ArgumentNullException.ThrowIfNull(transaction);
            if (transaction.Connection is null) throw new ArgumentException("Transaction has ended", nameof(transaction));
            return CreateCatalog(transaction);
        }

        private PayloadCatalog CreateCatalog(DbTransaction transaction)
        {
            var connection = transaction.Connection ?? throw new InvalidOperationException("Transaction ended");
            Task? locked = null;
            DateTime now = default;
            async Task Lock(CancellationToken ct)
            {
                if (SqlServer)
                    await connection.ExecuteAsync(new CommandDefinition("""
                        DECLARE @result int;
                        EXEC @result = sys.sp_getapplock @Resource = 'IxIFlow.PayloadCatalog',
                            @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 30000;
                        IF @result < 0 THROW 51000, 'Payload catalog lock unavailable', 1;
                        """, transaction: transaction, cancellationToken: ct));
                else if (repository.PayloadSqlDialect == PayloadDialect.PostgreSql)
                    await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(73104951223001)",
                        transaction: transaction, cancellationToken: ct));
                // SQLite's BEGIN IMMEDIATE owns the writer lock before clock sampling.
                now = await repository.OutboxNowAsync(connection, transaction);
            }
            async ValueTask Ensure(CancellationToken ct) => await (locked ??= Lock(ct));
            async ValueTask<PayloadRecord?> Read(string digest, CancellationToken ct)
            {
                await Ensure(ct);
                var stored = await connection.QuerySingleOrDefaultAsync<PayloadRow>(new CommandDefinition(
                    $"SELECT {C("StateJson", "state_json")} AS StateJson, {C("Body", "body")} AS Body FROM {Table} WHERE {C("Digest", "digest")}=@Digest",
                    new { Digest = digest }, transaction, cancellationToken: ct));
                if (stored is null) return null;
                var row = JsonSerializer.Deserialize<PayloadRecord>(stored.StateJson)!;
                row.Body = stored.Body;
                row.Owners = (await connection.QueryAsync<PayloadOwner>(new CommandDefinition(
                    $"SELECT {C("OwnerKind", "owner_kind")} AS Kind, {C("OwnerId", "owner_id")} AS Id FROM {Refs} WHERE {C("Digest", "digest")}=@Digest",
                    new { Digest = digest }, transaction, cancellationToken: ct))).ToHashSet();
                return row;
            }
            async ValueTask Write(PayloadRecord row, CancellationToken ct)
            {
                await Ensure(ct);
                var values = new { row.Reference.Digest, row.Reference.Bytes, row.Generation, row.Body,
                    StateJson = JsonSerializer.Serialize(row), Created = repository.At(row.TouchedAtUtc) };
                var changed = await connection.ExecuteAsync(new CommandDefinition(
                    $"UPDATE {Table} SET {C("Bytes", "bytes")}=@Bytes, {C("Generation", "generation")}=@Generation, {C("Body", "body")}=@Body, {C("StateJson", "state_json")}=@StateJson WHERE {C("Digest", "digest")}=@Digest",
                    values, transaction, cancellationToken: ct));
                if (changed == 0)
                    await connection.ExecuteAsync(new CommandDefinition(
                        $"INSERT INTO {Table} ({C("Digest", "digest")}, {C("Bytes", "bytes")}, {C("Generation", "generation")}, {C("Body", "body")}, {C("StateJson", "state_json")}, {C("CreatedAtUtc", "created_at_utc")}) VALUES (@Digest,@Bytes,@Generation,@Body,@StateJson,@Created)",
                        values, transaction, cancellationToken: ct));
                await connection.ExecuteAsync(new CommandDefinition($"DELETE FROM {Refs} WHERE {C("Digest", "digest")}=@Digest",
                    values, transaction, cancellationToken: ct));
                foreach (var owner in row.Owners)
                    await connection.ExecuteAsync(new CommandDefinition(
                        $"INSERT INTO {Refs} ({C("Digest", "digest")}, {C("OwnerKind", "owner_kind")}, {C("OwnerId", "owner_id")}) VALUES (@Digest,@Kind,@Id)",
                        new { row.Reference.Digest, owner.Kind, owner.Id }, transaction, cancellationToken: ct));
            }
            async ValueTask<IReadOnlyList<PayloadRecord>> All(CancellationToken ct)
            {
                await Ensure(ct);
                var digests = (await connection.QueryAsync<string>(new CommandDefinition(
                    $"SELECT {C("Digest", "digest")} FROM {Table}", transaction: transaction, cancellationToken: ct))).ToArray();
                var rows = new List<PayloadRecord>();
                foreach (var digest in digests) rows.Add((await Read(digest, ct))!);
                return rows;
            }
            return new PayloadCatalog(Read, Write, All, () => now, Settings, transaction);
        }

        private sealed class PayloadRow
        {
            public string StateJson { get; set; } = "";
            public byte[]? Body { get; set; }
        }
    }
}
