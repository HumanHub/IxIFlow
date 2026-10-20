using System.Data.Common;
using Dapper;
using IxIFlow.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using IxIFlow.Tests.Infrastructure;
using Npgsql;

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

    internal static async Task<ProfileStore> CreateAsync(string profile, TimeProvider? time = null) => profile switch
    {
        LocalMemory => ProfileStore.Memory(time),
        LocalSqlite => ProfileStore.Sqlite(time),
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
    private readonly Func<DbConnection>? _newConnection;
    private readonly List<IAsyncDisposable> _repositories = [];

    private ProfileStore(string profile, Func<TimeProvider?, IWorkflowStateRepository> newRepository, Func<Task> dispose,
        Func<DbConnection>? newConnection = null)
    {
        Profile = profile;
        _newRepository = newRepository;
        _dispose = dispose;
        _newConnection = newConnection;
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

    public static ProfileStore Memory(TimeProvider? time = null)
    {
        var repository = new InMemoryWorkflowStateRepository(time ?? TimeProvider.System);
        return new ProfileStore(StoreProfiles.LocalMemory, _ => repository, () => Task.CompletedTask);
    }

    public static ProfileStore Sqlite(TimeProvider? time = null)
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.Conformance", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(folder, "workflows.db"), DefaultTimeout = 1
        }.ConnectionString;
        return new ProfileStore(StoreProfiles.LocalSqlite,
            clock => new SqliteWorkflowStateRepository(connectionString, clock ?? time),
            () => SqliteTestFiles.DeleteAsync(folder));
    }

    public static async Task<ProfileStore> SqlServerAsync()
    {
        var connectionString = await SqlServerTestServer.CreateDatabaseAsync("IxIFlowConformance");
        return new ProfileStore(StoreProfiles.DistributedSqlServer,
            _ => new SqlWorkflowStateRepository(connectionString),
            () => SqlServerTestServer.DropDatabaseAsync(connectionString), () => new SqlConnection(connectionString));
    }

    public static async Task<ProfileStore> PostgreSqlAsync()
    {
        var connectionString = await PostgreSqlTestServer.CreateDatabaseAsync("ixiflow_conformance");
        return new ProfileStore(StoreProfiles.DistributedPostgreSql,
            _ => new PostgreSqlWorkflowStateRepository(connectionString),
            () => PostgreSqlTestServer.DropDatabaseAsync(connectionString), () => new NpgsqlConnection(connectionString));
    }

    internal async Task ExpireExecutionLeaseAsync(string instanceId)
    {
        await using var connection = _newConnection!();
        Assert.Equal(1, await connection.ExecuteAsync(Profile == StoreProfiles.DistributedSqlServer
            ? "UPDATE dbo.IxIFlowWorkflowLeases SET ExpiresAtUtc=DATEADD(SECOND,-1,SYSUTCDATETIME()) WHERE InstanceId=@Id"
            : "UPDATE ixiflow_workflow_leases SET expires_at_utc=clock_timestamp()-INTERVAL '1 second' WHERE instance_id=@Id",
            new { Id = instanceId }));
    }

    internal async Task AgeRetentionAsync()
    {
        await using var connection = _newConnection!();
        await connection.ExecuteAsync(Profile == StoreProfiles.DistributedSqlServer
            ? "UPDATE dbo.IxIFlowWorkflowInstances SET EndedAtUtc=DATEADD(DAY,-2,SYSUTCDATETIME()) WHERE EndedAtUtc IS NOT NULL; UPDATE dbo.IxIFlowWorkflowTombstones SET ExpiresAtUtc=DATEADD(DAY,-1,SYSUTCDATETIME())"
            : "UPDATE ixiflow_workflow_instances SET ended_at_utc=clock_timestamp()-INTERVAL '2 days' WHERE ended_at_utc IS NOT NULL; UPDATE ixiflow_workflow_tombstones SET expires_at_utc=clock_timestamp()-INTERVAL '1 day'");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var repository in _repositories)
            await repository.DisposeAsync();
        await _dispose();
    }
}
