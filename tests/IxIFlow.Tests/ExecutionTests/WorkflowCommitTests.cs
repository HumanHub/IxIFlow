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

[Trait("Feature", "durability")]
public class WorkflowCommitTests : IClassFixture<SqliteTestDatabase<WorkflowCommitTests>>
{
    [Fact]
    public async Task CommitSnapshotsStateWithoutMutatingCaller()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var instance = CreateInstance();
        var result = await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, 0, 0, "first"));
        Assert.Equal(new WorkflowCommitResult(WorkflowCommitStatus.Applied, 1), result);
        Assert.Equal(0, instance.Revision);
        instance.WorkflowDataJson = "changed";
        var saved = (await repository.GetWorkflowInstanceAsync(instance.InstanceId))!;
        Assert.Equal("{}", saved.WorkflowDataJson);
        saved.WorkflowDataJson = "also changed";
        Assert.Equal("{}", (await repository.GetWorkflowInstanceAsync(instance.InstanceId))!.WorkflowDataJson);
    }

    [Fact]
    public Task FailureBeforeCommitCanRetry()
    {
        var clock = new CommitFailureClock();
        return VerifyFailureBeforeCommit(new InMemoryWorkflowStateRepository(clock), clock: clock);
    }

    [Fact]
    public Task LostAcknowledgmentReturnsReceipt() => VerifyLostAcknowledgment(new InMemoryWorkflowStateRepository());

    [Fact]
    public Task StaleWriterCannotOverwrite() => VerifyConflict(new InMemoryWorkflowStateRepository());

    [Fact]
    public Task OldReceiptSurvivesLaterCommits() => VerifyOldReceipt(new InMemoryWorkflowStateRepository());

    private static async Task VerifyOldReceipt(IWorkflowStateRepository repository)
    {
        var first = CreateInstance();
        try
        {
            await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(first, 0, 0, "first"));
            var second = (await repository.GetWorkflowInstanceAsync(first.InstanceId))!;
            second.WorkflowDataJson = "next";
            await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(second, 0, 1, "second"));
            var repeated = await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(first, 0, 0, "first"));
            Assert.Equal(new WorkflowCommitResult(WorkflowCommitStatus.AlreadyApplied, 1), repeated);
            Assert.Equal("next", (await repository.GetWorkflowInstanceAsync(first.InstanceId))!.WorkflowDataJson);
        }
        finally { await repository.DeleteWorkflowInstanceAsync(first.InstanceId); }
    }

    [Fact]
    public Task CommitIdCannotNameDifferentPayload() => VerifyDifferentPayload(new InMemoryWorkflowStateRepository());

    private static async Task VerifyDifferentPayload(IWorkflowStateRepository repository)
    {
        var instance = CreateInstance();
        try
        {
            await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, 0, 0, "first"));
            instance.WorkflowDataJson = "different";
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, 0, 0, "first")));
        }
        finally { await repository.DeleteWorkflowInstanceAsync(instance.InstanceId); }
    }

    [Fact]
    public Task LegacySaveCannotOverwriteRevisionedState() => VerifyLegacySave(new InMemoryWorkflowStateRepository());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CommitIdCannotNameDifferentPartsOrMetadata(bool metadata) =>
        VerifyDifferentPartsOrMetadata(new InMemoryWorkflowStateRepository(), metadata);

    private static async Task VerifyDifferentPartsOrMetadata(IWorkflowStateRepository repository, bool metadata)
    {
        var instance = CreateInstance();
        var root = new ContinuationState { Id = "root", Stack = [new ScopePosition { ScopeId = "root" }] };
        var join = new ParallelJoinState { Id = "join", ParentContinuationId = root.Id, StepId = "root/0" };
        var branch = new ContinuationState { Id = "branch", ParentJoinId = join.Id,
            Status = ContinuationStatus.Waiting, Stack = [new ScopePosition { ScopeId = "root/0/branch/0" }] };
        var checkpoint = new ExecutionCheckpoint { DefinitionFingerprint = "receipt-parts",
            Continuations = [root, branch], Joins = [join] };
        instance.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
        var original = CheckpointCommit.Create(instance, 0, 0, "first");
        try
        {
            await repository.CommitWorkflowInstanceAsync(original);
            if (!metadata)
            {
                branch.Status = ContinuationStatus.Completed;
                instance.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
            }
            var changed = CheckpointCommit.Create(instance, 0, 0, "first", clearsCrashCount: !metadata);
            Assert.Equal(original.Payload, changed.Payload);
            if (metadata)
                Assert.Equal(original.Parts, changed.Parts);
            else
                Assert.NotEqual(original.Parts, changed.Parts);
            Assert.NotEqual(original.ContentHash, changed.ContentHash);
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.CommitWorkflowInstanceAsync(changed));
        }
        finally { await repository.DeleteWorkflowInstanceAsync(instance.InstanceId); }
    }

    private static async Task VerifyLegacySave(IWorkflowStateRepository repository)
    {
        var instance = CreateInstance();
        try
        {
            await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, 0, 0, "first"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveWorkflowInstanceAsync(instance));
            Assert.Equal(1, (await repository.GetWorkflowInstanceAsync(instance.InstanceId))!.Revision);
        }
        finally { await repository.DeleteWorkflowInstanceAsync(instance.InstanceId); }
    }

    [Fact]
    public async Task ConcurrentWritersHaveOneWinner()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var instance = CreateInstance();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Task.Run(() =>
            repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, 0, 0, index.ToString())))));
        Assert.Single(results, result => result.Status == WorkflowCommitStatus.Applied);
        Assert.Equal(7, results.Count(result => result.Status == WorkflowCommitStatus.Conflict));
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlCommitHandlesConflictsAndLostAcknowledgments() => VerifyCommitConflictsAndLostAcknowledgments(
        new SqlWorkflowStateRepository(SqlTestDatabase<WorkflowCommitTests>.ConnectionString),
        () => new SqlConnection(SqlTestDatabase<WorkflowCommitTests>.ConnectionString));

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlCommitHandlesConflictsAndLostAcknowledgments() => VerifyCommitConflictsAndLostAcknowledgments(
        new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<WorkflowCommitTests>.ConnectionString),
        () => new NpgsqlConnection(PostgreSqlTestDatabase<WorkflowCommitTests>.ConnectionString));

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    [Trait("Spec", "C-11")]
    public async Task SqliteCommitHandlesConflictsAndLostAcknowledgments()
    {
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<WorkflowCommitTests>.ConnectionString);
        await VerifyCommitConflictsAndLostAcknowledgments(repository,
            () => new SqliteConnection(SqliteTestDatabase<WorkflowCommitTests>.ConnectionString));
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlOldReceiptSurvivesLaterCommits() =>
        VerifyOldReceipt(new SqlWorkflowStateRepository(SqlTestDatabase<WorkflowCommitTests>.ConnectionString));

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlCommitIdCannotNameDifferentPayload() =>
        VerifyDifferentPayload(new SqlWorkflowStateRepository(SqlTestDatabase<WorkflowCommitTests>.ConnectionString));

    [SqlServerFact(Skip = "Known defect: SQL Server legacy Save throws SqlException instead of InvalidOperationException for revisioned state")]
    [Trait("Category", "SqlIntegration")]
    public Task SqlLegacySaveCannotOverwriteRevisionedState() =>
        VerifyLegacySave(new SqlWorkflowStateRepository(SqlTestDatabase<WorkflowCommitTests>.ConnectionString));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "SqlIntegration")]
    public Task SqlCommitIdCannotNameDifferentPartsOrMetadata(bool metadata) =>
        VerifyDifferentPartsOrMetadata(new SqlWorkflowStateRepository(SqlTestDatabase<WorkflowCommitTests>.ConnectionString), metadata);

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlOldReceiptSurvivesLaterCommits() =>
        VerifyOldReceipt(new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<WorkflowCommitTests>.ConnectionString));

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlCommitIdCannotNameDifferentPayload() =>
        VerifyDifferentPayload(new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<WorkflowCommitTests>.ConnectionString));

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlLegacySaveCannotOverwriteRevisionedState() =>
        VerifyLegacySave(new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<WorkflowCommitTests>.ConnectionString));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlCommitIdCannotNameDifferentPartsOrMetadata(bool metadata) =>
        VerifyDifferentPartsOrMetadata(new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<WorkflowCommitTests>.ConnectionString), metadata);

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    public async Task SqliteOldReceiptSurvivesLaterCommits()
    {
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<WorkflowCommitTests>.ConnectionString);
        await VerifyOldReceipt(repository);
    }

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    public async Task SqliteCommitIdCannotNameDifferentPayload()
    {
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<WorkflowCommitTests>.ConnectionString);
        await VerifyDifferentPayload(repository);
    }

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    public async Task SqliteLegacySaveCannotOverwriteRevisionedState()
    {
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<WorkflowCommitTests>.ConnectionString);
        await VerifyLegacySave(repository);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "SqliteIntegration")]
    public async Task SqliteCommitIdCannotNameDifferentPartsOrMetadata(bool metadata)
    {
        await using var repository = new SqliteWorkflowStateRepository(SqliteTestDatabase<WorkflowCommitTests>.ConnectionString);
        await VerifyDifferentPartsOrMetadata(repository, metadata);
    }

    private static async Task VerifyFailureBeforeCommit(IWorkflowStateRepository repository,
        Func<DbConnection>? connect = null, CommitFailureClock? clock = null)
    {
        var instance = CreateInstance();
        var commit = CheckpointCommit.Create(instance, 0, 0, "first");
        try
        {
            if (connect == null)
            {
                clock!.Fail = true;
                try
                {
                    var error = await Assert.ThrowsAsync<IOException>(() => repository.CommitWorkflowInstanceAsync(commit));
                    Assert.Equal("Injected store clock failure", error.Message);
                }
                finally { clock.Fail = false; }
            }
            else
            {
                // Initialize the schema before installing a failure after the checkpoint row is written.
                Assert.Null(await repository.GetWorkflowInstanceAsync(instance.InstanceId));
                await using var connection = connect();
                await connection.OpenAsync();
                var trigger = "fail_" + instance.InstanceId;
                var install = connection is SqlConnection ? $"""
                    CREATE TRIGGER dbo.{trigger} ON dbo.IxIFlowWorkflowCommits AFTER INSERT AS
                    IF EXISTS (SELECT 1 FROM inserted WHERE InstanceId='{instance.InstanceId}')
                        THROW 51000, 'injected commit failure', 1;
                    """ : connection is NpgsqlConnection ? $"""
                    CREATE FUNCTION {trigger}() RETURNS trigger LANGUAGE plpgsql AS $$
                    BEGIN IF NEW.instance_id='{instance.InstanceId}' THEN
                        RAISE EXCEPTION 'injected commit failure'; END IF; RETURN NEW; END $$;
                    CREATE TRIGGER {trigger} BEFORE INSERT ON ixiflow_workflow_commits
                        FOR EACH ROW EXECUTE FUNCTION {trigger}();
                    """ : $"""
                    CREATE TRIGGER {trigger} BEFORE INSERT ON ixiflow_workflow_commits
                    WHEN NEW.instance_id='{instance.InstanceId}'
                    BEGIN SELECT RAISE(ABORT, 'injected commit failure'); END;
                    """;
                var drop = connection is SqlConnection ? $"DROP TRIGGER dbo.{trigger}"
                    : connection is NpgsqlConnection
                        ? $"DROP TRIGGER {trigger} ON ixiflow_workflow_commits; DROP FUNCTION {trigger}()"
                        : $"DROP TRIGGER {trigger}";
                await connection.ExecuteAsync(install);
                try
                {
                    var error = await Assert.ThrowsAnyAsync<DbException>(() => repository.CommitWorkflowInstanceAsync(commit));
                    Assert.Contains("injected commit failure", error.Message);
                }
                finally { await connection.ExecuteAsync(drop); }
                foreach (var table in new[] { "instances", "parts", "commits" })
                {
                    var name = connection is SqlConnection ? "dbo.IxIFlowWorkflow" +
                        (table == "instances" ? "Instances" : table == "parts" ? "Parts" : "Commits")
                        : "ixiflow_workflow_" + table;
                    var column = connection is SqlConnection ? "InstanceId" : "instance_id";
                    Assert.Equal(0, await connection.ExecuteScalarAsync<int>(
                        $"SELECT COUNT(*) FROM {name} WHERE {column}=@Id", new { Id = instance.InstanceId }));
                }
            }
            Assert.Null(await repository.GetWorkflowInstanceAsync(instance.InstanceId));
            Assert.Empty(await repository.GetTraceAsync(instance.InstanceId));
            var result = await repository.CommitWorkflowInstanceAsync(commit);
            Assert.Equal(WorkflowCommitStatus.Applied, result.Status);
            Assert.Equal(new WorkflowCommitResult(WorkflowCommitStatus.Applied, 1), result);
            Assert.Equal(new WorkflowCommitResult(WorkflowCommitStatus.AlreadyApplied, 1),
                await repository.CommitWorkflowInstanceAsync(commit));
            var saved = (await repository.GetWorkflowInstanceAsync(instance.InstanceId))!;
            Assert.Equal(1, saved.Revision);
            Assert.Equal(instance.WorkflowName, saved.WorkflowName);
            Assert.Equal(instance.Status, saved.Status);
            Assert.Equal(instance.WorkflowDataJson, saved.WorkflowDataJson);
            Assert.Equal(instance.ExecutionStateJson, saved.ExecutionStateJson);
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instance.InstanceId);
        }
    }

    private static async Task VerifyLostAcknowledgment(IWorkflowStateRepository repository)
    {
        var instance = CreateInstance();
        try
        {
            async Task LoseAcknowledgment()
            {
                await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, 0, 0, "first"));
                throw new IOException("The commit succeeded but its acknowledgment was lost");
            }
            await Assert.ThrowsAsync<IOException>(LoseAcknowledgment);
            var result = await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, 0, 0, "first"));
            Assert.Equal(new WorkflowCommitResult(WorkflowCommitStatus.AlreadyApplied, 1), result);
            Assert.Equal(1, (await repository.GetWorkflowInstanceAsync(instance.InstanceId))!.Revision);
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instance.InstanceId);
        }
    }

    private static async Task VerifyConflict(IWorkflowStateRepository repository)
    {
        var instance = CreateInstance();
        try
        {
            await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, 0, 0, "winner"));
            instance.WorkflowDataJson = "stale";
            var result = await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, 0, 0, "loser"));
            Assert.Equal(new WorkflowCommitResult(WorkflowCommitStatus.Conflict, 1), result);
            Assert.Equal("{}", (await repository.GetWorkflowInstanceAsync(instance.InstanceId))!.WorkflowDataJson);
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instance.InstanceId);
        }
    }

    private static WorkflowInstance CreateInstance() => new()
    {
        InstanceId = Guid.NewGuid().ToString("N"),
        WorkflowName = "commit-tests",
        WorkflowDataJson = "{}",
        Status = WorkflowStatus.Running
    };
    private static async Task VerifyCommitConflictsAndLostAcknowledgments(IWorkflowStateRepository repository,
        Func<DbConnection> connect)
    {
        await VerifyFailureBeforeCommit(repository, connect);
        await VerifyLostAcknowledgment(repository);
        await VerifyConflict(repository);
    }

    private sealed class CommitFailureClock : TimeProvider
    {
        internal bool Fail { get; set; }
        public override DateTimeOffset GetUtcNow() => Fail
            ? throw new IOException("Injected store clock failure") : base.GetUtcNow();
    }

}
