using Dapper;
using IxIFlow.Core;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "durability")]
[Trait("Spec", "C-11")]
public sealed class Slice4SchemaTypeScenarios
{
    public static TheoryData<string> RelationalProfiles => new(StoreProfiles.ProfileNames.Where(p => p != StoreProfiles.LocalMemory));

    [Theory]
    [MemberData(nameof(RelationalProfiles))]
    public async Task CheckpointAndRunnableColumnsHaveTheirRequiredTypesAndNullability(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var repository = database.NewRepository();
        Assert.Null(await repository.GetWorkflowInstanceAsync("schema-type-probe"));
        await using var connection = database.NewConnection();

        var columns = (await connection.QueryAsync<Column>(
            database.Profile switch
            {
                StoreProfiles.LocalSqlite => """
                                             SELECT name AS Name, type AS Type, CASE WHEN "notnull"=0 THEN 1 ELSE 0 END AS Nullable,
                                                 -1 AS Length FROM pragma_table_info('ixiflow_workflow_instances')
                                             WHERE name IN ('state_json','runnable_at_utc') ORDER BY name
                                             """,
                StoreProfiles.DistributedSqlServer => """
                                                      SELECT name AS Name, TYPE_NAME(system_type_id) AS Type,
                                                          is_nullable AS Nullable, max_length AS Length
                                                      FROM sys.columns WHERE object_id=OBJECT_ID('dbo.IxIFlowWorkflowInstances')
                                                          AND name IN ('StateJson','RunnableAtUtc') ORDER BY name
                                                      """,
                _ => """
                     SELECT column_name AS "Name", data_type AS "Type", is_nullable='YES' AS "Nullable",
                         COALESCE(character_maximum_length,-1) AS "Length"
                     FROM information_schema.columns WHERE table_schema=current_schema() AND table_name='ixiflow_workflow_instances'
                         AND column_name IN ('state_json','runnable_at_utc') ORDER BY column_name
                     """
            })).ToArray();

        Assert.Equal(2, columns.Length);
        Assert.True(columns[0].Nullable);
        Assert.False(columns[1].Nullable);
        Assert.Equal(-1, columns[1].Length);

        Assert.Equal(
            database.Profile switch
            {
                StoreProfiles.LocalSqlite => new[] { "TEXT", "TEXT" },
                StoreProfiles.DistributedSqlServer => new[] { "datetime2", "nvarchar" },
                _ => new[] { "timestamp with time zone", "text" }
            }, columns.Select(column => column.Type));
    }

    [Theory]
    [MemberData(nameof(RelationalProfiles))]
    public async Task StartupRefusesAStateColumnChangedToABoundedString(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var repository = database.NewRepository();
        Assert.Null(await repository.GetWorkflowInstanceAsync("initialize-schema"));

        if (repository is IDisposable disposable)
            disposable.Dispose();

        await using (var connection = database.NewConnection())
        {
            await connection.ExecuteAsync(
                database.Profile switch
                {
                    StoreProfiles.LocalSqlite => """
                                                 PRAGMA writable_schema=ON;
                                                 UPDATE sqlite_schema
                                                 SET sql=replace(sql,'state_json TEXT NOT NULL','state_json VARCHAR(4000) NOT NULL')
                                                 WHERE name='ixiflow_workflow_instances';
                                                 PRAGMA schema_version=99;
                                                 PRAGMA writable_schema=OFF;
                                                 """,
                    StoreProfiles.DistributedSqlServer =>
                        "ALTER TABLE dbo.IxIFlowWorkflowInstances ALTER COLUMN StateJson nvarchar(4000) NOT NULL",
                    _ => "ALTER TABLE ixiflow_workflow_instances ALTER COLUMN state_json TYPE varchar(4000)"
                });
        }

        var reopened = database.NewRepository();
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => reopened.GetWorkflowInstanceAsync("schema-type-probe"));
        Assert.Equal(WorkflowErrorKind.InvalidState, error.Kind);
        Assert.Contains("earlier", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    public sealed class Column
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public bool Nullable { get; set; }
        public int Length { get; set; }
    }
}