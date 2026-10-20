using System.Data.Common;
using System.Reflection;
using System.Text.Json;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "registration")]
[Trait("Spec", "F-10")]
[Trait("Spec", "F-11")]
[Trait("Spec", "F-15")]
public sealed class CatalogArtifactThirdRoundTests
{
    public enum Scenario { NewerVersion, UnfinishedParent, PublishedCaller, DeprecatedCaller }
    public static TheoryData<Scenario> Scenarios => new(Enum.GetValues<Scenario>());

    [Theory, MemberData(nameof(Scenarios))]
    public Task Memory(Scenario scenario) => VerifyAsync(new InMemoryWorkflowStateRepository(), scenario);

    [Theory, MemberData(nameof(Scenarios)), Trait("Category", "SqliteIntegration")]
    public async Task Sqlite(Scenario scenario)
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.PR67R3", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var cs = "Data Source=" + Path.Combine(folder, "catalog.db");
        try
        {
            await using var store = new SqliteWorkflowStateRepository(cs);
            await VerifyAsync(store, scenario, () => new SqliteConnection(cs + ";Pooling=False"));
        }
        finally { await SqliteTestFiles.DeleteAsync(folder); }
    }

    [Theory, MemberData(nameof(Scenarios)), Trait("Category", "SqlIntegration")]
    public Task SqlServer(Scenario scenario)
    {
        var cs = SqlTestDatabase<CatalogArtifactThirdRoundTests>.ConnectionString;
        return VerifyAsync(new SqlWorkflowStateRepository(cs), scenario, () => new SqlConnection(cs));
    }

    [Theory, MemberData(nameof(Scenarios)), Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSql(Scenario scenario)
    {
        var cs = PostgreSqlTestDatabase<CatalogArtifactThirdRoundTests>.ConnectionString;
        return VerifyAsync(new PostgreSqlWorkflowStateRepository(cs), scenario, () => new NpgsqlConnection(cs));
    }

    private static async Task VerifyAsync(IWorkflowStateRepository store, Scenario scenario, Func<DbConnection>? connect = null)
    {
        var child = "R3-" + Guid.NewGuid().ToString("N");
        var caller = child + "-caller";
        const int oldVersion = 3;
        const int newVersion = 8;
        await store.RegisterWorkflowVersionAsync(new(child, 1, "child"));
        await store.RegisterWorkflowVersionAsync(new(caller, oldVersion, "old", Dependencies: [new(child, 1)]));
        await store.RegisterWorkflowVersionAsync(new(caller, newVersion, "new"));

        if (scenario is Scenario.PublishedCaller or Scenario.DeprecatedCaller)
        {
            if (scenario == Scenario.DeprecatedCaller)
                await store.SetWorkflowVersionStateAsync(caller, oldVersion, WorkflowVersionState.Deprecated);
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() =>
                store.SetWorkflowVersionStateAsync(child, 1, WorkflowVersionState.Retired));
            Assert.Equal(WorkflowErrorKind.InvalidState, error.Kind);
            Assert.Contains($"is required by caller {caller} v{oldVersion}", error.Message);
            Assert.DoesNotContain("unfinished parent", error.Message);
            Assert.Equal(WorkflowVersionState.Published, (await store.GetWorkflowVersionAsync(child, 1))!.State);
            return;
        }

        var instances = new List<WorkflowInstance>();
        // These unfinished instances share the caller's name, but their version has no dependency.
        for (var i = 0; i < 256; i++)
            await SeedAsync(caller, newVersion);

        // Also reject a false cross product of names and versions: (caller,3), (other,8)
        // must not match either (caller,8) above or (other,3) below.
        var other = child + "-other";
        await store.RegisterWorkflowVersionAsync(new(other, oldVersion, "other-old"));
        await store.RegisterWorkflowVersionAsync(new(other, newVersion, "other-new",
            WorkflowVersionState.Retired, Dependencies: [new(child, 1)]));
        await SeedAsync(other, oldVersion);
        var pairs = new WorkflowVersionReference[] { new(caller, oldVersion), new(other, newVersion) };
        await AssertQueryAsync(false);

        if (scenario == Scenario.UnfinishedParent)
            for (var i = 0; i < 16; i++) await SeedAsync(caller, oldVersion);
        await store.SetWorkflowVersionStateAsync(caller, oldVersion, WorkflowVersionState.Retired);
        await AssertQueryAsync(scenario == Scenario.UnfinishedParent);

        if (scenario == Scenario.UnfinishedParent)
        {
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() =>
                store.SetWorkflowVersionStateAsync(child, 1, WorkflowVersionState.Retired));
            Assert.Equal(WorkflowErrorKind.InvalidState, error.Kind);
            Assert.Contains($"has unfinished parent instances of {caller} v{oldVersion}", error.Message);
            Assert.DoesNotContain("is required by caller", error.Message);
            Assert.Equal(WorkflowVersionState.Published, (await store.GetWorkflowVersionAsync(child, 1))!.State);
            foreach (var instance in instances.Where(instance => instance.WorkflowName == caller && instance.WorkflowVersion == oldVersion))
            {
                instance.Status = WorkflowStatus.Completed;
                await store.SeedAsync(instance);
            }
            await AssertQueryAsync(false);
        }
        await store.SetWorkflowVersionStateAsync(child, 1, WorkflowVersionState.Retired);
        Assert.Equal(WorkflowVersionState.Retired, (await store.GetWorkflowVersionAsync(child, 1))!.State);

        async Task SeedAsync(string name, int version)
        {
            var instance = Instance(name, version);
            await store.SeedAsync(instance, wakeAfter: TimeSpan.Zero);
            instances.Add(instance);
        }

        async Task AssertQueryAsync(bool found)
        {
            if (connect == null)
            {
                var match = InMemoryWorkflowStateRepository.FindUnfinishedCaller(instances, pairs.ToHashSet());
                Assert.Equal(found ? new(caller, oldVersion) : null, match);
                return;
            }
            // Execute the production statement directly, so a query returning every newer-version
            // row cannot pass merely because retirement later filters its buffered results in C#.
            var sqlMethod = store.GetType().GetMethod("CatalogSql", BindingFlags.Static | BindingFlags.NonPublic)!;
            var statement = Enum.Parse(sqlMethod.GetParameters()[0].ParameterType, "CatalogUnfinishedCallers");
            var sql = (string)sqlMethod.Invoke(null, [statement])!;
            await using var connection = connect();
            var rows = (await connection.QueryAsync<CallerRow>(sql, new { Callers = JsonSerializer.Serialize(pairs) })).ToArray();
            Assert.InRange(rows.Length, 0, 1);
            if (found)
            {
                var row = Assert.Single(rows);
                Assert.Equal(caller, row.Name);
                Assert.Equal(oldVersion, row.Version);
            }
            else Assert.Empty(rows);
        }
    }

    [Fact]
    public void MemoryStopsEnumeratingAtFirstMatchingInstance()
    {
        var pair = new WorkflowVersionReference("depending-caller", 3);
        var read = 0;
        IEnumerable<WorkflowInstance> Rows()
        {
            read++;
            yield return Instance(pair.Name, pair.Version);
            throw new InvalidOperationException("The memory store read beyond its first match");
        }
        Assert.Equal(pair, InMemoryWorkflowStateRepository.FindUnfinishedCaller(Rows(), new HashSet<WorkflowVersionReference> { pair }));
        Assert.Equal(1, read);
    }

    private sealed class CallerRow
    {
        public string Name { get; set; } = "";
        public int Version { get; set; }
    }

    private static WorkflowInstance Instance(string name, int version) => new()
    {
        InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = name, WorkflowVersion = version,
        Status = WorkflowStatus.Suspended, ExecutionStateJson = StoreTestSupport.CheckpointJson,
        WorkflowDataJson = StoredJson.Inline("{}"), WorkflowDataType = typeof(object).AssemblyQualifiedName!
    };
}
