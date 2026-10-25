using System.Text.Json;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Templates;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "templates")]
[Trait("Spec", "H-03")]
[Trait("Spec", "F-14")]
public sealed class TemplateCatalogSchemaScenarios
{
    private static readonly string[] Columns = ["key", "version", "engine", "format", "model_type", "model_type_id", "model_packages_json", "cultures_json",
        "display_name", "description", "search_text", "published_draft_generation", "published_draft_revision", "published_at_utc", "published_by", "metadata_json", "catalog_revision"];
    private static TheoryData<string, string> Parts(IEnumerable<string> parts)
    {
        var data = new TheoryData<string, string>();
        foreach (var profile in StoreProfiles.ProfileNames.Where(profile => profile != StoreProfiles.LocalMemory))
            foreach (var part in parts) data.Add(profile, part);
        return data;
    }
    public static TheoryData<string, string> RequiredParts => Parts(Columns.Concat(["primary-key", "missing-primary-key"]));

    [Theory, MemberData(nameof(RequiredParts))]
    public async Task EveryTemplateColumnAndTheExactPrimaryKeyAreRequiredOnReopen(string profile, string part)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository(); await store.GetCatalogRevisionAsync();
        if (store is IAsyncDisposable disposable) await disposable.DisposeAsync();
        var sqlServer = database.Profile == StoreProfiles.DistributedSqlServer;
        var table = sqlServer ? "dbo.IxIFlowTemplateVersions" : "ixiflow_template_versions";
        await using (var connection = database.NewConnection())
        {
            if (part is "primary-key" or "missing-primary-key")
            {
                if (database.Profile == StoreProfiles.LocalSqlite)
                {
                    var ddl = await connection.QuerySingleAsync<string>("SELECT sql FROM sqlite_master WHERE type='table' AND name=@Table", new { Table = table });
                    await connection.ExecuteAsync($"DROP TABLE {table}");
                    await connection.ExecuteAsync(part == "primary-key" ? ddl.Replace("PRIMARY KEY (\"key\", \"version\")", "PRIMARY KEY (\"version\", \"key\")")
                        : System.Text.RegularExpressions.Regex.Replace(ddl, ",\\s*PRIMARY KEY \\(\"key\", \"version\"\\)", ""));
                }
                else if (sqlServer)
                {
                    var pk = await connection.QuerySingleAsync<string>("SELECT name FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID(@Table) AND type='PK'", new { Table = table });
                    await connection.ExecuteAsync($"ALTER TABLE {table} DROP CONSTRAINT [{pk.Replace("]", "]]")}]" +
                        (part == "primary-key" ? $"; ALTER TABLE {table} ADD PRIMARY KEY ([Version],[Key])" : ""));
                }
                else
                {
                    var pk = await connection.QuerySingleAsync<string>("SELECT conname FROM pg_constraint WHERE conrelid='ixiflow_template_versions'::regclass AND contype='p'");
                    await connection.ExecuteAsync($"ALTER TABLE {table} DROP CONSTRAINT \"{pk.Replace("\"", "\"\"")}\"" +
                        (part == "primary-key" ? $"; ALTER TABLE {table} ADD PRIMARY KEY (version,key)" : ""));
                }
            }
            else
            {
                var column = sqlServer ? string.Concat(part.Split('_').Select(word => char.ToUpperInvariant(word[0]) + word[1..])) : part;
                await connection.ExecuteAsync(sqlServer ? $"EXEC sp_rename N'{table}.{column}', N'PreviousTemplateColumn', N'COLUMN'"
                    : $"ALTER TABLE {table} RENAME COLUMN \"{column}\" TO previous_template_column");
            }
        }
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => database.NewRepository().GetCatalogRevisionAsync());
        Assert.Equal(WorkflowErrorKind.InvalidState, error.Kind); Assert.Contains("recreated", error.Message);
    }

    public static TheoryData<string, string> ProjectionParts => Parts(["model_packages_json", "cultures_json", "search_text", "model_type_id", "display_name", "metadata_json"]);

    [Theory, MemberData(nameof(ProjectionParts))]
    public async Task ProjectionJsonAndImmutableContentAgreeAndCorruptionIsRefused(string profile, string part)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var entry = await store.RegisterTemplateVersionAsync(TemplateCatalogScenarios.Content(), store);
        var sqlServer = database.Profile == StoreProfiles.DistributedSqlServer;
        var table = sqlServer ? "dbo.IxIFlowTemplateVersions" : "ixiflow_template_versions";
        string Column(string name) => sqlServer ? "[" + string.Concat(name.Split('_').Select(word => char.ToUpperInvariant(word[0]) + word[1..])) + "]" : "\"" + name + "\"";
        await using var connection = database.NewConnection();
        var packages = await connection.QuerySingleAsync<string>($"SELECT {Column("model_packages_json")} FROM {table}");
        var cultures = await connection.QuerySingleAsync<string>($"SELECT {Column("cultures_json")} FROM {table}");
        Assert.Equal(JsonSerializer.Serialize(entry.Version.ModelPackages), packages);
        Assert.Equal(new[] { "", "de" }, JsonSerializer.Deserialize<string[]>(cultures));
        Assert.Equal(await store.GetCatalogRevisionAsync(), await connection.QuerySingleAsync<long>($"SELECT {Column("catalog_revision")} FROM {table}"));
        await connection.ExecuteAsync($"UPDATE {table} SET {Column(part)}=@Value", new { Value = part == "metadata_json" ? "{}" : "corrupt" });
        await Assert.ThrowsAnyAsync<Exception>(() => store.GetTemplateVersionAsync("receipt", 1));
    }
}
