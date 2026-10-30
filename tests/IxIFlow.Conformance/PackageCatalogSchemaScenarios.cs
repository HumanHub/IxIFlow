using Dapper;
using IxIFlow.Core;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "registration")]
[Trait("Spec", "F-14")]
public sealed class PackageCatalogSchemaScenarios
{
    public static TheoryData<string, string, string> RequiredParts
    {
        get
        {
            var parts = new Dictionary<string, string[]>
            {
                ["packages"] =
                [
                    "table", "origin", "extraction", "extraction_detail", "closure_json", "withdrawn", "withdrawn_at_utc", "withdrawn_by",
                    "registered_at_utc", "registered_by", "published_draft_generation", "published_draft_revision", "published_draft_name",
                    "published_draft_version",
                    "published_at_utc", "published_by", "lifecycle_revision", "catalog_revision"
                ],
                ["installed_packages"] = ["catalog_revision"],
                ["package_activities"] =
                [
                    "table", "package_id", "package_version", "key", "version", "metadata_type_name", "csharp_type_name", "display_name",
                    "category", "icon", "search_text", "contract_hash", "descriptor_json", "catalog_revision"
                ],
                ["installed_activities"] =
                [
                    "table", "package_id", "package_version", "key", "version", "metadata_type_name", "csharp_type_name", "display_name",
                    "category", "icon", "search_text", "contract_hash", "descriptor_json", "catalog_revision", "fingerprint"
                ],
                ["package_pending_dependencies"] = ["table", "package_id", "package_version", "missing_package_id"]
            };

            var result = new TheoryData<string, string, string>();

            foreach (var profile in StoreProfiles.ProfileNames.Where(item => item != StoreProfiles.LocalMemory))
            foreach (var (table, columns) in parts)
            foreach (var column in columns)
                result.Add(profile, table, column);

            return result;
        }
    }

    [Theory]
    [MemberData(nameof(RequiredParts))]
    public async Task PackageAndActivitySchemasRequireEveryProjectionColumn(string profile, string logicalTable, string logicalColumn)
    {
        await using var database = await StoreProfiles.CreateAsync(profile, emptySchema: true);
        var store = database.NewRepository();
        await store.GetCatalogRevisionAsync();

        if (store is IAsyncDisposable disposable)
            await disposable.DisposeAsync();

        var sqlServer = database.Profile == StoreProfiles.DistributedSqlServer;
        var table = sqlServer ? "dbo.IxIFlow" + Pascal(logicalTable) : "ixiflow_" + logicalTable;
        var column = sqlServer ? Pascal(logicalColumn) : logicalColumn;

        await using (var connection = database.NewConnection())
        {
            if (sqlServer && logicalColumn != "table")
                foreach (var constraint in await connection.QueryAsync<string>("""
                                                                               SELECT QUOTENAME(name) FROM sys.check_constraints
                                                                               WHERE parent_object_id=OBJECT_ID(@Table)
                                                                                 AND parent_column_id=COLUMNPROPERTY(OBJECT_ID(@Table), @Column, 'ColumnId')
                                                                               """, new { Table = table, Column = column }))
                    await connection.ExecuteAsync($"ALTER TABLE {table} DROP CONSTRAINT {constraint}");

            var sql = sqlServer
                ? logicalColumn == "table"
                    ? $"EXEC sp_rename N'{table}', N'PreviousCatalogTable'"
                    : $"EXEC sp_rename N'{table}.{column}', N'PreviousCatalogColumn', N'COLUMN'"
                : logicalColumn == "table"
                    ? $"ALTER TABLE {table} RENAME TO previous_catalog_table"
                    : $"ALTER TABLE {table} RENAME COLUMN \"{column}\" TO previous_catalog_column";

            await connection.ExecuteAsync(sql);
        }

        var reopened = database.NewRepository();
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => reopened.GetCatalogRevisionAsync());
        Assert.Equal(WorkflowErrorKind.InvalidState, error.Kind);
        Assert.Contains("recreated", error.Message);
    }

    private static string Pascal(string value) => value == "csharp_type_name"
        ? "CSharpTypeName"
        : string.Concat(value.Split('_').Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
}