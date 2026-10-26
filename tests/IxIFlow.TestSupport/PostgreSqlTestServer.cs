using System.Collections.Concurrent;
using IxIFlow.Core;
using Npgsql;
using Testcontainers.PostgreSql;

namespace IxIFlow.Tests.Infrastructure;

/// <summary>
/// One PostgreSQL server for the whole test run. Uses IXIFLOW_TEST_POSTGRESQL_CONNECTION_STRING when it is set;
/// otherwise starts a container through Testcontainers on first use. Tests lease isolated databases on it, through
/// <see cref="PostgreSqlTestDatabase{TTests}"/> or <see cref="CreateDatabaseAsync"/>.
/// </summary>
public static class PostgreSqlTestServer
{
    private const string ExternalServerVariable = "IXIFLOW_TEST_POSTGRESQL_CONNECTION_STRING";
    private const int AdminTimeoutSeconds = 120;
    private static readonly Lazy<Task<string>> Server = new(StartAsync);
    private static readonly SemaphoreSlim CreateGate = new(1, 1);
    private static readonly ConcurrentStack<(string ConnectionString, bool HasSchema)> Available = new();
    private static readonly Lazy<Task<DatabaseSchemaFingerprint>> Reference = new(CaptureReferenceAsync);
    private static string _emptySchema = "";
    private sealed record Database(bool Reusable, string PublicSchemaSql);
    private static readonly ConcurrentDictionary<string, Database> Owned = new();
    // Lease connection string -> pooled database connection string.
    private static readonly ConcurrentDictionary<string, string> Leased = new();
    private static long _leases;

