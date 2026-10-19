using System.Data.Common;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "host")]
[Trait("Spec", "C-06")]
[Trait("Spec", "C-14")]
public sealed class MemberRegistryTests
{
    public static TheoryData<MemberRegistryScenario> Scenarios => new(Enum.GetValues<MemberRegistryScenario>());

    [Theory]
    [MemberData(nameof(Scenarios))]
    public Task MemoryRegistryContract(MemberRegistryScenario scenario)
    {
        var time = new FakeTimeProvider();
        return new MemberRegistryContract(new InMemoryWorkflowStateRepository(time), (_, by) =>
        {
            time.Advance(by);
            return Task.CompletedTask;
        }, () => Task.FromResult(time.GetUtcNow().UtcDateTime)).VerifyAsync(scenario);
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    [Trait("Category", "SqliteIntegration")]
    public async Task SqliteRegistryContract(MemberRegistryScenario scenario)
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.Registry", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var cs = "Data Source=" + Path.Combine(folder, "registry.db");
        try
        {
            var time = new FakeTimeProvider();
            await using var store = new SqliteWorkflowStateRepository(cs, time);
            await new MemberRegistryContract(store, (_, by) => { time.Advance(by); return Task.CompletedTask; },
                () => Task.FromResult(time.GetUtcNow().UtcDateTime), sqlite: true,
                id => AssertRemoved(() => InspectSqlite(cs), id)).VerifyAsync(scenario);
        }
        finally { await SqliteTestFiles.DeleteAsync(folder); }
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task SqlServerRegistryContract()
    {
        var cs = SqlTestDatabase<MemberRegistryTests>.ConnectionString;
        foreach (var scenario in Enum.GetValues<MemberRegistryScenario>())
            await DatabaseContract(new SqlWorkflowStateRepository(cs), () => new SqlConnection(cs)).VerifyAsync(scenario);
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSqlRegistryContract()
    {
        var cs = PostgreSqlTestDatabase<MemberRegistryTests>.ConnectionString;
        foreach (var scenario in Enum.GetValues<MemberRegistryScenario>())
            await DatabaseContract(new PostgreSqlWorkflowStateRepository(cs), () => new NpgsqlConnection(cs)).VerifyAsync(scenario);
    }

    private static MemberRegistryContract DatabaseContract(IWorkflowStateRepository store, Func<DbConnection> open) =>
        new(store, async (id, by) =>
        {
            await using var connection = open();
            await connection.ExecuteAsync(connection is SqlConnection
                ? "UPDATE dbo.IxIFlowHosts SET HeartbeatAtUtc=DATEADD(SECOND,-@Seconds,HeartbeatAtUtc) WHERE MemberId=@Id"
                : "UPDATE ixiflow_hosts SET heartbeat_at_utc=heartbeat_at_utc-@Seconds * INTERVAL '1 second' WHERE member_id=@Id",
                new { Id = id, Seconds = (int)by.TotalSeconds });
        }, async () =>
        {
            await using var connection = open();
            var value = await connection.ExecuteScalarAsync<DateTime>(connection is SqlConnection
                ? "SELECT SYSUTCDATETIME()" : "SELECT clock_timestamp()");
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }, assertRowsRemoved: id => AssertRemoved(open, id));

    private static async Task AssertRemoved(Func<DbConnection> open, Guid id)
    {
        await using var connection = open();
        var (hosts, capabilities, memberId) = connection is SqlConnection
            ? ("dbo.IxIFlowHosts", "dbo.IxIFlowHostCapabilities", "MemberId")
            : ("ixiflow_hosts", "ixiflow_host_capabilities", "member_id");
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM {hosts} WHERE {memberId}=@Id", new { Id = id }));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
            $"SELECT COUNT(*) FROM {capabilities} WHERE {memberId}=@Id", new { Id = id }));
    }

    private static SqliteConnection InspectSqlite(string connectionString) =>
        new(new SqliteConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactLivenessAndPruningBoundariesUseTheStoreClock(bool sqlite)
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.Registry", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var time = new FakeTimeProvider();
        var store = sqlite ? (IWorkflowStateRepository)new SqliteWorkflowStateRepository(
            "Data Source=" + Path.Combine(folder, "registry.db"), time) : new InMemoryWorkflowStateRepository(time);
        try
        {
            var member = MemberRegistryContract.Registration();
            await store.RegisterMemberAsync(member);
            time.Advance(TimeSpan.FromSeconds(15) - TimeSpan.FromTicks(1));
            Assert.Single(await store.GetLiveMembersAsync());
            time.Advance(TimeSpan.FromTicks(1));
            Assert.Empty(await store.GetLiveMembersAsync());
            time.Advance(TimeSpan.FromHours(1) - TimeSpan.FromSeconds(15));
            Assert.True(await store.HeartbeatAsync(member.Id, member.Capabilities)); // Exactly one hour is retained.
            time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromTicks(1));
            Assert.False(await store.HeartbeatAsync(member.Id, member.Capabilities));
        }
        finally
        {
            if (store is IAsyncDisposable disposable) await disposable.DisposeAsync();
            await SqliteTestFiles.DeleteAsync(folder);
        }
    }

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    public async Task SqliteRestartReplacesThePreviousProcessesAdvertisement()
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.Registry", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var cs = "Data Source=" + Path.Combine(folder, "registry.db");
        try
        {
            var old = MemberRegistryContract.Registration();
            await using (var crashed = new SqliteWorkflowStateRepository(cs))
                await crashed.RegisterMemberAsync(old);
            await using var restarted = new SqliteWorkflowStateRepository(cs);
            var current = MemberRegistryContract.Registration();
            await restarted.RegisterMemberAsync(current);
            Assert.Equal(current.Id, Assert.Single(await restarted.GetLiveMembersAsync()).Id);
            await AssertRemoved(() => InspectSqlite(cs), old.Id);
        }
        finally { await SqliteTestFiles.DeleteAsync(folder); }
    }

    [Fact]
    public async Task RegistryStalenessDoesNotAffectExecutionLeases()
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryWorkflowStateRepository(time);
        var member = MemberRegistryContract.Registration();
        await store.RegisterMemberAsync(member);
        Assert.NotNull(await store.TryAcquireExecutionLeaseAsync("work", "owner", TimeSpan.FromMinutes(2)));
        time.Advance(TimeSpan.FromSeconds(16));
        Assert.Empty(await store.GetLiveMembersAsync());
        Assert.Null(await store.TryAcquireExecutionLeaseAsync("work", "other", TimeSpan.FromMinutes(2)));
        await store.UnregisterMemberAsync(member.Id);
        Assert.True(await store.RenewExecutionLeaseAsync("work", "owner", TimeSpan.FromMinutes(2)));
    }
}
