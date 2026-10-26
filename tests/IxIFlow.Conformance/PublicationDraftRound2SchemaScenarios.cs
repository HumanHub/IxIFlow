using Dapper;
using IxIFlow.Core;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "registration")]
[Trait("Spec", "F-09")]
[Trait("Spec", "F-14")]
public sealed class PublicationDraftRound2SchemaScenarios
{
    public static IEnumerable<object[]> UniqueIndexes =>
        new[] { StoreProfiles.LocalSqlite, StoreProfiles.DistributedSqlServer, StoreProfiles.DistributedPostgreSql }
            .SelectMany(profile => new[] { "ix_publication_finished", "ix_publication_history" }
                .Select(index => new object[] { profile, index }));

    [Theory]
    [MemberData(nameof(UniqueIndexes))]
    public async Task PublicationSchemaRejectsSameNameUniqueRetentionAndHistoryIndexes(string profile, string index)
    {
        await using var database = await StoreProfiles.CreateAsync(profile, emptySchema: true);
        await InitializeAndReopenAsync(database);
        await using var connection = database.NewConnection();
        var sqlServer = profile == StoreProfiles.DistributedSqlServer;
        var table = sqlServer ? "dbo.IxIFlowPublicationAttempts" : "ixiflow_publication_attempts";
        var columns = index == "ix_publication_finished"
            ? sqlServer ? "FinishedAtUtc" : "finished_at_utc"
            : sqlServer ? "Kind, Name, Version, StartedAtUtc, Id" : "kind, name, version, started_at_utc, id";
        await connection.ExecuteAsync($"DROP INDEX {index}" + (sqlServer ? " ON " + table : ""));
        await connection.ExecuteAsync($"CREATE UNIQUE INDEX {index} ON {table} ({columns})");
        await AssertRejectedAsync(database);
    }

    public static IEnumerable<object[]> WrongCollations => new[]
    {
        ("ixiflow_publication_attempts", "ix_publication_history", "kind"),
        ("ixiflow_publication_attempts", "ix_publication_history", "name"),
        ("ixiflow_publication_attempts", "ix_publication_attempt_identity", "kind"),
        ("ixiflow_publication_attempts", "ix_publication_attempt_identity", "name"),
        ("ixiflow_definitions", "primary", "kind"),
        ("ixiflow_definitions", "primary", "name"),
        ("ixiflow_workflow_versions", "primary", "name"),
        ("ixiflow_publication_heads", "primary", "kind"),
        ("ixiflow_publication_heads", "primary", "name")
    }.Select(item => new object[] { StoreProfiles.DistributedPostgreSql, item.Item1, item.Item2, item.Item3 });

    [Theory]
    [MemberData(nameof(WrongCollations))]
    public async Task PublicationSchemaRejectsNonCodePointTextKeyCollations(string profile, string table, string index, string column)
    {
        await using var database = await StoreProfiles.CreateAsync(profile, emptySchema: true);
        await InitializeAndReopenAsync(database);
        await using var connection = database.NewConnection();
        if (index == "primary")
        {
            // Changing the column collation rebuilds its real primary index with the wrong collation.
            await connection.ExecuteAsync($"ALTER TABLE {table} ALTER COLUMN {column} TYPE text COLLATE \"POSIX\"");
        }
        else
        {
            var columns = index == "ix_publication_history"
                ? new[] { "kind", "name", "version", "started_at_utc", "id" }
                : new[] { "kind", "name", "version", "generation", "revision", "attempt" };
            var keys = string.Join(", ", columns.Select(key => key == column ? key + " COLLATE \"POSIX\"" : key));
            await connection.ExecuteAsync($"DROP INDEX {index}");
            await connection.ExecuteAsync($"CREATE {(index == "ix_publication_attempt_identity" ? "UNIQUE " : "")}INDEX {index} ON {table} ({keys})");
        }
        await AssertRejectedAsync(database);
    }

    public static IEnumerable<object[]> InvalidDraftPageIndexes => new[]
    {
        "missing", "wrong-table", "wrong-columns", "wrong-order", "descending", "extra-key", "unique", "predicate", "disabled",
        "missing-Name", "missing-Generation", "missing-Revision", "missing-SourceLength", "missing-UpdatedAtUtc", "missing-UpdatedBy",
        "includes-source", "missing-column", "ordinary-column", "not-persisted", "wrong-expression", "wrong-collation", "short-key", "former-utf8-expression"
    }.Select(fault => new object[] { StoreProfiles.DistributedSqlServer, fault });

