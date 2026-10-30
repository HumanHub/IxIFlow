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
    internal enum PayloadDialect
    {
        SqlServer,
        PostgreSql,
        Sqlite
    }

    internal abstract PayloadDialect PayloadSqlDialect { get; }
    private IPayloadStore? _payloadStore;
    private PayloadStoreSettings _payloadSettings = new();

    public IPayloadStore PayloadStore => LazyInitializer.EnsureInitialized(ref _payloadStore,
        () => new RelationalPayloadStore(this, _payloadSettings));

    // Acquire digest locks before instance/inbox locks. A transaction never waits for another digest while
    // holding one: it aborts and retries, which also avoids dependency-closure lock-order deadlocks.
    private sealed class RelationalPayloadStore(RelationalWorkflowStateRepository repository, PayloadStoreSettings settings)
        : CatalogPayloadStore(settings)
    {
        private bool SqlServer => repository.PayloadSqlDialect == PayloadDialect.SqlServer;
        private string Table => SqlServer ? "dbo.IxIFlowWorkflowPayloads" : "ixiflow_workflow_payloads";
        private string Refs => SqlServer ? "dbo.IxIFlowWorkflowPayloadRefs" : "ixiflow_workflow_payload_refs";
        private string C(string pascal, string snake) => SqlServer ? pascal : snake;

        internal override async ValueTask<T> Execute<T>(Func<PayloadCatalog, CancellationToken, ValueTask<T>> action,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await repository.EnsureSchemaAsync(cancellationToken: cancellationToken);

            for (var attempt = 0;; attempt++)
            {
                try
                {
                    return await repository.ExecuteOperationAsync(Operation.Commit, async () =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await using var connection = await repository.OpenConnectionAsync(cancellationToken: cancellationToken);

                        await using var transaction =
                            (await repository.BeginTransactionAsync(connection, Operation.Commit, cancellationToken: cancellationToken))!;

                        var catalog = CreateCatalog(transaction);

                        try
                        {
                            var result = await action(catalog, cancellationToken);
                            await transaction.CommitAsync(cancellationToken);

                            return result;
                        }
                        finally
                        {
                            catalog.End();
                        }
                    }, cancellationToken: cancellationToken);
                }
                catch (PayloadLockUnavailableException) when (attempt < 2)
                {
                    await Task.Delay(25 * (1 << attempt), cancellationToken);
                }
            }
        }

        public override IPayloadTransaction Enlist(DbTransaction transaction)
        {
            ArgumentNullException.ThrowIfNull(transaction);

            if (transaction.Connection is null)
                throw new ArgumentException("Transaction has ended", nameof(transaction));

            return CreateCatalog(transaction);
        }

        private PayloadCatalog CreateCatalog(DbTransaction transaction)
        {
            var connection = transaction.Connection ?? throw new InvalidOperationException("Transaction ended");
            var locked = new HashSet<string>(StringComparer.Ordinal);
            var rows = new Dictionary<string, PayloadRecord?>(StringComparer.Ordinal);
            DateTime now = default;

            async Task Lock(string digest, CancellationToken cancellationToken)
            {
                if (!locked.Add(digest))
                    return;

                // Wait for the first digest only. Secondary contention rolls the transaction back,
                // so standalone dependency operations cannot form a wait cycle with a checkpoint.
                var waitMs = locked.Count == 1 ? 2000 : 0;

                if (SqlServer)
                {
                    var result = await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
                            DECLARE @result int;
                            EXEC @result = sys.sp_getapplock @Resource = @Resource,
                                @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = @WaitMs;
                            SELECT @result;
                            """, new { Resource = "IxIFlow.Payload/" + digest, WaitMs = waitMs }, transaction,
                        cancellationToken: cancellationToken));

                    if (result < 0)
                        throw new PayloadLockUnavailableException();
                }
                else if (repository.PayloadSqlDialect == PayloadDialect.PostgreSql)
                {
                    var key = BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(digest)));

                    if (waitMs > 0)
                    {
                        try
                        {
                            await connection.ExecuteAsync(new CommandDefinition(
                                "SET LOCAL lock_timeout = '2s'; SELECT pg_advisory_xact_lock(@Key); SET LOCAL lock_timeout = '0'",
                                new { Key = key }, transaction, cancellationToken: cancellationToken));
                        }
                        catch (DbException error) when (!cancellationToken.IsCancellationRequested && error.SqlState == "55P03")
                        {
                            throw new PayloadLockUnavailableException();
                        }
                    }
                    else if (!await connection.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT pg_try_advisory_xact_lock(@Key)",
                                 new { Key = key }, transaction, cancellationToken: cancellationToken)))
                        throw new PayloadLockUnavailableException();
                }

                // SQLite's BEGIN IMMEDIATE owns the writer lock before clock sampling.
                now = await repository.OutboxNowAsync(connection, transaction, cancellationToken);
            }

            async ValueTask<PayloadRecord?> Read(string digest, CancellationToken cancellationToken)
            {
                await Lock(digest, cancellationToken);

                if (rows.TryGetValue(digest, out var cached))
                    return cached;

                var row = await Lookup(digest, cancellationToken);
                rows[digest] = row;

                return row;
            }

            async ValueTask<PayloadRecord?> Lookup(string digest, CancellationToken cancellationToken)
            {
                if (rows.TryGetValue(digest, out var cached))
                    return cached;

                var stored = await connection.QuerySingleOrDefaultAsync<PayloadRow>(new CommandDefinition(
                    $"SELECT {C("MetadataJson", "metadata_json")} AS MetadataJson FROM {Table} WHERE {C("Digest", "digest")}=@Digest",
                    new { Digest = digest }, transaction, cancellationToken: cancellationToken));

                if (stored is null)
                    return null;

                var row = JsonSerializer.Deserialize<PayloadRecord>(stored.MetadataJson)!;

                row.Owners = (await connection.QueryAsync<PayloadOwner>(new CommandDefinition(
                    $"SELECT {C("OwnerKind", "owner_kind")} AS Kind, {C("OwnerId", "owner_id")} AS Id FROM {Refs} WHERE {C("Digest", "digest")}=@Digest",
                    new { Digest = digest }, transaction, cancellationToken: cancellationToken))).ToHashSet();

                return row;
            }

            async ValueTask Write(PayloadRecord row, CancellationToken cancellationToken)
            {
                await Lock(row.Reference.Digest, cancellationToken);
                rows[row.Reference.Digest] = row;

                var values = new
                {
                    row.Reference.Digest, row.Reference.Bytes, row.Body,
                    MetadataJson = JsonSerializer.Serialize(row)
                };

                var changed = await connection.ExecuteAsync(new CommandDefinition(
                    $"UPDATE {Table} SET {C("Bytes", "bytes")}=@Bytes, {(row.BodyChanged ? C("Body", "body") + "=@Body," : "")} {C("MetadataJson", "metadata_json")}=@MetadataJson WHERE {C("Digest", "digest")}=@Digest",
                    values, transaction, cancellationToken: cancellationToken));

                if (changed == 0)
                    await connection.ExecuteAsync(new CommandDefinition(
                        $"INSERT INTO {Table} ({C("Digest", "digest")}, {C("Bytes", "bytes")}, {C("Body", "body")}, {C("MetadataJson", "metadata_json")}) VALUES (@Digest,@Bytes,@Body,@MetadataJson)",
                        values, transaction, cancellationToken: cancellationToken));

                await connection.ExecuteAsync(new CommandDefinition($"DELETE FROM {Refs} WHERE {C("Digest", "digest")}=@Digest",
                    values, transaction, cancellationToken: cancellationToken));

                foreach (var owner in row.Owners)
                    await connection.ExecuteAsync(new CommandDefinition(
                        $"INSERT INTO {Refs} ({C("Digest", "digest")}, {C("OwnerKind", "owner_kind")}, {C("OwnerId", "owner_id")}) VALUES (@Digest,@Kind,@Id)",
                        new { row.Reference.Digest, owner.Kind, owner.Id }, transaction, cancellationToken: cancellationToken));
            }

            async ValueTask Delete(string digest, CancellationToken cancellationToken)
            {
                await Lock(digest, cancellationToken);

                await connection.ExecuteAsync(new CommandDefinition(
                    $"DELETE FROM {Refs} WHERE {C("Digest", "digest")}=@Digest",
                    new { Digest = digest }, transaction, cancellationToken: cancellationToken));

                await connection.ExecuteAsync(new CommandDefinition(
                    $"DELETE FROM {Table} WHERE {C("Digest", "digest")}=@Digest",
                    new { Digest = digest }, transaction, cancellationToken: cancellationToken));

                rows[digest] = null;
            }

            async ValueTask<IReadOnlyList<PayloadRecord>> Owned(PayloadOwner owner, CancellationToken cancellationToken)
            {
                var digests = await connection.QueryAsync<string>(new CommandDefinition(
                    $"SELECT {C("Digest", "digest")} FROM {Refs} WHERE {C("OwnerKind", "owner_kind")}=@Kind AND {C("OwnerId", "owner_id")}=@Id",
                    owner, transaction, cancellationToken: cancellationToken));

                var owned = new List<PayloadRecord>();

                foreach (var digest in digests.Order(StringComparer.Ordinal))
                    if (await Lookup(digest, cancellationToken) is { } row)
                        owned.Add(row);

                return owned;
            }

            async ValueTask<byte[]?> Body(PayloadRecord row, CancellationToken cancellationToken) =>
                await connection.QuerySingleOrDefaultAsync<byte[]>(new CommandDefinition(
                    $"SELECT {C("Body", "body")} FROM {Table} WHERE {C("Digest", "digest")}=@Digest",
                    new { row.Reference.Digest }, transaction, cancellationToken: cancellationToken));

            async ValueTask<IReadOnlyList<PayloadRecord>> Discover(int limit, TimeSpan grace, CancellationToken cancellationToken)
            {
                now = await repository.OutboxNowAsync(connection, transaction, cancellationToken);

                var touched = repository.PayloadSqlDialect switch
                {
                    PayloadDialect.SqlServer => "TRY_CONVERT(datetime2, JSON_VALUE(p.MetadataJson, '$.TouchedAtUtc')) <= @Cutoff",
                    PayloadDialect.PostgreSql => "(p.metadata_json::jsonb->>'TouchedAtUtc')::timestamptz <= @Cutoff",
                    _ => "julianday(json_extract(p.metadata_json, '$.TouchedAtUtc')) <= julianday(@Cutoff)"
                };

                var json = await connection.QueryAsync<string>(new CommandDefinition(
                    $"SELECT {(SqlServer ? "TOP (@Limit)" : "")} p.{C("MetadataJson", "metadata_json")} FROM {Table} p {(SqlServer ? "WITH (READPAST, READCOMMITTEDLOCK)" : "")} WHERE {touched} AND NOT EXISTS " +
                    $"(SELECT 1 FROM {Refs} r WHERE r.{C("Digest", "digest")}=p.{C("Digest", "digest")}) " +
                    $"ORDER BY p.{C("Digest", "digest")} {(SqlServer ? "" : "LIMIT @Limit")}",
                    repository.WithTimes(new { Limit = limit }, ("Cutoff", now - grace)), transaction,
                    cancellationToken: cancellationToken));

                return json.Select(text => JsonSerializer.Deserialize<PayloadRecord>(text)!).ToArray();
            }

            // Enlisted transactions can be owned by a caller outside ExecuteOperationAsync, and each
            // payload operation has its own token. Guard the database callbacks at that boundary too.
            ValueTask<T> Guard<T>(Func<ValueTask<T>> call, CancellationToken token) =>
                new(ExecuteStoreAsync(() => call().AsTask(), token));

            ValueTask GuardWrite(Func<ValueTask> call, CancellationToken token) =>
                new(ExecuteStoreAsync(() => call().AsTask(), token));

            return new PayloadCatalog(
                (digest, token) => Guard(() => Read(digest, token), token),
                (row, token) => GuardWrite(() => Write(row, token), token),
                (digest, token) => GuardWrite(() => Delete(digest, token), token),
                () => now, Settings, transaction,
                (owner, token) => Guard(() => Owned(owner, token), token),
                (row, token) => Guard(() => Body(row, token), token),
                (limit, grace, token) => Guard(() => Discover(limit, grace, token), token),
                (digest, token) => Guard(() => Lookup(digest, token), token));
        }

        private sealed class PayloadRow
        {
            public string MetadataJson { get; set; } = "";
        }
    }

    private sealed class PayloadLockUnavailableException : Exception;
}