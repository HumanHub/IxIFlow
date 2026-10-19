using System.Data.Common;
using System.Text.Json;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
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

    // Acquire digest locks before instance/inbox locks. A transaction never waits for another digest while
    // holding one: it aborts and retries, which also avoids dependency-closure lock-order deadlocks.
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
            while (true)
            {
              try { return await repository.ExecuteOperationAsync(Operation.Commit, async () =>
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
              }); }
              catch (PayloadLockUnavailableException) { await Task.Delay(5, ct); }
            }
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
            var locked = new HashSet<string>(StringComparer.Ordinal);
            var rows = new Dictionary<string, PayloadRecord?>(StringComparer.Ordinal);
            DateTime now = default;
            async Task Lock(string digest, CancellationToken ct)
            {
                if (!locked.Add(digest)) return;
                if (SqlServer)
                {
                    var result = await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
                        DECLARE @result int;
                        EXEC @result = sys.sp_getapplock @Resource = @Resource,
                            @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 0;
                        SELECT @result;
                        """, new { Resource = "IxIFlow.Payload/" + digest }, transaction, cancellationToken: ct));
                    if (result < 0) throw new PayloadLockUnavailableException();
                }
                else if (repository.PayloadSqlDialect == PayloadDialect.PostgreSql)
                {
                    var key = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(digest)));
                    if (!await connection.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT pg_try_advisory_xact_lock(@Key)",
                        new { Key = key }, transaction, cancellationToken: ct))) throw new PayloadLockUnavailableException();
                }
                // SQLite's BEGIN IMMEDIATE owns the writer lock before clock sampling.
                now = await repository.OutboxNowAsync(connection, transaction);
            }
            async ValueTask<PayloadRecord?> Read(string digest, CancellationToken ct)
            {
                await Lock(digest, ct);
                if (rows.TryGetValue(digest, out var cached)) return cached;
                var stored = await connection.QuerySingleOrDefaultAsync<PayloadRow>(new CommandDefinition(
                    $"SELECT {C("StateJson", "state_json")} AS StateJson FROM {Table} WHERE {C("Digest", "digest")}=@Digest",
                    new { Digest = digest }, transaction, cancellationToken: ct));
                if (stored is null) { rows[digest] = null; return null; }
                var row = JsonSerializer.Deserialize<PayloadRecord>(stored.StateJson)!;
                row.Owners = (await connection.QueryAsync<PayloadOwner>(new CommandDefinition(
                    $"SELECT {C("OwnerKind", "owner_kind")} AS Kind, {C("OwnerId", "owner_id")} AS Id FROM {Refs} WHERE {C("Digest", "digest")}=@Digest",
                    new { Digest = digest }, transaction, cancellationToken: ct))).ToHashSet();
                rows[digest] = row;
                return row;
            }
            async ValueTask Write(PayloadRecord row, CancellationToken ct)
            {
                await Lock(row.Reference.Digest, ct);
                rows[row.Reference.Digest] = row;
                var values = new { row.Reference.Digest, row.Reference.Bytes, row.Generation, row.Body,
                    StateJson = JsonSerializer.Serialize(row), Created = repository.At(row.TouchedAtUtc) };
                var changed = await connection.ExecuteAsync(new CommandDefinition(
                    $"UPDATE {Table} SET {C("Bytes", "bytes")}=@Bytes, {C("Generation", "generation")}=@Generation, {(row.BodyChanged ? C("Body", "body") + "=@Body," : "")} {C("StateJson", "state_json")}=@StateJson WHERE {C("Digest", "digest")}=@Digest",
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
                var digests = (await connection.QueryAsync<string>(new CommandDefinition(
                    $"SELECT {C("Digest", "digest")} FROM {Table}", transaction: transaction, cancellationToken: ct))).ToArray();
                var rows = new List<PayloadRecord>();
                foreach (var digest in digests.Order(StringComparer.Ordinal)) rows.Add((await Read(digest, ct))!);
                return rows;
            }
            async ValueTask<IReadOnlyList<PayloadRecord>> Owned(PayloadOwner owner, CancellationToken ct)
            {
                var digests = await connection.QueryAsync<string>(new CommandDefinition(
                    $"SELECT {C("Digest", "digest")} FROM {Refs} WHERE {C("OwnerKind", "owner_kind")}=@Kind AND {C("OwnerId", "owner_id")}=@Id",
                    owner, transaction, cancellationToken: ct));
                var owned = new List<PayloadRecord>();
                foreach (var digest in digests.Order(StringComparer.Ordinal))
                    if (await Read(digest, ct) is { } row) owned.Add(row);
                return owned;
            }
            async ValueTask<byte[]?> Body(PayloadRecord row, CancellationToken ct) =>
                await connection.QuerySingleOrDefaultAsync<byte[]>(new CommandDefinition(
                    $"SELECT {C("Body", "body")} FROM {Table} WHERE {C("Digest", "digest")}=@Digest",
                    new { row.Reference.Digest }, transaction, cancellationToken: ct));
            async ValueTask<IReadOnlyList<PayloadRecord>> Discover(CancellationToken ct)
            {
                now = await repository.OutboxNowAsync(connection, transaction);
                var json = await connection.QueryAsync<string>(new CommandDefinition(
                    $"SELECT p.{C("StateJson", "state_json")} FROM {Table} p {(SqlServer ? "WITH (READPAST, READCOMMITTEDLOCK)" : "")} WHERE NOT EXISTS " +
                    $"(SELECT 1 FROM {Refs} r WHERE r.{C("Digest", "digest")}=p.{C("Digest", "digest")})",
                    transaction: transaction, cancellationToken: ct));
                return json.Select(text => JsonSerializer.Deserialize<PayloadRecord>(text)!).ToArray();
            }
            return new PayloadCatalog(Read, Write, All, () => now, Settings, transaction, Owned, Body, Discover);
        }

        private sealed class PayloadRow
        {
            public string StateJson { get; set; } = "";
            public byte[]? Body { get; set; }
        }
    }
    private sealed class PayloadLockUnavailableException : Exception;
}
