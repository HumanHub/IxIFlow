using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.Conformance;

/// <summary>
/// The test profiles from spec J-02. Each conformance scenario runs in every profile it applies to.
/// The SQLite profile is added when that store exists.
/// </summary>
public static class StoreProfiles
{
    public const string LocalMemory = "local-memory";
    public const string DistributedSqlServer = "distributed-sqlserver";
    public const string DistributedPostgreSql = "distributed-postgresql";

    /// <summary>Every profile. Use for scenarios that apply to any store.</summary>
    public static TheoryData<string> All => new() { LocalMemory, DistributedSqlServer, DistributedPostgreSql };

    /// <summary>Profiles with several hosts sharing a durable store.</summary>
    public static TheoryData<string> Distributed => new() { DistributedSqlServer, DistributedPostgreSql };

    internal static async Task<ProfileStore> CreateAsync(string profile) => profile switch
    {
        LocalMemory => ProfileStore.Memory(),
        DistributedSqlServer => await ProfileStore.SqlServerAsync(),
        DistributedPostgreSql => await ProfileStore.PostgreSqlAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown test profile")
    };
}

/// <summary>An isolated store for one test. Hosts created from it share the same data.</summary>
internal sealed class ProfileStore : IAsyncDisposable
{
    private readonly Func<IWorkflowStateRepository> _newRepository;
    private readonly Func<Task> _dispose;

    private ProfileStore(string profile, Func<IWorkflowStateRepository> newRepository, Func<Task> dispose)
    {
        Profile = profile;
        _newRepository = newRepository;
        _dispose = dispose;
    }

    public string Profile { get; }

    /// <summary>A repository for one host. In memory, every host shares one instance.</summary>
    public IWorkflowStateRepository NewRepository() => _newRepository();

    public static ProfileStore Memory()
    {
        var repository = new InMemoryWorkflowStateRepository();
        return new ProfileStore(StoreProfiles.LocalMemory, () => repository, () => Task.CompletedTask);
    }

    public static async Task<ProfileStore> SqlServerAsync()
    {
        var connectionString = await SqlServerTestServer.CreateDatabaseAsync("IxIFlowConformance");
        return new ProfileStore(StoreProfiles.DistributedSqlServer,
            () => new SqlWorkflowStateRepository(connectionString),
            () => SqlServerTestServer.DropDatabaseAsync(connectionString));
    }

    public static async Task<ProfileStore> PostgreSqlAsync()
    {
        var connectionString = await PostgreSqlTestServer.CreateDatabaseAsync("ixiflow_conformance");
        return new ProfileStore(StoreProfiles.DistributedPostgreSql,
            () => new PostgreSqlWorkflowStateRepository(connectionString),
            () => PostgreSqlTestServer.DropDatabaseAsync(connectionString));
    }

    public ValueTask DisposeAsync() => new(_dispose());
}
