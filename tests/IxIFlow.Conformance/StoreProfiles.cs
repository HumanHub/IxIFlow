using System.Data.Common;
using Dapper;
using IxIFlow.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using IxIFlow.Tests.Infrastructure;
using Npgsql;

namespace IxIFlow.Tests.Conformance;

/// <summary>
/// The store and host test profiles. Each conformance scenario runs in every profile it applies to.
/// IXIFLOW_PROFILES selects comma-separated canonical names; unset/blank selects everything.
/// Include "unprofiled" once to run facts and plain-provider rows (sqlserver/postgresql/sqlite).
/// </summary>
public static class StoreProfiles
{
    public const string LocalSqlite = "local-sqlite";
    public const string LocalMemory = "local-memory";
    public const string DistributedSqlServer = "distributed-sqlserver";
    public const string DistributedPostgreSql = "distributed-postgresql";

    // Opt in explicitly to facts and rows without one of the eight canonical profiles.
    public const string Unprofiled = "unprofiled";
    private static readonly Lazy<HashSet<string>?> Selection = new(ReadSelection);
    [ThreadStatic] internal static bool DiscoveringCatalog;

    internal static IEnumerable<string> Catalog => new[] { LocalMemory, LocalSqlite }.Concat(DistributedCatalog);

    internal static bool Selected(string profile) => Selection.Value?.Contains(profile) ?? true;

    private static HashSet<string>? ReadSelection()
    {
        var value = Environment.GetEnvironmentVariable("IXIFLOW_PROFILES");

        if (string.IsNullOrWhiteSpace(value))
            return null;

        var selected = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);

        var unknown = selected.Except(Catalog.Append(Unprofiled), StringComparer.Ordinal).ToArray();

        if (selected.Count == 0 || unknown.Length != 0)
            throw new ArgumentException($"Invalid IXIFLOW_PROFILES: {value}. Expected: {string.Join(",", Catalog.Append(Unprofiled))}");

        return selected;
    }

    private static IEnumerable<string> Filter(IEnumerable<string> profiles) =>
        profiles.Where(profile => DiscoveringCatalog || Selected(profile));

    /// <summary>Every selected profile. Use for scenarios that apply to any store.</summary>
    public static TheoryData<string> All => new(ProfileNames);

    public static IEnumerable<string> ProfileNames => Filter(Catalog);

    private static IEnumerable<string> DistributedCatalog =>
        new[] { DistributedSqlServer, DistributedPostgreSql }.SelectMany(provider =>
            Enumerable.Range(0, 3).Select(count => count == 0 ? provider : $"{provider}/coordinators-{count}"));

    /// <summary>Profiles that allow several hosts on one store (SQLite permits one host).</summary>
    public static TheoryData<string> MultipleHosts => new(Filter(new[] { LocalMemory }.Concat(DistributedCatalog)));

    /// <summary>Profiles with several hosts sharing a durable store.</summary>
    public static TheoryData<string> Distributed => new(Filter(DistributedCatalog));

    public static TheoryData<string> Coordinated => new(Filter(DistributedCatalog.Where(profile => CoordinatorCount(profile) > 0)));

    internal static int CoordinatorCount(string profile) => profile.Split('/') switch
    {
        [LocalMemory or LocalSqlite or DistributedSqlServer or DistributedPostgreSql] => 0,
        [DistributedSqlServer or DistributedPostgreSql, "coordinators-1"] => 1,
        [DistributedSqlServer or DistributedPostgreSql, "coordinators-2"] => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown test profile")
    };

    internal static async Task<ProfileStore> CreateAsync(string profile, TimeProvider? time = null, bool emptySchema = false)
    {
        var count = CoordinatorCount(profile);

        var store = profile.Split('/')[0] switch
        {
            LocalMemory => ProfileStore.Memory(time),
            LocalSqlite => ProfileStore.Sqlite(time),
            DistributedSqlServer => await ProfileStore.SqlServerAsync(emptySchema),
            DistributedPostgreSql => await ProfileStore.PostgreSqlAsync(emptySchema),
            _ => throw new ArgumentOutOfRangeException(nameof(profile))
        };

        try
        {
            await store.StartCoordinatorsAsync(count);

            return store;
        }
        catch
        {
            await store.DisposeAsync();

            throw;
        }
    }
}

/// <summary>An isolated store for one test. Hosts created from it share the same data.</summary>
internal sealed partial class ProfileStore : IAsyncDisposable
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
            () => SqliteTestFiles.DeleteAsync(folder), () => new SqliteConnection(connectionString));
    }

    public static async Task<ProfileStore> SqlServerAsync(bool emptySchema = false)
    {
        var connectionString = await SqlServerTestServer.CreateDatabaseAsync("IxIFlowConformance", emptySchema);

        return new ProfileStore(StoreProfiles.DistributedSqlServer,
            _ => new SqlServerWorkflowStateRepository(connectionString),
            () => SqlServerTestServer.DropDatabaseAsync(connectionString), () => new SqlConnection(connectionString));
    }

    public static async Task<ProfileStore> PostgreSqlAsync(bool emptySchema = false)
    {
        var connectionString = await PostgreSqlTestServer.CreateDatabaseAsync("ixiflow_conformance", emptySchema);

        return new ProfileStore(StoreProfiles.DistributedPostgreSql,
            _ => new PostgreSqlWorkflowStateRepository(connectionString),
            () => PostgreSqlTestServer.DropDatabaseAsync(connectionString), () => new NpgsqlConnection(connectionString));
    }

    internal DbConnection NewConnection() => _newConnection!();

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
        await StopCoordinatorsAsync();

        foreach (var repository in _repositories)
            await repository.DisposeAsync();

        await _dispose();
    }
}