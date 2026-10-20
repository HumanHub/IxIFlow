using System.Data.Common;
using System.Text.Json;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "registration")]
[Trait("Spec", "F-10")]
[Trait("Spec", "F-11")]
[Trait("Spec", "F-15")]
public sealed class CatalogArtifactDatabaseRegressionTests
{
    [SqlServerFact, Trait("Category", "SqlIntegration")]
    public async Task SqlServerLocksOnlyStartsAndChildStarts() =>
        await LockScopeAsync(new SqlWorkflowStateRepository(SqlTestDatabase<CatalogArtifactDatabaseRegressionTests>.ConnectionString),
            () => new SqlConnection(SqlTestDatabase<CatalogArtifactDatabaseRegressionTests>.ConnectionString), true);

    [PostgreSqlFact, Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSqlLocksOnlyStartsAndChildStarts() =>
        await LockScopeAsync(new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<CatalogArtifactDatabaseRegressionTests>.ConnectionString),
            () => new NpgsqlConnection(PostgreSqlTestDatabase<CatalogArtifactDatabaseRegressionTests>.ConnectionString), false);

    private static async Task LockScopeAsync(IWorkflowStateRepository store, Func<DbConnection> connect, bool sqlServer)
    {
        var name = "Locks-" + Guid.NewGuid().ToString("N");
        await store.RegisterWorkflowVersionAsync(new(name, 1, "locks"));
        var ordinary = Instance(name);
        var parent = Instance(name);
        var root = Instance(name);
        var child = Instance(name);
        child.ParentInstanceId = parent.InstanceId;
        child.ParentWaitId = "child";
        child.Status = WorkflowStatus.Running;
        await store.SeedAsync(ordinary, wakeAfter: TimeSpan.Zero);
        await store.SeedAsync(parent, wakeAfter: TimeSpan.Zero);
        var ordinaryLease = await StoreTestSupport.LeaseAsync(store, ordinary.InstanceId);
        var parentLease = await StoreTestSupport.LeaseAsync(store, parent.InstanceId);
        var rootLease = await StoreTestSupport.LeaseAsync(store, root.InstanceId);
        await using var connection = connect();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(sqlServer
            ? "EXEC sys.sp_getapplock @Resource=N'IxIFlow.Catalog', @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=5000"
            : "SELECT pg_advisory_xact_lock(hashtext('IxIFlow.Catalog'))", transaction: transaction);
        var starting = store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(root, rootLease.Epoch, 0));
        var children = store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(parent, parentLease.Epoch, parent.Revision,
            childStarts: [new(child.InstanceId, JsonSerializer.Serialize(child), "child")],
            openedWaits: [new("child", WaitKind.Child, "child", ChildCompleted.EventType, null)]));
        try
        {
            var commit = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(ordinary, ordinaryLease.Epoch, ordinary.Revision))
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(WorkflowCommitStatus.Applied, commit.Status);
            var draft = await store.SaveDefinitionAsync(new(name, 2, "workflow", "source"), 0).WaitAsync(TimeSpan.FromSeconds(5));
            await store.SaveDefinitionAsync(draft with { Source = "edit" }, 1).WaitAsync(TimeSpan.FromSeconds(5));
            // The blocked child-start must not have locked its parent's row before the catalog lock.
            await using var probe = connect();
            await probe.OpenAsync();
            await probe.ExecuteAsync(sqlServer
                ? "UPDATE dbo.IxIFlowWorkflowInstances SET Status=Status WHERE InstanceId=@InstanceId"
                : "UPDATE ixiflow_workflow_instances SET status=status WHERE instance_id=@InstanceId",
                new { parent.InstanceId }, commandTimeout: 5);
            Assert.False(starting.IsCompleted);
            Assert.False(children.IsCompleted);
        }
        finally { await transaction.RollbackAsync(); }
        Assert.Equal(WorkflowCommitStatus.Applied, (await starting).Status);
        Assert.Equal(WorkflowCommitStatus.Applied, (await children).Status);
    }

    [SqlServerFact, Trait("Category", "SqlIntegration")]
    public async Task SqlServerRetirementChunksCallersAndReadsOnlyNeededColumns()
    {
        var cs = SqlTestDatabase<CatalogArtifactDatabaseRegressionTests>.ConnectionString;
        await ProjectionAsync(new SqlWorkflowStateRepository(cs), () => new SqlConnection(cs), Provider.SqlServer);
    }

    [PostgreSqlFact, Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSqlRetirementReadsOnlyUnfinishedCallers()
    {
        var cs = PostgreSqlTestDatabase<CatalogArtifactDatabaseRegressionTests>.ConnectionString;
        await ProjectionAsync(new PostgreSqlWorkflowStateRepository(cs), () => new NpgsqlConnection(cs), Provider.PostgreSql);
    }

    [Fact, Trait("Category", "SqliteIntegration")]
    public async Task SqliteRetirementReadsOnlyUnfinishedCallers()
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.PR67Projection", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var cs = "Data Source=" + Path.Combine(folder, "catalog.db");
        try
        {
            await using var store = new SqliteWorkflowStateRepository(cs);
            await ProjectionAsync(store, () => new SqliteConnection(cs + ";Pooling=False"), Provider.Sqlite);
        }
        finally { await SqliteTestFiles.DeleteAsync(folder); }
    }

    private enum Provider { SqlServer, PostgreSql, Sqlite }

    private static async Task ProjectionAsync(IWorkflowStateRepository store, Func<DbConnection> connect, Provider provider)
    {
        var name = "Projection-" + Guid.NewGuid().ToString("N");
        await store.RegisterWorkflowVersionAsync(new(name, 1, "child"));
        // More names than SQL Server allows in one command; all are retired callers.
        var callers = Enumerable.Range(0, 2105).Select(i => new WorkflowVersion(name + "-" + i, 1,
            "caller", WorkflowVersionState.Retired, Dependencies: [new(name, 1)])).ToArray();
        await using var connection = connect();
        await connection.OpenAsync();
        var insert = provider == Provider.SqlServer
            ? "INSERT INTO dbo.IxIFlowWorkflowVersions (Name,Version,StructureHash,State,MetadataJson) VALUES (@Name,1,'caller','Retired',@Metadata)"
            : "INSERT INTO ixiflow_workflow_versions (name,version,structure_hash,state,metadata_json) VALUES (@Name,1,'caller','Retired',@Metadata)";
        await connection.ExecuteAsync(insert, callers.Select(caller => new { caller.Name, Metadata = JsonSerializer.Serialize(caller) }));
        var unrelated = Instance(name + "-unrelated");
        var terminal = Instance(name + "-terminal");
        terminal.Status = WorkflowStatus.Terminated;
        var parent = Instance(callers[^1].Name);
        await store.SeedAsync(unrelated);
        await store.SeedAsync(terminal);
        // Retired callers cannot start through the protocol: seed a historical unfinished parent
        // under a temporary uncatalogued name, then restore its persisted caller identity.
        var oldName = parent.WorkflowName;
        parent.WorkflowName = name + "-historic";
        await store.SeedAsync(parent);
        parent.WorkflowName = oldName;
        var update = provider == Provider.SqlServer
            ? "UPDATE dbo.IxIFlowWorkflowInstances SET WorkflowName=@Name, StateJson=@Json WHERE InstanceId=@Id"
            : "UPDATE ixiflow_workflow_instances SET workflow_name=@Name, state_json=@Json WHERE instance_id=@Id";
        // Extra payload fields cannot be deserialized as a WorkflowInstance. Retirement needs only
        // the SQL status, workflow name and the numeric version extracted from JSON.
        var projected = "{\"WorkflowVersion\":1,\"Status\":{},\"WorkflowDataJson\":[]}";
        await connection.ExecuteAsync(update, new { Name = oldName, Json = projected, Id = parent.InstanceId });
        await connection.ExecuteAsync(update, new { Name = unrelated.WorkflowName, Json = "invalid JSON", Id = unrelated.InstanceId });
        await connection.ExecuteAsync(update, new { Name = callers[0].Name, Json = "invalid JSON", Id = terminal.InstanceId });
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => store.SetWorkflowVersionStateAsync(name, 1, WorkflowVersionState.Retired));
        Assert.Equal(WorkflowErrorKind.InvalidState, error.Kind);
        Assert.Contains("unfinished parent", error.Message);
        await connection.ExecuteAsync(provider == Provider.SqlServer
            ? "UPDATE dbo.IxIFlowWorkflowInstances SET Status='Completed' WHERE InstanceId=@Id"
            : "UPDATE ixiflow_workflow_instances SET status='Completed' WHERE instance_id=@Id", new { Id = parent.InstanceId });
        await store.SetWorkflowVersionStateAsync(name, 1, WorkflowVersionState.Retired);
    }

    private static WorkflowInstance Instance(string name) => new()
    {
        InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = name, WorkflowVersion = 1,
        Status = WorkflowStatus.Suspended, ExecutionStateJson = StoreTestSupport.CheckpointJson,
        WorkflowDataJson = StoredJson.Inline("{}"), WorkflowDataType = IxIFlow.Core.Runtime.WorkflowTypeIdentity.SavedName(typeof(object))
    };
}
