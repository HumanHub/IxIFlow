using IxIFlow.Core;
using Microsoft.Data.Sqlite;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.Conformance;

/// <summary>
/// The test profiles from spec J-02. Each conformance scenario runs in every profile it applies to.
/// </summary>
public static class StoreProfiles
{
    public const string LocalSqlite = "local-sqlite";
    public const string LocalMemory = "local-memory";
    public const string DistributedSqlServer = "distributed-sqlserver";
    public const string DistributedPostgreSql = "distributed-postgresql";

    /// <summary>Every profile. Use for scenarios that apply to any store.</summary>
    public static TheoryData<string> All => new() { LocalMemory, LocalSqlite, DistributedSqlServer, DistributedPostgreSql };

    /// <summary>Profiles that allow several hosts on one store (C-18 excludes SQLite).</summary>
    public static TheoryData<string> MultipleHosts => new() { LocalMemory, DistributedSqlServer, DistributedPostgreSql };

    /// <summary>Profiles with several hosts sharing a durable store.</summary>
    public static TheoryData<string> Distributed => new() { DistributedSqlServer, DistributedPostgreSql };

    internal static async Task<ProfileStore> CreateAsync(string profile) => profile switch
    {
        LocalMemory => ProfileStore.Memory(),
        LocalSqlite => ProfileStore.Sqlite(),
        DistributedSqlServer => await ProfileStore.SqlServerAsync(),
        DistributedPostgreSql => await ProfileStore.PostgreSqlAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown test profile")
    };
}

/// <summary>An isolated store for one test. Hosts created from it share the same data.</summary>
internal sealed class ProfileStore : IAsyncDisposable
{
    private readonly Func<TimeProvider?, IWorkflowStateRepository> _newRepository;
    private readonly Func<Task> _dispose;
    private readonly List<IAsyncDisposable> _repositories = [];

    private ProfileStore(string profile, Func<TimeProvider?, IWorkflowStateRepository> newRepository, Func<Task> dispose)
    {
        Profile = profile;
        _newRepository = newRepository;
        _dispose = dispose;
    }

    public string Profile { get; }

    /// <summary>A repository for one host. In memory, every host shares one instance.</summary>
    public IWorkflowStateRepository NewRepository(TimeProvider? time = null)
    {
        var repository = _newRepository(time);
        if (repository is IAsyncDisposable disposable)
            _repositories.Add(disposable);
        return repository;
    }

    public static ProfileStore Memory()
    {
        var repository = new InMemoryWorkflowStateRepository();
        return new ProfileStore(StoreProfiles.LocalMemory, _ => repository, () => Task.CompletedTask);
    }

    public static ProfileStore Sqlite()
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.Conformance", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(folder, "workflows.db"), DefaultTimeout = 1
        }.ConnectionString;
        return new ProfileStore(StoreProfiles.LocalSqlite,
            time => new SqliteWorkflowStateRepository(connectionString, time),
            () => SqliteTestFiles.DeleteAsync(folder));
    }

    public static async Task<ProfileStore> SqlServerAsync()
    {
        var connectionString = await SqlServerTestServer.CreateDatabaseAsync("IxIFlowConformance");
        return new ProfileStore(StoreProfiles.DistributedSqlServer,
            _ => new SqlWorkflowStateRepository(connectionString),
            () => SqlServerTestServer.DropDatabaseAsync(connectionString));
    }

    public static async Task<ProfileStore> PostgreSqlAsync()
    {
        var connectionString = await PostgreSqlTestServer.CreateDatabaseAsync("ixiflow_conformance");
        return new ProfileStore(StoreProfiles.DistributedPostgreSql,
            _ => new PostgreSqlWorkflowStateRepository(connectionString),
            () => PostgreSqlTestServer.DropDatabaseAsync(connectionString));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var repository in _repositories)
            await repository.DisposeAsync();
        await _dispose();
    }
}