    [Theory]
    [MemberData(nameof(InvalidDraftPageIndexes))]
    public async Task PublicationDraftSchemaRejectsIncorrectPagingIndexAndOrderingKey(string profile, string fault)
    {
        await using var database = await StoreProfiles.CreateAsync(profile, emptySchema: true);
        await InitializeAndReopenAsync(database);
        await using var connection = database.NewConnection();
        const string table = "dbo.IxIFlowDefinitions";
        const string index = "ix_definitions_page";
        if (fault == "disabled")
            await connection.ExecuteAsync($"ALTER INDEX {index} ON {table} DISABLE");
        else
        {
            await connection.ExecuteAsync($"DROP INDEX {index} ON {table}");
            var keys = "Kind, NameOrder, Version";
            var includes = new[] { "Name", "Generation", "Revision", "SourceLength", "UpdatedAtUtc", "UpdatedBy" }.ToList();
            var target = table;
            var predicate = "";
            if (fault is "missing-column" or "ordinary-column" or "not-persisted" or "wrong-expression" or "wrong-collation" or "short-key" or "former-utf8-expression")
            {
                await connection.ExecuteAsync($"ALTER TABLE {table} DROP COLUMN NameOrder");
                if (fault == "missing-column")
                {
                    await AssertRejectedAsync(database);
                    return;
                }
                var expression = fault == "wrong-expression" ? "CONVERT(VARBINARY(800), Name)"
                    : fault == "former-utf8-expression" ? "CONVERT(VARBINARY(800), CONVERT(VARCHAR(800), Name COLLATE Latin1_General_100_BIN2_UTF8))"
                    : $"CONVERT(VARBINARY({(fault == "short-key" ? 100 : 800)}), CONVERT(VARCHAR(800), Name COLLATE {(fault == "wrong-collation" ? "Latin1_General_100_BIN2" : "Latin1_General_100_CI_AS_SC_UTF8")}) COLLATE {(fault == "wrong-collation" ? "Latin1_General_100_BIN2" : "Latin1_General_100_CI_AS_SC_UTF8")})";
                await connection.ExecuteAsync($"ALTER TABLE {table} ADD NameOrder " + (fault == "ordinary-column" ? "VARBINARY(800) NULL"
                    : $"AS ({expression})" + (fault == "not-persisted" ? "" : " PERSISTED")));
            }
            switch (fault)
            {
                case "wrong-table":
                    target = "dbo.DraftPageIndexDecoy";
                    await connection.ExecuteAsync($"SELECT TOP (0) Kind, NameOrder, Version, Name, Generation, Revision, SourceLength, UpdatedAtUtc, UpdatedBy INTO {target} FROM {table}");
                    break;
                case "wrong-columns": keys = "Kind, Name, Version"; includes.Remove("Name"); break;
                case "wrong-order": keys = "Kind, Version, NameOrder"; break;
                case "descending": keys = "Kind, NameOrder DESC, Version"; break;
                case "extra-key": keys += ", Generation"; includes.Remove("Generation"); break;
                case "predicate": predicate = " WHERE Version>1"; break;
                case "includes-source": includes.Add("Source"); break;
            }
            if (fault.StartsWith("missing-", StringComparison.Ordinal)) includes.Remove(fault[8..]);
            if (fault != "missing")
                await connection.ExecuteAsync($"CREATE {(fault == "unique" ? "UNIQUE " : "")}INDEX {index} ON {target} ({keys}) INCLUDE ({string.Join(", ", includes)}){predicate}");
        }
        await AssertRejectedAsync(database);
    }

    private static async Task InitializeAndReopenAsync(ProfileStore database)
    {
        var store = database.NewRepository();
        await store.GetCatalogRevisionAsync();
        if (store is IAsyncDisposable disposable) await disposable.DisposeAsync();
        // Positive control: the same fresh repository/probe must accept the intact DDL first.
        var reopened = database.NewRepository();
        await reopened.GetCatalogRevisionAsync();
        if (reopened is IAsyncDisposable reopenedDisposable) await reopenedDisposable.DisposeAsync();
    }

    private static async Task AssertRejectedAsync(ProfileStore database)
    {
        var reopened = database.NewRepository();
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => reopened.GetCatalogRevisionAsync());
        Assert.Equal(WorkflowErrorKind.InvalidState, error.Kind);
        Assert.Contains("earlier build", error.Message);
    }
}
