using Dapper;
using IxIFlow.Core;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "registration")]
[Trait("Spec", "F-09")]
[Trait("Spec", "F-14")]
public sealed class PublicationDraftSchemaScenarios
{
    public static IEnumerable<object[]> MissingParts =>
        new[] { StoreProfiles.LocalSqlite, StoreProfiles.DistributedSqlServer, StoreProfiles.DistributedPostgreSql }
            .SelectMany(profile => new[]
            {
                "definitions.pk", "workflow_versions.pk", "draft_generations.pk", "publication_heads.pk", "publication_attempts.pk",
                "definitions.generation", "definitions.source_length", "definitions.updated_at_utc", "definitions.updated_by",
                "draft_generations.name", "publication_heads.active_id", "publication_heads.last_attempt",
                "publication_attempts.heartbeat_at_utc", "publication_attempts.started_by", "publication_attempts.outcome_json",
                "workflow_versions.published_draft_generation", "workflow_versions.published_draft_revision",
                "workflow_versions.published_at_utc", "workflow_versions.published_by", "workflow_versions.state_changed_at_utc",
                "workflow_versions.state_changed_by", "workflow_versions.lifecycle_revision", "attempt-index"
            }.Select(part => new object[] { profile, part }));

    [Theory]
    [MemberData(nameof(MissingParts))]
    public async Task PublicationDraftSchemaRejectsMissingColumnsKeysAndWrongUniqueShape(string profile, string part)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await store.GetCatalogRevisionAsync();
        if (store is IAsyncDisposable disposable) await disposable.DisposeAsync();
        await using var connection = database.NewConnection();
        bool sqlServer = profile == StoreProfiles.DistributedSqlServer;
        string Column(string value) => sqlServer ? string.Concat(value.Split('_').Select(word => char.ToUpperInvariant(word[0]) + word[1..])) : value;
        string Table(string value) => sqlServer ? "dbo.IxIFlow" + Column(value) : "ixiflow_" + value;
        if (part == "attempt-index")
        {
            await connection.ExecuteAsync(sqlServer ? "DROP INDEX ix_publication_attempt_identity ON dbo.IxIFlowPublicationAttempts"
                : "DROP INDEX ix_publication_attempt_identity");
            // Keeping the expected name must not let a wrong key shape pass the probe.
            await connection.ExecuteAsync("CREATE UNIQUE INDEX ix_publication_attempt_identity ON " + Table("publication_attempts") + " (" + Column("id") + ")");
        }
        else
        {
            var pair = part.Split('.');
            var table = Table(pair[0]);
            if (pair[1] != "pk")
            {
                if (sqlServer && pair[0] == "definitions")
                    await connection.ExecuteAsync("DROP INDEX ix_definitions_page ON dbo.IxIFlowDefinitions");
                await connection.ExecuteAsync("ALTER TABLE " + table + " DROP COLUMN " + Column(pair[1]));
            }
            else if (profile == StoreProfiles.LocalSqlite)
            {
                var ddl = await connection.QuerySingleAsync<string>("SELECT sql FROM sqlite_master WHERE type='table' AND name=@Name", new { Name = table });
                var indexes = (await connection.QueryAsync<string>("SELECT sql FROM sqlite_master WHERE type='index' AND tbl_name=@Name AND sql IS NOT NULL", new { Name = table })).ToArray();
                // Preserve every column and secondary index; remove only the primary key constraint.
                ddl = System.Text.RegularExpressions.Regex.Replace(ddl, @",\s*PRIMARY KEY\s*\([^)]*\)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                await connection.ExecuteAsync("DROP TABLE " + table);
                await connection.ExecuteAsync(ddl);
                foreach (var index in indexes) await connection.ExecuteAsync(index);
            }
            else
            {
                var key = sqlServer
                    ? await connection.QuerySingleAsync<string>("SELECT name FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID(@Name) AND type='PK'", new { Name = table })
                    : await connection.QuerySingleAsync<string>("SELECT conname FROM pg_constraint WHERE conrelid=CAST(@Name AS regclass) AND contype='p'", new { Name = table });
                await connection.ExecuteAsync("ALTER TABLE " + table + " DROP CONSTRAINT " + (sqlServer ? "[" + key.Replace("]", "]]") + "]" : "\"" + key.Replace("\"", "\"\"") + "\""));
            }
        }
        var reopened = database.NewRepository();
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => reopened.GetCatalogRevisionAsync());
        Assert.Equal(WorkflowErrorKind.InvalidState, error.Kind);
        Assert.Contains("earlier build", error.Message);
    }

    [Theory]
    [InlineData(StoreProfiles.LocalSqlite)]
    [InlineData(StoreProfiles.DistributedSqlServer)]
    [InlineData(StoreProfiles.DistributedPostgreSql)]
    public async Task PublicationWorkflowAuditProjectionMustAgreeWithMetadata(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var contract = WorkflowContract.OfType(typeof(object));
        await store.RegisterWorkflowVersionAsync(new("Audit", 1, "hash", contract.ToJson(), contract.Hash, Descriptor: new WorkflowDescriptor("Audit", "", [], null, null, IxIFlow.Core.Runtime.WorkflowTypeIdentity.SavedName(typeof(object)), IxIFlow.Core.Runtime.WorkflowTypeIdentity.CSharpName(typeof(object))), []));
        await using var connection = database.NewConnection();
        await connection.ExecuteAsync(profile == StoreProfiles.DistributedSqlServer
            ? "UPDATE dbo.IxIFlowWorkflowVersions SET StateChangedBy='forged' WHERE Name='Audit'"
            : "UPDATE ixiflow_workflow_versions SET state_changed_by='forged' WHERE name='Audit'");
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => store.GetWorkflowVersionAsync("Audit", 1));
        Assert.Equal(WorkflowErrorKind.ArtifactMismatch, error.Kind);
    }
}
