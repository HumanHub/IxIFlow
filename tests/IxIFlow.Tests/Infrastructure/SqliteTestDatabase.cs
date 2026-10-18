using Microsoft.Data.Sqlite;

namespace IxIFlow.Tests.Infrastructure;

/// <summary>One local database per test class, removed with its WAL and owner files afterwards.</summary>
public sealed class SqliteTestDatabase<TTests> : IAsyncLifetime
{
    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "IxIFlow.Sqlite.Tests",
        typeof(TTests).Name + "-" + Guid.NewGuid().ToString("N"));

    public static string ConnectionString { get; } = Create();

    private static string Create()
    {
        Directory.CreateDirectory(Folder);
        return new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(Folder, "workflows.db"), DefaultTimeout = 1
        }.ConnectionString;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => SqliteTestFiles.DeleteAsync(Folder);
}
