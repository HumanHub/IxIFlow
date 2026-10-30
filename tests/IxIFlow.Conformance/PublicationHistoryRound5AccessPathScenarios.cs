using System.Reflection;
using System.Xml.Linq;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Relational;
using Xunit.Abstractions;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "registration")]
public sealed class PublicationHistoryRound5AccessPathScenarios(ITestOutputHelper output)
{
    [Theory]
    [InlineData(StoreProfiles.DistributedSqlServer)]
    public async Task PublicationSmallPageDeepContinuationAndTimestampTiesUseTheHistoryIndexWithoutSort(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await store.GetCatalogRevisionAsync();
        await using var connection = database.NewConnection();
        await connection.OpenAsync();

        await connection.ExecuteAsync("""
                                      WITH digits(n) AS (SELECT n FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) d(n)),
                                      numbers(n) AS (SELECT TOP (8192) ROW_NUMBER() OVER (ORDER BY a.n,b.n,c.n,d.n)-1
                                                    FROM digits a CROSS JOIN digits b CROSS JOIN digits c CROSS JOIN digits d)
                                      INSERT INTO dbo.IxIFlowPublicationAttempts
                                          (Id, Kind, Name, Version, Generation, Revision, Attempt, State, HostId,
                                           StartedAtUtc, HeartbeatAtUtc, FinishedAtUtc, CatalogRevision)
                                      SELECT CONVERT(UNIQUEIDENTIFIER, RIGHT('00000000'+CONVERT(VARCHAR(8),n.n),8)+'-0123-4567-89ab-'+RIGHT('000000000000'+CONVERT(VARCHAR(12),8191-n.n),12)),
                                             'workflow', N'History', 1, '01234567-89ab-cdef-0123-456789abcdef', 1, n.n+1, 'Failed', NEWID(),
                                             DATEADD(SECOND,n.n/4096,CONVERT(DATETIME2,'2026-10-20T12:00:00',126)),
                                             CONVERT(DATETIME2,'2026-10-20T12:00:02',126), CONVERT(DATETIME2,'2026-10-20T12:00:02',126), 0
                                      FROM numbers n;
                                      UPDATE STATISTICS dbo.IxIFlowPublicationAttempts WITH FULLSCAN;
                                      """);

        var query = new WorkflowPublicationQuery(CatalogScope.Unrestricted, "workflow", "History", 1, Limit: 1);
        var first = await store.GetPublicationsAsync(query);
        Assert.Equal(Id(0), Assert.Single(first.Items).Id);
        var second = await store.GetPublicationsAsync(query with { After = first.NextCursor });
        Assert.Equal(Id(1), Assert.Single(second.Items).Id);
        var deep = await store.GetPublicationsAsync(query with { Limit = 1000 });

        for (var page = 1; page < 4; page++)
            deep = await store.GetPublicationsAsync(query with { Limit = 1000, After = deep.NextCursor });

        Assert.Equal(Id(3999), deep.Items[^1].Id);
        var continued = await store.GetPublicationsAsync(query with { After = deep.NextCursor });
        Assert.Equal(Id(4000), Assert.Single(continued.Items).Id);
        var tieEnd = await store.GetPublicationsAsync(query with { After = deep.NextCursor, Limit = 96 });
        Assert.Equal(Id(4095), tieEnd.Items[^1].Id);
        var across = await store.GetPublicationsAsync(query with { After = tieEnd.NextCursor });
        Assert.Equal(Id(4096), Assert.Single(across.Items).Id);

        foreach (var after in new WorkflowPublication?[] { null, first.Items[0], deep.Items[^1], tieEnd.Items[^1] })
        {
            // Invoke the production binder, including datetime2, varchar(10) and varchar(36).
            var parameters = (DynamicParameters)typeof(RelationalWorkflowStateRepository)
                .GetMethod("PublicationPageParameters", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(store, [query, after?.StartedAtUtc, after?.Id])!;

            var plan = await ActualPlanAsync(connection, Sql(store, "PublicationPage"), parameters);
            var seek = OrderedSeekWithoutSort(plan, "ix_publication_history");

            if (after != null)
                Assert.Contains(seek.Descendants(Ns + "SeekPredicates").Descendants(Ns + "ColumnReference"),
                    column => (string?)column.Attribute("Column") == "StartedAtUtc");
        }

        static Guid Id(int index) => Guid.Parse($"{index:00000000}-0123-4567-89ab-{8191 - index:000000000000}");
    }

    [Theory]
    [InlineData(StoreProfiles.DistributedSqlServer)]
    public async Task DraftContinuationBindsTheExactPersistedKeyTypesAndSeeksNameOrder(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await store.GetCatalogRevisionAsync();
        await using var connection = database.NewConnection();
        await connection.OpenAsync();

        await connection.ExecuteAsync("""
                                      WITH digits(n) AS (SELECT n FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) d(n)),
                                      numbers(n) AS (SELECT TOP (2048) ROW_NUMBER() OVER (ORDER BY a.n,b.n,c.n,d.n)-1
                                                    FROM digits a CROSS JOIN digits b CROSS JOIN digits c CROSS JOIN digits d)
                                      INSERT INTO dbo.IxIFlowDefinitions (Kind,Name,Version,Generation,Revision,Source,SourceLength,UpdatedAtUtc)
                                      SELECT 'workflow', N'Draft'+RIGHT('00000'+CONVERT(VARCHAR(5),n.n),5), v.Version, NEWID(), 1, N'', 0, SYSUTCDATETIME()
                                      FROM numbers n CROSS JOIN (VALUES(1),(2)) v(Version);
                                      UPDATE STATISTICS dbo.IxIFlowDefinitions WITH FULLSCAN;
                                      """);

        var query = new WorkflowDraftQuery(CatalogScope.Unrestricted, Kind: "workflow", Limit: 1);

        var parameters = (DynamicParameters)typeof(RelationalWorkflowStateRepository)
            .GetMethod("DraftPageParameters", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [query, new[] { "workflow", "Draft01500", "1" }, 1])!;

        var scope = (string)typeof(RelationalWorkflowStateRepository).GetMethod("ScopePredicate",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [CatalogScope.Unrestricted, parameters])!;

        var plan = await ActualPlanAsync(connection, Sql(store, "CatalogDefinitionPage").Replace("{scope}", scope), parameters);
        var seek = OrderedSeekWithoutSort(plan, "ix_definitions_page");

        Assert.Contains(seek.Descendants(Ns + "SeekPredicates").Descendants(Ns + "ColumnReference"),
            column => (string?)column.Attribute("Column") == "NameOrder");

        // A conversion of the indexed column would reintroduce a dynamic expression/range seek.
        Assert.DoesNotContain(seek.Descendants(Ns + "SeekPredicates").Descendants(Ns + "Convert"),
            convert => convert.Descendants(Ns + "ColumnReference").Any(column =>
                (string?)column.Attribute("Column") is "Kind" or "NameOrder"));
    }

    private static readonly XNamespace Ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    private static string Sql(IWorkflowStateRepository store, string name)
    {
        var statement = typeof(RelationalWorkflowStateRepository).GetNestedType("Statement", BindingFlags.NonPublic)!;

        return (string)typeof(RelationalWorkflowStateRepository).GetMethod("Sql", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, [Enum.Parse(statement, name)])!;
    }

    private async Task<XDocument> ActualPlanAsync(System.Data.Common.DbConnection connection, string sql, DynamicParameters parameters)
    {
        await connection.ExecuteAsync("SET STATISTICS XML ON");

        try
        {
            using var results = await connection.QueryMultipleAsync(sql, parameters);
            Assert.Equal(2, (await results.ReadAsync()).Count());
            var xml = await results.ReadSingleAsync<string>();
            output.WriteLine(xml);

            return XDocument.Parse(xml);
        }
        finally
        {
            await connection.ExecuteAsync("SET STATISTICS XML OFF");
        }
    }

    private static XElement OrderedSeekWithoutSort(XDocument plan, string index)
    {
        var operators = plan.Descendants(Ns + "RelOp").ToArray();
        Assert.DoesNotContain(operators, op => ((string?)op.Attribute("PhysicalOp"))?.Contains("Sort", StringComparison.Ordinal) == true);

        var seek = Assert.Single(operators, op => (string?)op.Attribute("PhysicalOp") == "Index Seek"
            && (string?)op.Element(Ns + "IndexScan")?.Element(Ns + "Object")?.Attribute("Index") == "[" + index + "]");

        Assert.True((bool?)seek.Element(Ns + "IndexScan")?.Attribute("Ordered"));

        return seek;
    }
}