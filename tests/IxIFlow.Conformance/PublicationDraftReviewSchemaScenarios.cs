using Dapper;
using IxIFlow.Core;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "registration")]
[Trait("Spec", "F-09")]
[Trait("Spec", "F-14")]
public sealed class PublicationDraftReviewSchemaScenarios
{
    public static IEnumerable<object[]> InvalidIndexes =>
        new[] { StoreProfiles.LocalSqlite, StoreProfiles.DistributedSqlServer, StoreProfiles.DistributedPostgreSql }
            .SelectMany(profile => new[] { "ix_publication_finished", "ix_publication_history" }
                .SelectMany(index => Cases(profile, index).Select(fault => new object[] { profile, index, fault })));

    private static IEnumerable<string> Cases(string profile, string index)
    {
        yield return "missing";
        yield return "wrong-columns";
        yield return "wrong-table";
        yield return "predicate";
        yield return "descending";
        yield return "extra-key";
        if (index == "ix_publication_history") yield return "wrong-order";
        if (profile == StoreProfiles.DistributedSqlServer) yield return "disabled";
        if (profile == StoreProfiles.DistributedPostgreSql)
        {
            yield return "invalid";
            yield return "not-ready";
            yield return "not-live";
        }
        if (profile != StoreProfiles.DistributedSqlServer) yield return "expression";
    }

    [Theory]
    [MemberData(nameof(InvalidIndexes))]
    public async Task PublicationSchemaRejectsIncorrectHistoryAndRetentionIndexes(string profile, string index, string fault)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await store.GetCatalogRevisionAsync();
        if (store is IAsyncDisposable disposable) await disposable.DisposeAsync();
        await using var connection = database.NewConnection();
        var sqlServer = profile == StoreProfiles.DistributedSqlServer;
        string Column(string value) => sqlServer ? string.Concat(value.Split('_').Select(word => char.ToUpperInvariant(word[0]) + word[1..])) : value;
        var table = sqlServer ? "dbo.IxIFlowPublicationAttempts" : "ixiflow_publication_attempts";
        var columns = (index == "ix_publication_finished" ? new[] { "finished_at_utc" } : new[] { "kind", "name", "version", "started_at_utc", "id" }).Select(Column).ToArray();
        if (fault == "disabled") await connection.ExecuteAsync($"ALTER INDEX {index} ON {table} DISABLE");
        else if (fault is "invalid" or "not-ready" or "not-live")
        {
            // PostgreSQL's catalog flags describe indexes left unusable by interrupted concurrent DDL.
            var flag = fault == "invalid" ? "indisvalid" : fault == "not-ready" ? "indisready" : "indislive";
            await connection.ExecuteAsync($"UPDATE pg_index SET {flag}=false WHERE indexrelid=CAST(@Name AS regclass)", new { Name = index });
        }
        else
        {
            await connection.ExecuteAsync($"DROP INDEX {index}" + (sqlServer ? " ON " + table : ""));
            if (fault != "missing")
            {
                var target = table;
                var keys = string.Join(", ", columns);
                var predicate = "";
                switch (fault)
                {
                    case "wrong-columns": keys = Column("host_id"); break;
                    case "wrong-table":
                        target = sqlServer ? "dbo.PublicationIndexDecoy" : "publication_index_decoy";
                        await connection.ExecuteAsync($"CREATE TABLE {target} (" + string.Join(", ", columns.Select(column => column + " int")) + ")");
                        break;
                    case "predicate": predicate = " WHERE " + Column("finished_at_utc") + " IS NOT NULL"; break;
                    case "descending": keys = columns[0] + " DESC" + (columns.Length == 1 ? "" : ", " + string.Join(", ", columns.Skip(1))); break;
                    case "extra-key": keys += ", " + Column("host_id"); break;
                    case "wrong-order": keys = string.Join(", ", columns.Reverse()); break;
                    case "expression":
                        keys = profile == StoreProfiles.DistributedPostgreSql && columns.Length == 1
                            ? "(COALESCE(finished_at_utc, '2000-01-01 00:00:00+00'::timestamptz))"
                            : "(" + columns[0] + " || '')" + (columns.Length == 1 ? "" : ", " + string.Join(", ", columns.Skip(1)));
                        break;
                }
                await connection.ExecuteAsync($"CREATE INDEX {index} ON {target} ({keys}){predicate}");
            }
        }
        var reopened = database.NewRepository();
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => reopened.GetCatalogRevisionAsync());
        Assert.Equal(WorkflowErrorKind.InvalidState, error.Kind);
        Assert.Contains("earlier build", error.Message);
    }
}
