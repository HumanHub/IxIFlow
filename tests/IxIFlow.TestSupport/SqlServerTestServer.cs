using System.Collections.Concurrent;
using IxIFlow.Core;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace IxIFlow.Tests.Infrastructure;

/// <summary>
/// One SQL Server for the whole test run. Uses IXIFLOW_TEST_SQL_CONNECTION_STRING when it is set;
/// otherwise starts a container through Testcontainers on first use. Tests lease isolated databases on
/// it, through <see cref="SqlTestDatabase{TTests}"/> or <see cref="CreateDatabaseAsync"/>.
/// </summary>
public static class SqlServerTestServer
{
    private const string ExternalServerVariable = "IXIFLOW_TEST_SQL_CONNECTION_STRING";
    private static readonly Lazy<Task<string>> Server = new(StartAsync);
    private static readonly SemaphoreSlim CreateGate = new(1, 1);
    private static readonly ConcurrentStack<(string ConnectionString, bool HasSchema)> Available = new();
    private static readonly Lazy<Task<DatabaseSchemaFingerprint>> Reference = new(CaptureReferenceAsync);
    private static string _emptySchema = "";
    private static readonly ConcurrentDictionary<string, bool> Owned = new();
    private static readonly ConcurrentDictionary<string, string> Leased = new();
    private static long _leases;

