using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace IxIFlow.Tests.Infrastructure;

/// <summary>
/// One SQL Server for the whole test run. Uses IXIFLOW_TEST_SQL_CONNECTION_STRING when it is set;
/// otherwise starts a container through Testcontainers on first use. Tests get their own databases on
/// it, through <see cref="SqlTestDatabase{TTests}"/> or <see cref="CreateDatabaseAsync"/>.
/// </summary>
public static class SqlServerTestServer
{
    private const string ExternalServerVariable = "IXIFLOW_TEST_SQL_CONNECTION_STRING";
    private static readonly Lazy<Task<string>> Server = new(StartAsync);

    /// <summary>Creates an empty database and returns a connection string for it.</summary>
    public static async Task<string> CreateDatabaseAsync(string prefix)
    {
        var server = await Server.Value;
        var name = $"{prefix}_{Guid.NewGuid():N}";
        await using (var connection = new SqlConnection(server))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{name}]";
            await command.ExecuteNonQueryAsync();
        }

        return new SqlConnectionStringBuilder(server) { InitialCatalog = name }.ConnectionString;
    }

    /// <summary>Drops a database created by <see cref="CreateDatabaseAsync"/>.</summary>
    public static async Task DropDatabaseAsync(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var name = builder.InitialCatalog;
        builder.InitialCatalog = "master";
        SqlConnection.ClearAllPools();
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
            DROP DATABASE [{name}];
            """;
        await command.ExecuteNonQueryAsync();
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
/// An empty database for one test class, created on first use and dropped when the run ends. Test
/// classes run in parallel, and their hosts poll and claim whatever their store holds, so each class
/// keeps its rows to itself.
/// </summary>
public static class SqlTestDatabase<TTests>
{
    private static readonly Lazy<string> Database = new(() => Task.Run(CreateAsync).GetAwaiter().GetResult());

    public static string ConnectionString => Database.Value;

    private static async Task<string> CreateAsync()
    {
        var connectionString = await SqlServerTestServer.CreateDatabaseAsync("IxIFlow_" + typeof(TTests).Name);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                SqlServerTestServer.DropDatabaseAsync(connectionString).GetAwaiter().GetResult();
            }
            catch (Exception error) when (error is SqlException or InvalidOperationException)
            {
                // The container may already be gone; its databases go with it.
            }
        };
        return connectionString;
    }
}
