using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace IxIFlow.Tests.ExecutionTests;

public enum InstalledPackageScenario { Registration, CoexistingFingerprints, Replacement, Liveness, Unregister, PruneAndReregister, FailedRegistration, DuplicateSelections, NullVersion }

[Trait("Feature", "host")]
[Trait("Spec", "F-21")]
public sealed class InstalledActivityPackageTests
{
    public static TheoryData<InstalledPackageScenario> Scenarios => new(Enum.GetValues<InstalledPackageScenario>());

    [Theory]
    [MemberData(nameof(Scenarios))]
    public Task MemoryCatalogContract(InstalledPackageScenario scenario)
    {
        var time = new FakeTimeProvider();
        return Verify(new InMemoryWorkflowStateRepository(time), scenario, (_, by) =>
        { time.Advance(by); return Task.CompletedTask; });
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    [Trait("Category", "SqliteIntegration")]
    public async Task SqliteCatalogContract(InstalledPackageScenario scenario)
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.Installed", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var cs = "Data Source=" + Path.Combine(folder, "installed.db");
        try
        {
            var time = new FakeTimeProvider();
            if (scenario == InstalledPackageScenario.CoexistingFingerprints)
            {
                await VerifySqliteCoexistingFingerprints(cs, time);
                return;
            }
            await using var store = new SqliteWorkflowStateRepository(cs, time);
            await Verify(store, scenario, (_, by) => { time.Advance(by); return Task.CompletedTask; },
                () => new SqliteConnection(new SqliteConnectionStringBuilder(cs) { Pooling = false }.ConnectionString));
        }
        finally { await SqliteTestFiles.DeleteAsync(folder); }
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task SqlServerCatalogContract()
    {
        var cs = SqlTestDatabase<InstalledActivityPackageTests>.ConnectionString;
        foreach (var scenario in Enum.GetValues<InstalledPackageScenario>())
            await DatabaseContract(new SqlWorkflowStateRepository(cs), new SqlWorkflowStateRepository(cs), scenario, () => new SqlConnection(cs));
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSqlCatalogContract()
    {
        var cs = PostgreSqlTestDatabase<InstalledActivityPackageTests>.ConnectionString;
        foreach (var scenario in Enum.GetValues<InstalledPackageScenario>())
            await DatabaseContract(new PostgreSqlWorkflowStateRepository(cs), new PostgreSqlWorkflowStateRepository(cs), scenario, () => new NpgsqlConnection(cs));
    }

    private static Task DatabaseContract(IWorkflowStateRepository store, IWorkflowStateRepository competing, InstalledPackageScenario scenario,
        Func<DbConnection> open) => Verify(store, scenario, async (id, by) =>
    {
        await using var connection = open();
        await connection.ExecuteAsync(connection is SqlConnection
            ? "UPDATE dbo.IxIFlowHosts SET HeartbeatAtUtc=DATEADD(SECOND,-@Seconds,HeartbeatAtUtc) WHERE MemberId=@Id"
            : "UPDATE ixiflow_hosts SET heartbeat_at_utc=heartbeat_at_utc-@Seconds * INTERVAL '1 second' WHERE member_id=@Id",
            new { Id = id, Seconds = (int)by.TotalSeconds });
    }, open, competing);

    private static async Task Verify(IWorkflowStateRepository store, InstalledPackageScenario scenario,
        Func<Guid, TimeSpan, Task> age, Func<DbConnection>? open = null,
        IWorkflowStateRepository? competing = null)
    {
        var id = "installed." + Guid.NewGuid().ToString("N");
        var first = Package(id, "[{\"Key\":\"first\"}]");
        var second = Package(id, "[{\"Key\":\"second\"}]");
        var member = MemberRegistryContract.Registration() with { InstalledPackages = [first] };
        if (scenario == InstalledPackageScenario.CoexistingFingerprints)
        {
            await VerifyCoexistingFingerprints(store, competing ?? store, member, second);
            return;
        }
        async Task<InstalledActivityPackage> Read(string fingerprint) => Assert.Single(
            await store.GetInstalledPackagesAsync(id.ToUpperInvariant()), row => row.Fingerprint == fingerprint);
        try
        {
            await store.RegisterMemberAsync(member);
            Assert.Equal(member.Id, Assert.Single((await Read(first.Fingerprint)).Hosts));
            switch (scenario)
            {
                case InstalledPackageScenario.Registration:
                    var saved = await Read(first.Fingerprint);
                    Assert.Equal(first.ActivitiesJson, saved.ActivitiesJson);
                    Assert.Equal(first.Version, saved.Version);
                    Assert.Equal(member.Capabilities.OrderBy(pair => pair.Key),
                        Assert.Single(await store.GetLiveMembersAsync(), row => row.Id == member.Id).Capabilities.OrderBy(pair => pair.Key));
                    Assert.Contains(await store.GetInstalledPackagesAsync(), row => row.Id == id);
                    Assert.Empty(await store.GetInstalledPackagesAsync("absent"));
                    await store.RegisterMemberAsync(member);
                    Assert.Single(await store.GetInstalledPackagesAsync(id));
                    break;
                case InstalledPackageScenario.Replacement:
                    await store.RegisterMemberAsync(member with { InstalledPackages = [second] });
                    Assert.Empty((await Read(first.Fingerprint)).Hosts);
                    Assert.Equal(member.Id, Assert.Single((await Read(second.Fingerprint)).Hosts));
                    await store.RegisterMemberAsync(member with { InstalledPackages = [] });
                    Assert.All(await store.GetInstalledPackagesAsync(id), row => Assert.Empty(row.Hosts));
                    break;
                case InstalledPackageScenario.Liveness:
                    await age(member.Id, TimeSpan.FromSeconds(16));
                    Assert.Empty((await Read(first.Fingerprint)).Hosts);
                    Assert.True(await store.HeartbeatAsync(member.Id, member.Capabilities));
                    Assert.Equal(member.Id, Assert.Single((await Read(first.Fingerprint)).Hosts));
                    break;
                case InstalledPackageScenario.Unregister:
                    await store.UnregisterMemberAsync(member.Id);
                    Assert.Empty((await Read(first.Fingerprint)).Hosts);
                    if (open != null) await AssertMarksRemoved(open, member.Id);
                    break;
                case InstalledPackageScenario.PruneAndReregister:
                    await age(member.Id, TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
                    Assert.Empty((await Read(first.Fingerprint)).Hosts);
                    Assert.False(await store.HeartbeatAsync(member.Id, member.Capabilities));
                    if (open != null) await AssertMarksRemoved(open, member.Id);
                    await store.RegisterMemberAsync(member);
                    Assert.Equal(member.Id, Assert.Single((await Read(first.Fingerprint)).Hosts));
                    break;
                case InstalledPackageScenario.FailedRegistration:
                    await Assert.ThrowsAnyAsync<Exception>(() => store.RegisterMemberAsync(member with
                    { InstalledPackages = [second, first with { Fingerprint = "invalid" }] }));
                    Assert.Equal(member.Id, Assert.Single((await Read(first.Fingerprint)).Hosts));
                    Assert.Single(await store.GetInstalledPackagesAsync(id));
                    using (var cancelled = new CancellationTokenSource())
                    {
                        cancelled.Cancel();
                        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.RegisterMemberAsync(
                            member with { InstalledPackages = [second] }, cancelled.Token));
                    }
                    Assert.Single(await store.GetInstalledPackagesAsync(id));
                    break;
                case InstalledPackageScenario.DuplicateSelections:
                case InstalledPackageScenario.NullVersion:
                    var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => store.RegisterMemberAsync(member with
                    {
                        Name = "must not be saved",
                        Capabilities = new Dictionary<string, string> { ["changed"] = "must not be saved" },
                        InstalledPackages = scenario == InstalledPackageScenario.DuplicateSelections
                            ? [second, second] : [second, first with { Version = null! }]
                    }));
                    Assert.Equal(WorkflowErrorKind.InvalidRequest, error.Kind);
                    if (scenario == InstalledPackageScenario.DuplicateSelections)
                        Assert.Contains("repeats fingerprint", error.Message);
                    Assert.Single(await store.GetInstalledPackagesAsync(id));
                    Assert.Equal(member.Id, Assert.Single((await Read(first.Fingerprint)).Hosts));
                    var unchanged = Assert.Single(await store.GetLiveMembersAsync(), row => row.Id == member.Id);
                    Assert.Equal(member.Name, unchanged.Name);
                    Assert.Equal(member.Capabilities.OrderBy(pair => pair.Key), unchanged.Capabilities.OrderBy(pair => pair.Key));
                    break;
            }
        }
        finally { await store.UnregisterMemberAsync(member.Id); }
    }

    private static async Task VerifyCoexistingFingerprints(IWorkflowStateRepository store, IWorkflowStateRepository competing,
        MemberRegistration firstMember, InstalledActivityPackage second)
    {
        var first = Assert.Single(firstMember.InstalledPackages);
        var secondMember = MemberRegistryContract.Registration() with { InstalledPackages = [second] };
        // A third member registers the first member's exact package at the same time, so two writers race to insert one content row.
        var thirdMember = MemberRegistryContract.Registration() with { InstalledPackages = [first] };
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Register(IWorkflowStateRepository repository, MemberRegistration member)
        {
            await start.Task;
            await repository.RegisterMemberAsync(member);
        }
        var one = Register(store, firstMember);
        var two = Register(competing, secondMember);
        var three = Register(competing, thirdMember);
        start.SetResult();
        try
        {
            await Task.WhenAll(one, two, three).WaitAsync(TimeSpan.FromSeconds(10));
            var packages = await store.GetInstalledPackagesAsync(first.Id);
            Assert.Equal(2, packages.Count);
            var live = (await store.GetLiveMembersAsync()).Select(member => member.Id).ToHashSet();
            Assert.Contains(firstMember.Id, live);
            Assert.Contains(secondMember.Id, live);
            Assert.Contains(thirdMember.Id, live);
            foreach (var (package, members) in new[] { (first, new[] { firstMember.Id, thirdMember.Id }), (second, new[] { secondMember.Id }) })
            {
                var saved = Assert.Single(packages, row => row.Fingerprint == package.Fingerprint);
                Assert.Equal(package.ActivitiesJson, saved.ActivitiesJson);
                Assert.Equal(package.Version, saved.Version);
                Assert.Equal(members.Order(), saved.Hosts.Order());
            }
        }
        finally
        {
            await store.UnregisterMemberAsync(firstMember.Id);
            await competing.UnregisterMemberAsync(secondMember.Id);
            await competing.UnregisterMemberAsync(thirdMember.Id);
        }
    }

    private static async Task VerifySqliteCoexistingFingerprints(string connectionString, TimeProvider time)
    {
        var id = "installed." + Guid.NewGuid().ToString("N");
        var first = Package(id, "[{\"Key\":\"first\"}]");
        var second = Package(id, "[{\"Key\":\"second\"}]");
        var firstMember = MemberRegistryContract.Registration() with { InstalledPackages = [first] };
        var secondMember = MemberRegistryContract.Registration() with { InstalledPackages = [second] };
        // SQLite takes exclusive file ownership for the repository's lifetime, so owners replace each other.
        await using (var store = new SqliteWorkflowStateRepository(connectionString, time))
        {
            await store.RegisterMemberAsync(firstMember);
            Assert.Equal(new[] { firstMember.Id }, Assert.Single(await store.GetInstalledPackagesAsync(id)).Hosts);
        }
        await using var replacement = new SqliteWorkflowStateRepository(connectionString, time);
        await replacement.RegisterMemberAsync(secondMember);
        var packages = await replacement.GetInstalledPackagesAsync(id);
        Assert.Equal(2, packages.Count);
        var old = Assert.Single(packages, package => package.Fingerprint == first.Fingerprint);
        var current = Assert.Single(packages, package => package.Fingerprint == second.Fingerprint);
        Assert.Equal(first.ActivitiesJson, old.ActivitiesJson);
        Assert.Equal(second.ActivitiesJson, current.ActivitiesJson);
        Assert.Empty(old.Hosts);
        Assert.Equal(new[] { secondMember.Id }, current.Hosts);
        await replacement.UnregisterMemberAsync(secondMember.Id);
    }

    internal static InstalledActivityPackage Package(string id, string json) => new(id, "1.0.0",
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))), json);

    private static async Task AssertMarksRemoved(Func<DbConnection> open, Guid id)
    {
        await using var connection = open();
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>(connection is SqlConnection
            ? "SELECT COUNT(*) FROM dbo.IxIFlowHostPackages WHERE MemberId=@Id"
            : "SELECT COUNT(*) FROM ixiflow_host_packages WHERE member_id=@Id", new { Id = id }));
    }

    [Fact]
    public async Task SqliteProcessReplacementRemovesPreviousHostMarks()
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.Installed", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var cs = "Data Source=" + Path.Combine(folder, "installed.db");
        var package = Package("installed.replacement", "[]");
        var prior = MemberRegistryContract.Registration() with { InstalledPackages = [package] };
        try
        {
            await using (var first = new SqliteWorkflowStateRepository(cs)) await first.RegisterMemberAsync(prior);
            await using var next = new SqliteWorkflowStateRepository(cs);
            await next.RegisterMemberAsync(MemberRegistryContract.Registration());
            Assert.Empty(Assert.Single(await next.GetInstalledPackagesAsync(package.Id)).Hosts);
            await AssertMarksRemoved(() => new SqliteConnection(new SqliteConnectionStringBuilder(cs) { Pooling = false }.ConnectionString), prior.Id);
        }
        finally { await SqliteTestFiles.DeleteAsync(folder); }
    }
}
