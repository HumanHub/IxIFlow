using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace IxIFlow.Tests.Infrastructure;

/// <summary>
/// One SQL Server for the whole test run. Uses IXIFLOW_TEST_SQL_CONNECTION_STRING when it is set;
/// otherwise starts a container through Testcontainers on first use.
/// </summary>
internal static class SqlServerTestServer
{
    private const string ExternalServerVariable = "IXIFLOW_TEST_SQL_CONNECTION_STRING";
    private const string SharedDatabase = "IxIFlowTests";
    private static readonly Lazy<Task<string>> Server = new(StartAsync);
    private static readonly Lazy<string> Shared = new(() =>
        Task.Run(CreateSharedDatabaseAsync).GetAwaiter().GetResult());

    /// <summary>
    /// The shared test database for the whole run. With IXIFLOW_TEST_SQL_CONNECTION_STRING set,
    /// that connection string is used as is; otherwise an IxIFlowTests database is created in the container.
    /// </summary>
    public static string ConnectionString => Shared.Value;

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

    private static async Task<string> CreateSharedDatabaseAsync()
    {
        var server = await Server.Value;
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ExternalServerVariable)))
            return server;

        await using (var connection = new SqlConnection(server))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"IF DB_ID('{SharedDatabase}') IS NULL CREATE DATABASE [{SharedDatabase}]";
            await command.ExecuteNonQueryAsync();
        }

        return new SqlConnectionStringBuilder(server) { InitialCatalog = SharedDatabase }.ConnectionString;
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