    static SqlServerTestServer()
    {
        // Register before StartAsync registers container disposal. Also clean up external servers.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var connectionString in Owned.Keys)
            {
                try
                {
                    DestroyDatabaseAsync(connectionString).GetAwaiter().GetResult();
                }
                catch (Exception error) when (error is SqlException or InvalidOperationException)
                {
                    // A crashed/removed server cannot be cleaned up here.
                    Console.Error.WriteLine($"SQL test database cleanup failed: {error.Message}");
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

        if (emptySchema && database.HasSchema)
            await ResetDatabaseAsync(database.ConnectionString, keepSchema: false);

        if (!emptySchema && !database.HasSchema)
        {
            await InitializeStoreAsync(database.ConnectionString);

            await using var check =
                new SqlConnection(new SqlConnectionStringBuilder(database.ConnectionString) { Pooling = false }.ConnectionString);

            await check.OpenAsync();

            if (!await reference.MatchesFreshAsync(check))
                throw new InvalidOperationException("Initialized SQL database differs from the fresh store fingerprint; not leased.");
        }

        var lease = new SqlConnectionStringBuilder(database.ConnectionString)
            { ApplicationName = $"IxIFlow test lease {Interlocked.Increment(ref _leases)}" }.ConnectionString;

        if (!Leased.TryAdd(lease, database.ConnectionString))
            throw new InvalidOperationException("A SQL test database was leased twice.");

        return lease;
    }

    /// <summary>Creates a new database for tests that change database-level options; never reused.</summary>
    public static async Task<string> CreateFreshDatabaseAsync(string prefix)
    {
        var connectionString = await CreateOwnedDatabaseAsync(prefix, reusable: false);
        Leased.TryAdd(connectionString, connectionString);

        return connectionString;
    }

    private static async Task<string> CreateOwnedDatabaseAsync(string prefix, bool reusable)
    {
        var server = await Server.Value;
        var name = $"{prefix[..Math.Min(prefix.Length, 80)]}_{Guid.NewGuid():N}";
        var connectionString = new SqlConnectionStringBuilder(server) { InitialCatalog = name }.ConnectionString;
        await CreateGate.WaitAsync();

        try
        {
            for (var attempt = 0;; attempt++)
            {
                try
                {
                    await using var connection = new SqlConnection(AdminConnectionString(server));
                    await connection.OpenAsync();

                    // All suite processes cooperate on the same master lock. CREATE DATABASE still
                    // retries 1807 in case a non-cooperating client holds model's lock.
                    await using var gate = connection.CreateCommand();
                    gate.CommandTimeout = 90;

                    gate.CommandText = """
                                       DECLARE @result int;
                                       EXEC @result = sys.sp_getapplock @Resource = 'IxIFlow.Tests.CreateDatabase',
                                           @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 60000;
                                       IF @result < 0 THROW 51000, 'Cannot acquire test database creation lock', 1;
                                       """;

                    await gate.ExecuteNonQueryAsync();
                    await using var command = connection.CreateCommand();
                    command.CommandTimeout = 60;
                    command.CommandText = $"CREATE DATABASE {Quote(name)}";
                    await command.ExecuteNonQueryAsync();
                    Owned.TryAdd(connectionString, reusable);

                    return connectionString;
                }
                catch (SqlException error) when (attempt < 5 && error.Errors.Cast<SqlError>().Any(item => item.Number == 1807))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * (1 << attempt) + Random.Shared.Next(200)));
                }
            }
        }
        finally
        {
            CreateGate.Release();
        }
    }

    /// <summary>Resets and returns a lease, or physically drops a fresh database.</summary>
    public static async Task DropDatabaseAsync(string lease)
    {
        if (!Leased.TryRemove(lease, out var connectionString) || !Owned.TryGetValue(connectionString, out var reusable))
            throw new InvalidOperationException("The SQL test database is not an outstanding lease.");

        ClearDatabasePool(lease);

        if (!reusable)
        {
            await DestroyDatabaseAsync(connectionString);

            return;
        }

        try
        {
            var hasSchema = await ResetDatabaseAsync(connectionString);
            Available.Push((connectionString, hasSchema));
        }
        catch (Exception error) when (error is SqlException or InvalidOperationException)
        {
            // The test is over and its result stands; only this database's reuse failed (for example a session
            // that would not end under load). It is never handed out again and is destroyed at process exit.
            Console.Error.WriteLine($"IxIFlow test database retired after a failed SQL Server reset: {error.Message}");
        }
    }

    private static async Task<bool> ResetDatabaseAsync(string connectionString, bool keepSchema = true)
    {
        var reference = keepSchema && Environment.GetEnvironmentVariable("IXIFLOW_TEST_KEEP_SCHEMA") != "0"
            ? await Reference.Value
            : null;

        ClearDatabasePool(connectionString);
        await using var connection = new SqlConnection(AdminConnectionString(connectionString));
        await connection.OpenAsync();

        await DatabaseSchemaFingerprint.ExecuteAsync(connection, $"""
                                                                  USE {Quote(new SqlConnectionStringBuilder(connectionString).InitialCatalog)};
                                                                  ALTER DATABASE CURRENT SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                                                                  SET XACT_ABORT ON;
                                                                  BEGIN TRANSACTION;
                                                                  """);

        if (reference != null && await reference.TryWipeAsync(connection))
        {
            await DatabaseSchemaFingerprint.ExecuteAsync(connection, "COMMIT; ALTER DATABASE CURRENT SET MULTI_USER;");

            return true;
        }

        await using var command = connection.CreateCommand();
        command.CommandTimeout = 60;

        // Enter while the database is still MULTI_USER, then keep this session in the database
        // until MULTI_USER is restored. It already owns the single-user slot when other sessions
        // are evicted, so a pooled/reconnecting client cannot take it between ALTER and USE.
        // One non-pooled administrative session takes exclusive access, including rolling back
        // transactions left by a failed test. Dropping tables also removes their indexes, defaults,
        // checks and DML triggers. Remove FKs first, then tables, sequences and table/alias types.
        // Current engine SQL creates no views or routines; unknown objects fail the empty check.
        command.CommandText = $"""
                               DECLARE @sql nvarchar(max) = N'';
                               SELECT @sql = @sql + N'ALTER TABLE ' + QUOTENAME(OBJECT_SCHEMA_NAME(parent_object_id)) + N'.'
                                   + QUOTENAME(OBJECT_NAME(parent_object_id)) + N' DROP CONSTRAINT ' + QUOTENAME(name) + N';'
                                   FROM sys.foreign_keys WHERE is_ms_shipped = 0;
                               EXEC sys.sp_executesql @sql;
                               SET @sql = N'';
                               SELECT @sql = @sql + N'DROP TABLE ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N';'
                                   FROM sys.tables WHERE is_ms_shipped = 0;
                               EXEC sys.sp_executesql @sql;
                               SET @sql = N'';
                               SELECT @sql = @sql + N'DROP SEQUENCE ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N';'
                                   FROM sys.sequences WHERE is_ms_shipped = 0;
                               EXEC sys.sp_executesql @sql;
                               SET @sql = N'';
                               SELECT @sql = @sql + N'DROP TYPE ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N';'
                                   FROM sys.types WHERE is_user_defined = 1;
                               EXEC sys.sp_executesql @sql;
                               IF EXISTS (SELECT 1 FROM sys.objects WHERE is_ms_shipped = 0)
                                   OR EXISTS (SELECT 1 FROM sys.types WHERE is_user_defined = 1)
                                   OR EXISTS (SELECT 1 FROM sys.schemas WHERE schema_id BETWEEN 5 AND 16383)
                                   THROW 51000, 'SQL test database reset left user objects or schemas', 1;
                               """;

        await command.ExecuteNonQueryAsync();

        if (_emptySchema != await DatabaseSchemaFingerprint.ReadSchemaAsync(connection, sqlServer: true))
            throw new InvalidOperationException("Full SQL reset differs from the fresh empty catalog; database retired.");

        await DatabaseSchemaFingerprint.ExecuteAsync(connection, "COMMIT; ALTER DATABASE CURRENT SET MULTI_USER;");

        return false;
    }

    private static async Task InitializeStoreAsync(string connectionString)
    {
        var store = new SqlServerWorkflowStateRepository(connectionString);
        await store.GetCatalogRevisionAsync();
    }

    private static async Task<DatabaseSchemaFingerprint> CaptureReferenceAsync()
    {
        var database = await CreateOwnedDatabaseAsync("IxIFlowReference", reusable: false);

        try
        {
            await using var connection = new SqlConnection(new SqlConnectionStringBuilder(database) { Pooling = false }.ConnectionString);
            await connection.OpenAsync();
            _emptySchema = await DatabaseSchemaFingerprint.ReadSchemaAsync(connection, sqlServer: true);
            await InitializeStoreAsync(database);
            var reference = await DatabaseSchemaFingerprint.CaptureAsync(connection, sqlServer: true);
            Console.WriteLine($"SQL Server fresh-store fingerprint: schema={reference.Schema}, rows={reference.Data}");

            return reference;
        }
        finally
        {
            await DestroyDatabaseAsync(database);
        }
    }

    private static async Task DestroyDatabaseAsync(string connectionString)
    {
        var name = Quote(new SqlConnectionStringBuilder(connectionString).InitialCatalog);
        ClearDatabasePool(connectionString);
        await using var connection = new SqlConnection(AdminConnectionString(connectionString));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 60;

        command.CommandText = $"""
                               ALTER DATABASE {name} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                               DROP DATABASE {name};
                               """;

        await command.ExecuteNonQueryAsync();
        Owned.TryRemove(connectionString, out _);
    }

    private static string AdminConnectionString(string connectionString) =>
        new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master", Pooling = false }.ConnectionString;

    private static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";

    private static void ClearDatabasePool(string connectionString)
    {
        // ClearAllPools also evicts unrelated tests' connections. Other pools for this database (e.g. ownership
        // traffic with a different Application Name) are disconnected by SINGLE_USER instead.
        using var connection = new SqlConnection(connectionString);
        SqlConnection.ClearPool(connection);
    }

    private static async Task<string> StartAsync()
    {
        var external = Environment.GetEnvironmentVariable(ExternalServerVariable);

        if (!string.IsNullOrWhiteSpace(external))
            return external;

        var container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
        await container.StartAsync();

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            container.DisposeAsync().AsTask().GetAwaiter().GetResult();

        return container.GetConnectionString();
    }
}

/// <summary>
/// An empty database leased for one test class on first use and dropped when the run ends. Test
/// classes run in parallel, and their hosts poll and claim whatever their store holds, so each class
/// keeps its rows to itself.
/// </summary>
public static class SqlTestDatabase<TTests>
{
    private static readonly Lazy<string> Database = new(() => Task.Run(CreateAsync).GetAwaiter().GetResult());

    public static string ConnectionString => Database.Value;

    private static Task<string> CreateAsync() =>
        SqlServerTestServer.CreateDatabaseAsync("IxIFlow_" + typeof(TTests).Name, emptySchema: true);
}