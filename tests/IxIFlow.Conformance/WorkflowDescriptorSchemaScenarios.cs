using Dapper;
using IxIFlow.Core;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "registration")]
[Trait("Spec", "F-14")]
public sealed class WorkflowDescriptorSchemaScenarios
{
    public static TheoryData<string, string> RequiredParts
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var profile in StoreProfiles.ProfileNames.Where(profile => profile != StoreProfiles.LocalMemory))
                foreach (var part in new[] { "display_name", "category", "tags", "search_text", "descriptor_json", "contract_hash", "category-index", "primary-key" })
                    data.Add(profile, part);
            return data;
        }
    }

    [Theory, MemberData(nameof(RequiredParts))]
    public async Task EveryWorkflowProjectionAndIndexShapeIsRequiredOnReopen(string profile, string part)
    {
        await using var database = await StoreProfiles.CreateAsync(profile, emptySchema: true);
        var store = database.NewRepository();
        await store.GetCatalogRevisionAsync();
        if (store is IAsyncDisposable disposable) await disposable.DisposeAsync();
        var sqlServer = database.Profile == StoreProfiles.DistributedSqlServer;
        var table = sqlServer ? "dbo.IxIFlowWorkflowVersions" : "ixiflow_workflow_versions";
        var column = sqlServer ? string.Concat(part.Split('_').Select(word => char.ToUpperInvariant(word[0]) + word[1..])) : part;
        await using (var connection = database.NewConnection())
        {
            if (part == "category-index")
            {
                await connection.ExecuteAsync(sqlServer ? $"DROP INDEX ix_workflow_versions_category ON {table}" : "DROP INDEX ix_workflow_versions_category");
                await connection.ExecuteAsync($"CREATE INDEX ix_workflow_versions_category ON {table} ({(sqlServer ? "Name,Category" : "name,category")})");
            }
            else if (part == "primary-key")
            {
                if (database.Profile == StoreProfiles.LocalSqlite)
                {
                    // Rebuild the empty fixture table, retaining every column and secondary index.
                    var ddl = await connection.QuerySingleAsync<string>("SELECT sql FROM sqlite_master WHERE type='table' AND name=@Table", new { Table = table });
                    var indexes = (await connection.QueryAsync<string>("SELECT sql FROM sqlite_master WHERE type='index' AND tbl_name=@Table AND sql IS NOT NULL", new { Table = table })).ToArray();
                    await connection.ExecuteAsync($"DROP TABLE {table}");
                    await connection.ExecuteAsync(ddl.Replace("PRIMARY KEY (name, version)", "PRIMARY KEY (version, name)"));
                    foreach (var index in indexes) await connection.ExecuteAsync(index);
                }
                else if (sqlServer)
                {
                    var key = await connection.QuerySingleAsync<string>("SELECT name FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID(@Table) AND type='PK'", new { Table = table });
                    await connection.ExecuteAsync($"ALTER TABLE {table} DROP CONSTRAINT [{key.Replace("]", "]]")}]; ALTER TABLE {table} ADD PRIMARY KEY (Version,Name)");
                }
                else
                {
                    var key = await connection.QuerySingleAsync<string>("SELECT conname FROM pg_constraint WHERE conrelid='ixiflow_workflow_versions'::regclass AND contype='p'");
                    await connection.ExecuteAsync($"ALTER TABLE {table} DROP CONSTRAINT \"{key.Replace("\"", "\"\"")}\"; ALTER TABLE {table} ADD PRIMARY KEY (version,name)");
                }
            }
            else await connection.ExecuteAsync(sqlServer
                ? $"EXEC sp_rename N'{table}.{column}', N'PreviousWorkflowColumn', N'COLUMN'"
                : $"ALTER TABLE {table} RENAME COLUMN \"{column}\" TO previous_workflow_column");
        }
        var reopened = database.NewRepository();
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => reopened.GetCatalogRevisionAsync());
        Assert.Equal(WorkflowErrorKind.InvalidState, error.Kind);
        Assert.Contains("recreated", error.Message);
    }
}
