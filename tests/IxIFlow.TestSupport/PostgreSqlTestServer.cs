using Npgsql;
using Testcontainers.PostgreSql;

namespace IxIFlow.Tests.Infrastructure;

/// <summary>
/// One PostgreSQL server for the whole test run. Uses IXIFLOW_TEST_POSTGRESQL_CONNECTION_STRING when it is set;
/// otherwise starts a container through Testcontainers on first use. Tests get their own databases on it, through
/// <see cref="PostgreSqlTestDatabase{TTests}"/> or <see cref="CreateDatabaseAsync"/>.
/// </summary>
public static class PostgreSqlTestServer
{
    private const string ExternalServerVariable = "IXIFLOW_TEST_POSTGRESQL_CONNECTION_STRING";
    private static readonly Lazy<Task<string>> Server = new(StartAsync);

    /// <summary>Creates an empty database and returns a connection string for it.</summary>
    public static async Task<string> CreateDatabaseAsync(string prefix)
    {
        var server = await Server.Value;
        // Names longer than 63 bytes would be cut, so the prefix is cut first and the unique part kept.
        var start = prefix.ToLowerInvariant();
        var name = $"{start[..Math.Min(start.Length, 30)]}_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(server))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{name}\"";
            await command.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;
    }

    /// <summary>Drops a database created by <see cref="CreateDatabaseAsync"/>, closing its connections.</summary>
    public static async Task DropDatabaseAsync(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var name = builder.Database;
        builder.Database = "postgres";
        NpgsqlConnection.ClearAllPools();
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE \"{name}\" WITH (FORCE)";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> StartAsync()
    {
        var external = Environment.GetEnvironmentVariable(ExternalServerVariable);
        if (!string.IsNullOrWhiteSpace(external))
            return external;

        // Every test class gets a database and its hosts keep pools open, so the server takes more than the default.
        var container = new PostgreSqlBuilder("postgres:17-alpine")
            .WithCommand("-c", "max_connections=500")
            .Build();
        await container.StartAsync();
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            container.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return container.GetConnectionString();
    }
}

/// <summary>
/// An empty database for one test class, created on first use and dropped when the run ends. Test classes run in
/// parallel, and their hosts poll and claim whatever their store holds, so each class keeps its rows to itself.
/// </summary>
public static class PostgreSqlTestDatabase<TTests>
{
    private static readonly Lazy<string> Database = new(() => Task.Run(CreateAsync).GetAwaiter().GetResult());

    public static string ConnectionString => Database.Value;

    private static async Task<string> CreateAsync()
    {
        var connectionString = await PostgreSqlTestServer.CreateDatabaseAsync("ixiflow_" + typeof(TTests).Name);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                PostgreSqlTestServer.DropDatabaseAsync(connectionString).GetAwaiter().GetResult();
            }
            catch (Exception error) when (error is NpgsqlException or InvalidOperationException)
            {
                // The container may already be gone; its databases go with it.
            }
        };
        return connectionString;
    }
}
