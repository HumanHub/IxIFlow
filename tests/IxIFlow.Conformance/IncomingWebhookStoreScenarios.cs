using System.Data.Common;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace IxIFlow.Tests.Conformance;

[Trait("Spec", "D-10")]
[Trait("Feature", "durability")]
public sealed class IncomingWebhookStoreScenarios
{
    public static TheoryData<string> SqlStores => new() { "sqlite", "sqlserver", "postgresql" };
    public static TheoryData<string, string> IncompleteSchemas
    {
        get
        {
            var cases = new TheoryData<string, string>();
            foreach (var profile in new[] { "sqlite", "sqlserver", "postgresql" })
                foreach (var part in new[] { "admissions", "ownership", "deliveries", "owner-kind", "old-column", "outbox", "epoch" })
                    cases.Add(profile, part);
            return cases;
        }
    }

    private sealed class Database(string profile, string connectionString, string? folder = null) : IAsyncDisposable
    {
        public bool SqlServer => profile == "sqlserver";
        public DbConnection Connection { get; } = profile switch
        {
            "sqlserver" => new SqlConnection(connectionString),
            "postgresql" => new NpgsqlConnection(connectionString),
            _ => new SqliteConnection(new SqliteConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString)
        };
        public IWorkflowStateRepository Repository() => profile switch
        {
            "sqlserver" => new SqlWorkflowStateRepository(connectionString),
            "postgresql" => new PostgreSqlWorkflowStateRepository(connectionString),
            _ => new SqliteWorkflowStateRepository(connectionString)
        };
        public string Table(string name) => SqlServer ? name switch
        {
            "admissions" => "dbo.IxIFlowWorkflowAdmissions",
            "ownership" => "dbo.IxIFlowWorkflowOwnership",
            "deliveries" => "dbo.IxIFlowWebhookDeliveries",
            "tombstones" => "dbo.IxIFlowWorkflowTombstones",
            "instances" => "dbo.IxIFlowWorkflowInstances",
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        } : name switch
        {
            "deliveries" => "ixiflow_webhook_deliveries",
            _ => "ixiflow_workflow_" + name
        };
        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            if (profile == "sqlserver") await SqlServerTestServer.DropDatabaseAsync(connectionString);
            else if (profile == "postgresql") await PostgreSqlTestServer.DropDatabaseAsync(connectionString);
            else await SqliteTestFiles.DeleteAsync(folder!);
        }
    }

    private static async Task<Database> CreateAsync(string profile)
    {
        if (profile == "sqlserver") return new(profile, await SqlServerTestServer.CreateDatabaseAsync("IxIFlowIngressFix"));
        if (profile == "postgresql") return new(profile, await PostgreSqlTestServer.CreateDatabaseAsync("ixiflow_ingress_fix"));
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.IngressFix", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return new(profile, $"Data Source={Path.Combine(folder, "state.db")}", folder);
    }

    private static ValueTask DisposeRepositoryAsync(IWorkflowStateRepository repository) =>
        repository is IAsyncDisposable disposable ? disposable.DisposeAsync() : ValueTask.CompletedTask;

    [Theory]
    [MemberData(nameof(SqlStores))]
    public async Task InstanceTableFromAnEarlierBuildIsRefusedWithoutCreatingOrMigratingTables(string profile)
    {
        await using var database = await CreateAsync(profile);
        await database.Connection.ExecuteAsync($"CREATE TABLE {database.Table("instances")} (old_id int)");
        var repository = database.Repository();
        try
        {
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => repository.GetWorkflowInstanceAsync("missing"));
            Assert.Equal(WorkflowErrorKind.InvalidState, error.Kind);
            Assert.Contains("earlier build", error.Message);
            Assert.Contains("must be recreated", error.Message);
            Assert.NotNull(error.InnerException);
            Assert.Equal(0, await database.Connection.ExecuteScalarAsync<int>(database.SqlServer
                ? "SELECT COUNT(*) FROM sys.tables WHERE name='IxIFlowWorkflowAdmissions'"
                : profile == "postgresql" ? "SELECT COUNT(*) FROM information_schema.tables WHERE table_name='ixiflow_workflow_admissions'"
                : "SELECT COUNT(*) FROM sqlite_schema WHERE name='ixiflow_workflow_admissions'"));
        }
        finally { await DisposeRepositoryAsync(repository); }
    }

    [Theory]
    [MemberData(nameof(IncompleteSchemas))]
    public async Task ExistingDatabaseMustHaveEveryCurrentTableAndColumn(string profile, string missing)
    {
        await using var database = await CreateAsync(profile);
        var first = database.Repository();
        try { Assert.Null(await first.GetWorkflowInstanceAsync("missing")); }
        finally { await DisposeRepositoryAsync(first); }
        var sql = missing switch
        {
            "owner-kind" => $"ALTER TABLE {database.Table("ownership")} DROP COLUMN {(database.SqlServer ? "OwnerKind" : "owner_kind")}",
            "old-column" => $"ALTER TABLE {database.Table("instances")} DROP COLUMN {(database.SqlServer ? "StateJson" : "state_json")}",
            "outbox" => "DROP TABLE " + (database.SqlServer ? "dbo.IxIFlowOutboxDeliveries" : "ixiflow_outbox_deliveries"),
            "epoch" => profile == "sqlite" ? "DROP TABLE ixiflow_execution_epoch"
                : "DROP SEQUENCE " + (database.SqlServer ? "dbo.IxIFlowExecutionEpoch" : "ixiflow_execution_epoch"),
            _ => "DROP TABLE " + database.Table(missing)
        };
        await database.Connection.ExecuteAsync(sql);
        var second = database.Repository();
        try
        {
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => second.GetWorkflowInstanceAsync("missing"));
            Assert.Contains("earlier build", error.Message);
            Assert.Contains("must be recreated", error.Message);
        }
        finally { await DisposeRepositoryAsync(second); }
    }

    [Theory]
    [MemberData(nameof(SqlStores))]
    public async Task ACompleteCurrentDatabaseCanBeOpenedAgainWithoutSchemaChanges(string profile)
    {
        await using var database = await CreateAsync(profile);
        for (var host = 0; host < 2; host++)
        {
            var repository = database.Repository();
            try { Assert.Null(await repository.GetWorkflowInstanceAsync("missing")); }
            finally { await DisposeRepositoryAsync(repository); }
        }
    }

    [Theory]
    [MemberData(nameof(SqlStores))]
    public async Task SignedReceiptsPersistWithoutRewakingAndRetentionPurgesAllThreeIngressTables(string profile)
    {
        await using var database = await CreateAsync(profile);
        var repository = database.Repository();
        try
        {
            var instance = new WorkflowInstance
            {
                InstanceId = "signed-owner", WorkflowName = "Receipt", WorkflowVersion = 1, Status = WorkflowStatus.Running,
                WorkflowDataType = typeof(object).AssemblyQualifiedName!, WorkflowDataJson = "{}",
                ExecutionStateJson = StoreTestSupport.CheckpointJson
            };
            var request = new WorkflowAdmission("vendor", "trigger", "delivery", new string('A', 64), "vendor",
                OwnerKind: WorkflowOwnerKind.SignedSender);
            var admitted = await repository.AdmitWorkflowInstanceAsync(CheckpointCommit.CreateAdmission(instance), request);
            Assert.Equal(WorkflowCommitStatus.Applied, admitted.Status);
            instance = (await repository.GetWorkflowInstanceAsync(instance.InstanceId))!;
            var grant = await StoreTestSupport.LeaseAsync(repository, instance.InstanceId);
            var opened = await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, grant.Epoch, instance.Revision,
                parks: true, wakeAfter: TimeSpan.FromHours(1), openedWaits: [new("wait", WaitKind.Event, "approval", "Approved", null)]));
            Assert.Equal(WorkflowCommitStatus.Applied, opened.Status);
            await StoreTestSupport.ReleaseAsync(repository, instance.InstanceId);
            var workflowEvent = new WorkflowEvent(instance.InstanceId, "cloud-identity", "Approved", "{\"approved\":true}", "approval",
                DeliveryIdentity: new string('B', 64), Source: "urn:vendor", DeliveryRetention: TimeSpan.FromHours(2));
            var accepted = await repository.AppendEventAsync(workflowEvent);
            Assert.Equal(EventAppendStatus.Stored, accepted.Status);
            Assert.True(accepted.MadeRunnable);
            await DisposeRepositoryAsync(repository);
            repository = database.Repository();
            Assert.Equal(WorkflowOwnerKind.SignedSender, (await repository.GetWorkflowOwnershipAsync(instance.InstanceId))!.OwnerKind);
            var duplicate = await repository.AppendEventAsync(workflowEvent with { PayloadJson = "{ \"approved\": false }", Key = "other" });
            Assert.Equal(EventAppendStatus.Duplicate, duplicate.Status);
            Assert.Equal(accepted.Sequence, duplicate.Sequence);
            Assert.False(duplicate.MadeRunnable);
            Assert.Equal(EventAppendStatus.Conflict, (await repository.AppendEventAsync(workflowEvent with { EventId = "another" })).Status);
            var receipt = await database.Connection.QuerySingleAsync<(DateTime Expires, DateTime Received)>(database.SqlServer
                ? "SELECT d.ExpiresAtUtc, e.ReceivedAtUtc FROM dbo.IxIFlowWebhookDeliveries d CROSS JOIN dbo.IxIFlowWorkflowEvents e"
                : "SELECT d.expires_at_utc, e.received_at_utc FROM ixiflow_webhook_deliveries d CROSS JOIN ixiflow_workflow_events e");
            Assert.Equal(TimeSpan.FromHours(2), receipt.Expires - receipt.Received);
            foreach (var table in new[] { "admissions", "ownership", "deliveries" })
                Assert.Equal(1, await database.Connection.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {database.Table(table)}"));
            await repository.DeleteWorkflowInstanceAsync(instance.InstanceId, TimeSpan.FromHours(1));
            await repository.PurgeAsync(TimeSpan.FromDays(30), TimeSpan.FromDays(90), 100);
            Assert.NotNull(await repository.GetWorkflowOwnershipAsync(instance.InstanceId));
            // Expire on the server, without wall-clock sleeps or changing production clocks.
            var expired = new DateTime(2000, 1, 1);
            foreach (var table in new[] { "admissions", "deliveries", "tombstones" })
                await database.Connection.ExecuteAsync($"UPDATE {database.Table(table)} SET {(database.SqlServer ? "ExpiresAtUtc" : "expires_at_utc")}=@Expired",
                    new { Expired = profile == "postgresql" ? DateTime.SpecifyKind(expired, DateTimeKind.Utc) : expired });
            await repository.PurgeAsync(TimeSpan.FromDays(30), TimeSpan.FromDays(90), 100);
            Assert.Null(await repository.GetWorkflowOwnershipAsync(instance.InstanceId));
            Assert.Null(await repository.GetWorkflowAdmissionAsync(request.Sender, request.Trigger, request.Key));
            foreach (var table in new[] { "admissions", "ownership", "deliveries" })
                Assert.Equal(0, await database.Connection.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {database.Table(table)}"));
        }
        finally { await DisposeRepositoryAsync(repository); }
    }
}