    static PostgreSqlTestServer()
    {
        // Run before container disposal; external servers need the same cleanup.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var connectionString in Owned.Keys)
            {
                try { DestroyDatabaseAsync(connectionString).GetAwaiter().GetResult(); }
                catch (Exception error) when (error is NpgsqlException or InvalidOperationException)
                {
                    Console.Error.WriteLine($"PostgreSQL test database cleanup failed: {error.Message}");
                }
            }
        };
    }

    /// <summary>Leases a pristine store. Set emptySchema for tests that must exercise schema creation.</summary>
    public static async Task<string> CreateDatabaseAsync(string prefix, bool emptySchema = false)
    {
        var reference = await Reference.Value;
        if (!Available.TryPop(out var database))
            database = (await CreateOwnedDatabaseAsync(prefix, reusable: true), false);
        var connectionString = database.ConnectionString;
        if (emptySchema && database.HasSchema) await ResetDatabaseAsync(connectionString, Owned[connectionString].PublicSchemaSql, keepSchema: false);
        if (!emptySchema && !database.HasSchema)
        {
            await InitializeStoreAsync(connectionString);
            await using var check = new NpgsqlConnection(ResetConnectionString(connectionString));
            await check.OpenAsync();
            if (!await reference.MatchesFreshAsync(check)) throw new InvalidOperationException("Initialized PostgreSQL database differs from the fresh store fingerprint; not leased.");
        }
        // The store derives more pools from this string (ownership, wake-ups) by extending its application name.
        // A name unique to the lease keeps every pool to one lease, so connections the reset terminates are
        // never handed to the next test.
        var lease = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = $"IxIFlow test lease {Interlocked.Increment(ref _leases)}"
        }.ConnectionString;
        if (!Leased.TryAdd(lease, connectionString))
            throw new InvalidOperationException("A PostgreSQL test database was leased twice.");
        return lease;
    }

    /// <summary>Creates a database for tests of persistent database-level state; never reused.</summary>
    public static async Task<string> CreateFreshDatabaseAsync(string prefix)
    {
        var connectionString = await CreateOwnedDatabaseAsync(prefix, reusable: false);
        Leased.TryAdd(connectionString, connectionString);
        return connectionString;
    }

    private static async Task<string> CreateOwnedDatabaseAsync(string prefix, bool reusable)
    {
        var server = await Server.Value;
        // Names longer than 63 bytes would be cut, so the prefix is cut first and the unique part kept.
        var start = prefix.ToLowerInvariant();
        var name = $"{start[..Math.Min(start.Length, 30)]}_{Guid.NewGuid():N}";
        var connectionString = new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;
        await CreateGate.WaitAsync();
        try
        {
            await using var connection = new NpgsqlConnection(AdminConnectionString(server));
            await connection.OpenAsync();
            // The session lock is shared by all suite processes on this server. Closing this
            // non-pooled connection releases it, including when CREATE or initialization fails.
            await ExecuteAsync(connection, "SELECT pg_advisory_lock(1249412695, 1)");
            await ExecuteAsync(connection, $"CREATE DATABASE {Quote(name)}");
            Owned.TryAdd(connectionString, new(reusable, ""));
            if (reusable)
            {
                // Preserve the actual template's default owner/ACL, including older or externally
                // configured servers. PostgreSQL 17 normally uses pg_database_owner and PUBLIC USAGE.
                await using var database = new NpgsqlConnection(ResetConnectionString(connectionString));
                await database.OpenAsync();
                await using var defaults = database.CreateCommand();
                defaults.CommandTimeout = AdminTimeoutSeconds;
                defaults.CommandText = """
                    SELECT 'CREATE SCHEMA public AUTHORIZATION ' || quote_ident(pg_get_userbyid(nspowner)) || ';'
                        || 'REVOKE ALL ON SCHEMA public FROM PUBLIC;'
                        || coalesce((SELECT string_agg('GRANT ' || privilege_type || ' ON SCHEMA public TO '
                            || CASE WHEN grantee = 0 THEN 'PUBLIC' ELSE quote_ident(pg_get_userbyid(grantee)) END
                            || CASE WHEN is_grantable THEN ' WITH GRANT OPTION' ELSE '' END || ';', '')
                            FROM aclexplode(coalesce(nspacl, acldefault('n', nspowner)))), '')
                        || 'COMMENT ON SCHEMA public IS ''standard public schema'';'
                    FROM pg_namespace WHERE nspname = 'public'
                    """;
                var restoreSql = (string?)await defaults.ExecuteScalarAsync()
                    ?? throw new InvalidOperationException("A new PostgreSQL test database has no public schema.");
                Owned[connectionString] = new(true, restoreSql);
            }
            return connectionString;
        }
        finally { CreateGate.Release(); }
    }

    /// <summary>Resets and returns a lease, or physically drops a fresh database.</summary>
    public static async Task DropDatabaseAsync(string lease)
    {
        if (!Leased.TryRemove(lease, out var connectionString) || !Owned.TryGetValue(connectionString, out var database))
            throw new InvalidOperationException("The PostgreSQL test database is not an outstanding lease.");
        ClearDatabasePool(lease);
        if (!database.Reusable)
        {
            await DestroyDatabaseAsync(connectionString);
            return;
        }
        try
        {
            var hasSchema = await ResetDatabaseAsync(connectionString, database.PublicSchemaSql);
            Available.Push((connectionString, hasSchema));
        }
        catch (Exception error) when (error is NpgsqlException or InvalidOperationException)
        {
            // The test is over and its result stands; only this database's reuse failed (for example a session
            // that would not end under load). It is never handed out again and is destroyed at process exit.
            Console.Error.WriteLine($"IxIFlow test database retired after a failed PostgreSQL reset: {error.Message}");
        }
    }

    private static async Task<bool> ResetDatabaseAsync(string connectionString, string publicSchemaSql, bool keepSchema = true)
    {
        var reference = keepSchema && Environment.GetEnvironmentVariable("IXIFLOW_TEST_KEEP_SCHEMA") != "0"
            ? await Reference.Value : null;
        var hasSchema = false;
        ClearDatabasePool(connectionString);
        var name = Quote(new NpgsqlConnectionStringBuilder(connectionString).Database!);
        await using var admin = new NpgsqlConnection(AdminConnectionString(connectionString));
        await admin.OpenAsync();
        await using (var connection = new NpgsqlConnection(ResetConnectionString(connectionString)))
        {
            await connection.OpenAsync();
            // Keep our reset session connected, but prevent pooled/reconnecting clients entering
            // during reset. A failed reset is discarded even if ALLOW_CONNECTIONS stays false.
            await ExecuteAsync(admin, $"ALTER DATABASE {name} ALLOW_CONNECTIONS false");
            await ExecuteAsync(connection, """
                DO $$
                DECLARE backend record;
                BEGIN
                    FOR backend IN SELECT pid FROM pg_stat_activity
                        WHERE datname = current_database() AND pid <> pg_backend_pid()
                    LOOP
                        IF NOT pg_terminate_backend(backend.pid, 10000)
                            AND EXISTS (SELECT 1 FROM pg_stat_get_activity(backend.pid)) THEN
                            RAISE EXCEPTION 'Cannot terminate PostgreSQL test backend %', backend.pid;
                        END IF;
                    END LOOP;
                END $$;
                """);
            await ExecuteAsync(connection, "BEGIN;");
            hasSchema = reference != null && await reference.TryWipeAsync(connection);
            if (!hasSchema) await ExecuteAsync(connection, """
                DO $$
                DECLARE schema_name text;
                BEGIN
                    FOR schema_name IN SELECT nspname FROM pg_namespace
                        WHERE nspname !~ '^pg_' AND nspname <> 'information_schema'
                    LOOP
                        EXECUTE format('DROP SCHEMA %I CASCADE', schema_name);
                    END LOOP;
                END $$;
                """ + publicSchemaSql + """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_namespace WHERE nspname !~ '^pg_'
                        AND nspname NOT IN ('information_schema', 'public'))
                        OR NOT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname = 'public')
                        OR EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                            WHERE c.oid >= 16384 OR (n.nspname !~ '^pg_' AND n.nspname <> 'information_schema'))
                        OR EXISTS (SELECT 1 FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace
                            WHERE t.oid >= 16384 OR (n.nspname !~ '^pg_' AND n.nspname <> 'information_schema'))
                        OR EXISTS (SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                            WHERE p.oid >= 16384 OR (n.nspname !~ '^pg_' AND n.nspname <> 'information_schema')) THEN
                        RAISE EXCEPTION 'PostgreSQL test database reset left user objects or schemas';
                    END IF;
                END $$;
                """);
            if (!hasSchema && _emptySchema != await DatabaseSchemaFingerprint.ReadSchemaAsync(connection, sqlServer: false))
                throw new InvalidOperationException("Full PostgreSQL reset differs from the fresh empty catalog; database retired.");
            await ExecuteAsync(connection, "COMMIT;");
        }
        await ExecuteAsync(admin, $"ALTER DATABASE {name} ALLOW_CONNECTIONS true");
        return hasSchema;
    }

    private static async Task InitializeStoreAsync(string connectionString)
    {
        var store = new PostgreSqlWorkflowStateRepository(connectionString);
        await store.GetCatalogRevisionAsync();
    }

    private static async Task<DatabaseSchemaFingerprint> CaptureReferenceAsync()
    {
        var database = await CreateOwnedDatabaseAsync("ixiflow_reference", reusable: false);
        try
        {
            await using var connection = new NpgsqlConnection(ResetConnectionString(database));
            await connection.OpenAsync();
            _emptySchema = await DatabaseSchemaFingerprint.ReadSchemaAsync(connection, sqlServer: false);
            await InitializeStoreAsync(database);
            var reference = await DatabaseSchemaFingerprint.CaptureAsync(connection, sqlServer: false);
            Console.WriteLine($"PostgreSQL fresh-store fingerprint: schema={reference.Schema}, rows={reference.Data}");
            return reference;
        }
        finally { await DestroyDatabaseAsync(database); }
    }

    private static async Task DestroyDatabaseAsync(string connectionString)
    {
        ClearDatabasePool(connectionString);
        var name = new NpgsqlConnectionStringBuilder(connectionString).Database!;
        await using var connection = new NpgsqlConnection(AdminConnectionString(connectionString));
        await connection.OpenAsync();
        await ExecuteAsync(connection, $"DROP DATABASE {Quote(name)} WITH (FORCE)");
        Owned.TryRemove(connectionString, out _);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = AdminTimeoutSeconds;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static string AdminConnectionString(string connectionString) =>
        new NpgsqlConnectionStringBuilder(ResetConnectionString(connectionString)) { Database = "postgres" }.ConnectionString;

    private static string ResetConnectionString(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false, Multiplexing = false, Enlist = false }.ConnectionString;

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    private static void ClearDatabasePool(string connectionString)
    {
        // Pools derived from the lease are terminated by reset and never used again; unrelated databases stay warm.
        using var connection = new NpgsqlConnection(connectionString);
        NpgsqlConnection.ClearPool(connection);
    }

    private static async Task<string> StartAsync()
    {
        var external = Environment.GetEnvironmentVariable(ExternalServerVariable);
        if (!string.IsNullOrWhiteSpace(external))
            return external;

        // Every test class gets a database and its hosts keep pools open, so the server takes more than the default.
        var container = new PostgreSqlBuilder("postgres:17-alpine")
            .WithCreateParameterModifier(parameters => parameters.HostConfig.ShmSize = 1024L * 1024 * 1024)
            .WithCommand("-c", "max_connections=500")
            .Build();
        await container.StartAsync();
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            container.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return container.GetConnectionString();
    }
}

/// <summary>
/// An empty database leased for one test class on first use and destroyed when the run ends. Test classes run in
/// parallel, and their hosts poll and claim whatever their store holds, so each class keeps its rows to itself.
/// </summary>
public static class PostgreSqlTestDatabase<TTests>
{
    private static readonly Lazy<string> Database = new(() => Task.Run(CreateAsync).GetAwaiter().GetResult());

    public static string ConnectionString => Database.Value;

    private static Task<string> CreateAsync() =>
        PostgreSqlTestServer.CreateDatabaseAsync("ixiflow_" + typeof(TTests).Name, emptySchema: true);
}
