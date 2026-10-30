using System.Reflection;
using System.Text;
using System.Xml.Linq;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Relational;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "registration")]
[Trait("Spec", "F-09")]
[Trait("Spec", "F-14")]
public sealed class PublicationDraftSqlServerAccessPathScenarios
{
    [Theory]
    [InlineData(StoreProfiles.DistributedSqlServer)]
    public async Task DraftLimitOnePageAndContinuationSeekCoveringCodePointIndexWithoutSort(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await store.GetCatalogRevisionAsync();
        await using var connection = database.NewConnection();
        await connection.OpenAsync();

        // Enough unrelated kinds, names, version ties and source bytes to expose a scan/sort or lookup.
        await connection.ExecuteAsync("""
                                      WITH digits(n) AS (SELECT n FROM (VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9)) d(n)),
                                      numbers(n) AS (SELECT TOP (2048) ROW_NUMBER() OVER (ORDER BY a.n,b.n,c.n,d.n)-1
                                                    FROM digits a CROSS JOIN digits b CROSS JOIN digits c CROSS JOIN digits d)
                                      INSERT INTO dbo.IxIFlowDefinitions (Kind, Name, Version, Generation, Revision, Source, SourceLength, UpdatedAtUtc, UpdatedBy)
                                      SELECT k.Kind, N'Draft'+RIGHT('00000'+CONVERT(VARCHAR(5),n.n),5), v.Version, NEWID(), 1,
                                             REPLICATE(N'x',2048), 2048, SYSUTCDATETIME(), N'plan-test'
                                      FROM numbers n CROSS JOIN (VALUES('activity'),('template'),('workflow')) k(Kind)
                                          CROSS JOIN (VALUES(1),(2)) v(Version);
                                      UPDATE STATISTICS dbo.IxIFlowDefinitions WITH FULLSCAN;
                                      """);

        var query = new WorkflowDraftQuery(CatalogScope.Unrestricted, Kind: "workflow", Limit: 1);
        var first = await store.GetDefinitionsAsync(query);
        Assert.Equal(("Draft00000", 1), (Assert.Single(first.Items).Name, first.Items[0].Version));
        Assert.NotNull(first.NextCursor);
        var second = await store.GetDefinitionsAsync(query with { After = first.NextCursor });
        Assert.Equal(("Draft00000", 2), (Assert.Single(second.Items).Name, second.Items[0].Version));

        // A deep continuation also proves that the lower bound is a seek on NameOrder, not just Kind.
        var earlier = await store.GetDefinitionsAsync(query with { Limit = 1000 });
        Assert.NotNull(earlier.NextCursor);
        var continued = await store.GetDefinitionsAsync(query with { After = earlier.NextCursor });
        Assert.Equal(("Draft00500", 1), (Assert.Single(continued.Items).Name, continued.Items[0].Version));

        var repository = (RelationalWorkflowStateRepository)store;
        var statement = typeof(RelationalWorkflowStateRepository).GetNestedType("Statement", BindingFlags.NonPublic)!;

        var sqlTemplate =
            (string)typeof(RelationalWorkflowStateRepository).GetMethod("Sql", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(repository, [Enum.Parse(statement, "CatalogDefinitionPage")])!;

        foreach (var after in new WorkflowDraftSummary?[] { null, first.Items[0], earlier.Items[^1] })
        {
            // Use the production statement, scope builder and parameter shapes (Limit:1 reads two rows).
            var parameters = new DynamicParameters(new
            {
                query.Kind, query.NamePrefix, PrefixLength = (int?)null, Limit = query.Limit + 1,
                AfterKind = after?.Kind, AfterName = after?.Name, AfterVersion = after?.Version,
                AfterNameOrder = after == null ? null : Encoding.UTF8.GetBytes(after.Name)
            });

            var scope = (string)typeof(RelationalWorkflowStateRepository)
                .GetMethod("ScopePredicate", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(repository, [query.Scope, parameters])!;

            var sql = sqlTemplate.Replace("{scope}", scope);

            // Parameterized SqlClient RPCs do not reliably expose an estimated SHOWPLAN row.
            // Execute the production query and read its actual plan after the result rows.
            await connection.ExecuteAsync("SET STATISTICS XML ON");
            string plan;

            try
            {
                using var results = await connection.QueryMultipleAsync(sql, parameters);
                Assert.Equal(2, (await results.ReadAsync()).Count());
                plan = await results.ReadSingleAsync<string>();
            }
            finally
            {
                await connection.ExecuteAsync("SET STATISTICS XML OFF");
            }

            var document = XDocument.Parse(plan);
            XNamespace ns = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
            var operators = document.Descendants(ns + "RelOp").ToArray();

            Assert.DoesNotContain(operators,
                op => ((string?)op.Attribute("PhysicalOp"))?.Contains("Sort", StringComparison.Ordinal) == true);

            Assert.DoesNotContain(operators, op => (string?)op.Attribute("LogicalOp") == "Key Lookup");

            var seek = Assert.Single(operators, op => (string?)op.Attribute("PhysicalOp") == "Index Seek"
                && op.Element(ns + "IndexScan")?.Element(ns + "Object")?.Attribute("Index")?.Value == "[ix_definitions_page]");

            Assert.True((bool?)seek.Element(ns + "IndexScan")?.Attribute("Ordered"));

            if (after != null)
                Assert.Contains(seek.Descendants(ns + "SeekPredicates").Descendants(ns + "ColumnReference"),
                    column => (string?)column.Attribute("Column") == "NameOrder");
        }
    }

    [Theory]
    [InlineData(StoreProfiles.DistributedSqlServer)]
    public async Task DraftPersistedOrderingKeyPreservesMaximumLengthUnicodeNamesAndSummaryUpdates(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();

        // 200 BMP units require 600 UTF-8 bytes; 100 supplementary scalars use the full UTF-16 bound.
        string[] names =
        [
            "A", "AA", "\ue000", new string('\ue000', 200), "\ufffd", new string('\ufffd', 200),
            "\U00010000", "\U00010000A", string.Concat(Enumerable.Repeat("\U00010000", 100))
        ];

        foreach (var name in names.Reverse())
        {
            var draft = await store.SaveDefinitionAsync(new(name, 1, "workflow", "original"), null, "creator");
            await store.SaveDefinitionAsync(draft with { Source = "changed source" }, draft.Id, "editor");
        }

        await using var connection = database.NewConnection();

        var rows = (await connection.QueryAsync<(string Name, byte[] NameOrder)>(
            "SELECT Name, NameOrder FROM dbo.IxIFlowDefinitions ORDER BY Kind, NameOrder, Version")).ToArray();

        Assert.Equal(names, rows.Select(row => row.Name));

        foreach (var row in rows)
            Assert.Equal(Encoding.UTF8.GetBytes(row.Name), row.NameOrder);

        foreach (var prefix in new string?[] { null, "", "\U00010000" })
        {
            string? after = null;

            foreach (var name in names.Where(name => prefix == null || name.StartsWith(prefix, StringComparison.Ordinal)))
            {
                var page = await store.GetDefinitionsAsync(new(CatalogScope.Unrestricted, Kind: "workflow", NamePrefix: prefix,
                    After: after, Limit: 1));

                var summary = Assert.Single(page.Items);
                Assert.Equal(name, summary.Name);
                Assert.Equal(2, summary.Revision);
                Assert.Equal("changed source".Length, summary.SourceLength);
                Assert.Equal("editor", summary.UpdatedBy);
                after = page.NextCursor;
            }

            Assert.Null(after);
        }
    }
}