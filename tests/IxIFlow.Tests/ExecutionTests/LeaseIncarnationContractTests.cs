using System.Data.Common;
using System.Text.Json;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
[Trait("Spec", "A-03")]
[Trait("Spec", "H-05")]
public sealed class LeaseIncarnationContractTests
{
    [Fact]
    public Task MemoryFirstCommitsAdoptIncarnationAndLaterCommitsRemainFenced()
    {
        var time = new FakeTimeProvider();
        return VerifyFirstCommits(new InMemoryWorkflowStateRepository(time),
            (_, by) => { time.Advance(by); return Task.CompletedTask; });
    }

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    public async Task SqliteFirstCommitsAdoptIncarnationAndLaterCommitsRemainFenced()
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.LeaseIncarnation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var time = new FakeTimeProvider();
            await using var store = new SqliteWorkflowStateRepository(
                "Data Source=" + Path.Combine(folder, "first-commits.db"), time);
            await VerifyFirstCommits(store, (_, by) => { time.Advance(by); return Task.CompletedTask; });
        }
        finally { await SqliteTestFiles.DeleteAsync(folder); }
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlServerFirstCommitsAdoptIncarnationAndLaterCommitsRemainFenced()
    {
        var connectionString = SqlTestDatabase<LeaseIncarnationContractTests>.ConnectionString;
        return VerifyFirstCommits(new SqlWorkflowStateRepository(connectionString),
            (id, by) => AgeRows(() => new SqlConnection(connectionString), id, by));
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlFirstCommitsAdoptIncarnationAndLaterCommitsRemainFenced()
    {
        var connectionString = PostgreSqlTestDatabase<LeaseIncarnationContractTests>.ConnectionString;
        return VerifyFirstCommits(new PostgreSqlWorkflowStateRepository(connectionString),
            (id, by) => AgeRows(() => new NpgsqlConnection(connectionString), id, by));
    }

    private static async Task VerifyFirstCommits(IWorkflowStateRepository store,
        Func<string, TimeSpan, Task> advance)
    {
        var duration = TimeSpan.FromMinutes(1);
        var tombstones = TimeSpan.FromDays(1);
        foreach (var fromJson in new[] { true, false })
        {
            var id = Guid.NewGuid().ToString("N");
            try
            {
                var saved = NewInstance(id);
                saved.Incarnation = Guid.NewGuid();
                var caller = fromJson
                    ? JsonSerializer.Deserialize<WorkflowInstance>(JsonSerializer.Serialize(saved))!
                    : saved.CopyForResume();
                Assert.Equal(0, caller.Revision);
                var savedIncarnation = caller.Incarnation;
                var epoch = (await store.TryAcquireExecutionLeaseAsync(id, "owner", duration, new ClaimQuery(Guid.Empty, new Dictionary<string, string>())))!.Epoch;
                var first = CheckpointCommit.Create(caller, epoch, 0);
                Assert.NotEqual(savedIncarnation, first.Incarnation);
                Assert.Null(await store.GetWorkflowInstanceAsync(id));
                await Apply(store, first);
                Assert.Equal(first.Incarnation, caller.Incarnation);
                Assert.Equal(WorkflowCommitStatus.AlreadyApplied,
                    (await store.CommitWorkflowInstanceAsync(first)).Status);

                // A different first commit conflicts without changing the committed incarnation.
                var contender = caller.CopyForResume();
                contender.WorkflowDataJson = StoredJson.Inline("{\"contender\":true}");
                var different = CheckpointCommit.Create(contender, epoch, 0);
                Assert.NotEqual(first.Incarnation, different.Incarnation);
                CommitAssert.Equal(new WorkflowCommitResult(WorkflowCommitStatus.Conflict, 1),
                    await store.CommitWorkflowInstanceAsync(different));
                Assert.Equal(first.Incarnation, (await store.GetWorkflowInstanceAsync(id))!.Incarnation);

                // The original caller carries the adopted incarnation into its next commit.
                caller.Revision = 1;
                var next = CheckpointCommit.Create(caller, epoch, caller.Revision);
                Assert.Equal(first.Incarnation, next.Incarnation);
                await Apply(store, next);
                Assert.Equal(2, (await store.GetWorkflowInstanceAsync(id))!.Revision);

                // Re-creation starts a new incarnation at revision one, under a later epoch.
                var oldEpoch = epoch;
                var stale = new[]
                {
                    CheckpointCommit.Create(caller, oldEpoch, 1),
                    CheckpointCommit.Create(caller, oldEpoch, 2)
                };
                await store.DeleteWorkflowInstanceAsync(id, tombstones);
                await advance(id, tombstones + TimeSpan.FromDays(1));
                var replacement = NewInstance(id);
                var grant = (await store.TryAcquireExecutionLeaseAsync(id, "replacement", duration, new ClaimQuery(Guid.Empty, new Dictionary<string, string>())))!;
                Assert.True(grant.Epoch > oldEpoch);
                var replacementFirst = CheckpointCommit.Create(replacement, grant.Epoch, 0);
                await Apply(store, replacementFirst);
                Assert.NotEqual(first.Incarnation, replacementFirst.Incarnation);
                foreach (var late in stale)
                {
                    CommitAssert.Equal(new WorkflowCommitResult(WorkflowCommitStatus.StaleEpoch, 1),
                        await store.CommitWorkflowInstanceAsync(late));
                    var unchanged = (await store.GetWorkflowInstanceAsync(id))!;
                    Assert.Equal(1, unchanged.Revision);
                    Assert.Equal(replacementFirst.Incarnation, unchanged.Incarnation);
                    Assert.True(await store.RenewExecutionLeaseAsync(id, "replacement", duration));
                }
                await Apply(store, CheckpointCommit.Create(replacement, grant.Epoch, 1));
            }
            finally { await store.DeleteWorkflowInstanceAsync(id); }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task MemoryDeletionRemovesLeaseAndFencesIncarnation(bool purge)
    {
        var time = new FakeTimeProvider();
        return Verify(new InMemoryWorkflowStateRepository(time), purge,
            (_, by) => { time.Advance(by); return Task.CompletedTask; });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "SqliteIntegration")]
    public async Task SqliteDeletionRemovesLeaseAndFencesIncarnation(bool purge)
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.LeaseIncarnation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var connectionString = "Data Source=" + Path.Combine(folder, "incarnation.db");
        try
        {
            var time = new FakeTimeProvider();
            await using var store = new SqliteWorkflowStateRepository(connectionString, time);
            await Verify(store, purge, (_, by) => { time.Advance(by); return Task.CompletedTask; },
                () => new SqliteConnection(new SqliteConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString));
        }
        finally { await SqliteTestFiles.DeleteAsync(folder); }
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlServerDeletionRemovesLeaseAndFencesIncarnation() => VerifySqlServer(false);

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlServerPurgeRemovesLeaseAndFencesIncarnation() => VerifySqlServer(true);

    private static Task VerifySqlServer(bool purge)
    {
        var connectionString = SqlTestDatabase<LeaseIncarnationContractTests>.ConnectionString;
        return Verify(new SqlWorkflowStateRepository(connectionString), purge,
            (id, by) => AgeRows(() => new SqlConnection(connectionString), id, by),
            () => new SqlConnection(connectionString));
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlDeletionRemovesLeaseAndFencesIncarnation() => VerifyPostgreSql(false);

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlPurgeRemovesLeaseAndFencesIncarnation() => VerifyPostgreSql(true);

    private static Task VerifyPostgreSql(bool purge)
    {
        var connectionString = PostgreSqlTestDatabase<LeaseIncarnationContractTests>.ConnectionString;
        return Verify(new PostgreSqlWorkflowStateRepository(connectionString), purge,
            (id, by) => AgeRows(() => new NpgsqlConnection(connectionString), id, by),
            () => new NpgsqlConnection(connectionString));
    }

    private static async Task Verify(IWorkflowStateRepository store, bool purge,
        Func<string, TimeSpan, Task> advance, Func<DbConnection>? open = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var retention = TimeSpan.FromDays(30);
        var tombstones = TimeSpan.FromDays(90);
        var duration = TimeSpan.FromMinutes(1);
        try
        {
            await VerifyAbsentDelete(store, id + "-absent", duration, open);
            await VerifyFirstInsertReplay(store, id + "-retry", duration, advance);
            var original = NewInstance(id);
            await Apply(store, CheckpointCommit.Create(original, (await StoreTestSupport.LeaseAsync(store, original.InstanceId)).Epoch, 0));
            await StoreTestSupport.ReleaseAsync(store, id);
            original = (await store.GetWorkflowInstanceAsync(id))!;
            var originalIncarnation = original.Incarnation;
            Assert.NotEqual(Guid.Empty, originalIncarnation);
            var oldFirst = (await store.TryAcquireExecutionLeaseAsync(id, "old-first", duration, new ClaimQuery(Guid.Empty, new Dictionary<string, string>())))!;
            Assert.True(oldFirst.Epoch > 0);
            await advance(id, TimeSpan.FromMinutes(2));
            var oldGrant = (await store.TryAcquireExecutionLeaseAsync(id, "old-owner", duration, new ClaimQuery(Guid.Empty, new Dictionary<string, string>())))!;
            Assert.True(oldGrant.Epoch > oldFirst.Epoch);
            Assert.Equal(1, oldGrant.CrashCount);

            // These all name revision one and the old owner's epoch. The completed one is also a receipt replay.
            var lateRunning = CheckpointCommit.Create(original, oldGrant.Epoch, original.Revision);
            var latePark = CheckpointCommit.Create(original, oldGrant.Epoch, original.Revision,
                parks: true, wakeAfter: TimeSpan.FromHours(1));
            original.Status = WorkflowStatus.Completed;
            var lateCompletion = CheckpointCommit.Create(original, oldGrant.Epoch, original.Revision, "completion");
            await Apply(store, lateCompletion);
            await AssertLeaseRows(open, id, 1);

            if (purge)
            {
                await advance(id, retention + TimeSpan.FromDays(1));
                Assert.Equal(1, (await store.PurgeAsync(retention, tombstones, 100)).InstancesRemoved);
            }
            else
                await store.DeleteWorkflowInstanceAsync(id, tombstones);
            Assert.Null(await store.GetWorkflowInstanceAsync(id));
            await AssertLeaseRows(open, id, 0);
            var tombstone = await store.GetTombstoneAsync(id);
            Assert.NotNull(tombstone);
            Assert.Null(await store.TryAcquireExecutionLeaseAsync(id, "tombstoned", duration, new ClaimQuery(Guid.Empty, new Dictionary<string, string>())));
            await store.DeleteWorkflowInstanceAsync(id, tombstones);
            Assert.Equal(tombstone, await store.GetTombstoneAsync(id));
            await AssertLeaseRows(open, id, 0);

            // Reuse becomes legal when the tombstone expires, without a wall-clock sleep.
            await advance(id, tombstones + TimeSpan.FromDays(1));
            await store.PurgeAsync(retention, tombstones, 100);
            // A first commit must allocate a new incarnation even from the old instance object.
            var replacement = original.CopyForResume();
            replacement.Status = WorkflowStatus.Running;
            replacement.Revision = 0;
            Assert.Equal(originalIncarnation, replacement.Incarnation);
            var firstInsert = CheckpointCommit.Create(replacement, (await StoreTestSupport.LeaseAsync(store, replacement.InstanceId)).Epoch, 0);
            Assert.NotEqual(originalIncarnation, firstInsert.Incarnation);
            Assert.Equal(firstInsert.Incarnation, replacement.Incarnation);
            await Apply(store, firstInsert);
            await StoreTestSupport.ReleaseAsync(store, id);
            replacement = (await store.GetWorkflowInstanceAsync(id))!;
            Assert.Equal(firstInsert.Incarnation, replacement.Incarnation);
            Assert.NotEqual(originalIncarnation, replacement.Incarnation);
            Assert.Equal(original.Revision, replacement.Revision);
            Assert.Equal(replacement.Incarnation, (await store.GetWorkflowInstanceSummaryAsync(id))!.Incarnation);
            Assert.Equal(replacement.Incarnation,
                Assert.Single(await Task.WhenAll((await store.QueryInstancesAsync(new InstanceQuery(WorkflowName: replacement.WorkflowName, Limit: int.MaxValue))).Items.Select(async row => (await store.GetWorkflowInstanceAsync(row.InstanceId))!))).Incarnation);

            var first = (await store.TryAcquireExecutionLeaseAsync(id, "new-first", duration, new ClaimQuery(Guid.Empty, new Dictionary<string, string>())))!;
            Assert.True(first.Epoch > oldGrant.Epoch);
            Assert.Equal(0, first.CrashCount);
            Assert.True((await store.ReleaseExecutionLeaseAsync(id, "new-first")).Released);
            var current = (await store.TryAcquireExecutionLeaseAsync(id, "new-owner", duration, new ClaimQuery(Guid.Empty, new Dictionary<string, string>())))!;
            Assert.True(current.Epoch > first.Epoch);
            Assert.Equal(0, current.CrashCount);
            await VerifyStaleEffects(store, original, replacement, oldGrant.Epoch, duration);

            foreach (var late in new[] { lateRunning, latePark, lateCompletion })
            {
                var result = await store.CommitWorkflowInstanceAsync(late);
                Assert.Equal(WorkflowCommitStatus.StaleEpoch, result.Status);
                Assert.Equal(replacement.Revision, result.Revision);
                Assert.True(await store.RenewExecutionLeaseAsync(id, "new-owner", duration));
                var unchanged = (await store.GetWorkflowInstanceAsync(id))!;
                Assert.Equal(replacement.Revision, unchanged.Revision);
                Assert.Equal(WorkflowStatus.Running, unchanged.Status);
                Assert.Equal(replacement.Incarnation, unchanged.Incarnation);
            }
            Assert.False((await store.ReleaseExecutionLeaseAsync(id, "old-owner")).Released);
            Assert.False(await store.RenewExecutionLeaseAsync(id, "old-owner", duration));

            // A leased commit after the first must also name the row's incarnation.
            var wrongIncarnation = NewInstance(id);
            wrongIncarnation.Status = WorkflowStatus.Completed;
            Assert.Equal(WorkflowCommitStatus.StaleEpoch,
                (await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(wrongIncarnation, current.Epoch, replacement.Revision))).Status);
            var untouched = (await store.GetWorkflowInstanceAsync(id))!;
            Assert.Equal(replacement.Revision, untouched.Revision);
            Assert.Equal(replacement.WorkflowDataJson, untouched.WorkflowDataJson);
            Assert.Equal(replacement.Incarnation, untouched.Incarnation);

            // A present receipt belongs to the replacement. A reused ID with different transition
            // content is rejected by receipt validation before the incarnation fence.
            replacement.Status = WorkflowStatus.Completed;
            var normal = CheckpointCommit.Create(replacement, current.Epoch, replacement.Revision, "completion");
            await Apply(store, normal);
            Assert.Equal(WorkflowCommitStatus.AlreadyApplied, (await store.CommitWorkflowInstanceAsync(normal)).Status);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitWorkflowInstanceAsync(lateCompletion));
            var finished = (await store.GetWorkflowInstanceAsync(id))!;
            Assert.Equal(2, finished.Revision);
            Assert.Equal(WorkflowStatus.Completed, finished.Status);
            Assert.Equal(replacement.Incarnation, finished.Incarnation);

            // A first commit also allocates a fresh incarnation when reusing an old object to create a row.
            await store.DeleteWorkflowInstanceAsync(id, tombstones);
            await AssertLeaseRows(open, id, 0);
            await advance(id, tombstones + TimeSpan.FromDays(1));
            await store.PurgeAsync(retention, tombstones, 100);
            original.Revision = 0;
            original.Status = WorkflowStatus.Running;
            await store.SeedAsync(original);
            var saved = (await store.GetWorkflowInstanceAsync(id))!;
            Assert.Equal(original.Incarnation, saved.Incarnation);
            Assert.NotEqual(originalIncarnation, saved.Incarnation);
            Assert.NotEqual(replacement.Incarnation, saved.Incarnation);
            var update = saved.CopyForResume();
            await store.SeedAsync(update);
            Assert.Equal(saved.Incarnation, update.Incarnation);
            Assert.Equal(saved.Incarnation, (await store.GetWorkflowInstanceAsync(id))!.Incarnation);
            await VerifyJsonAndChildReuse(store, saved, advance, tombstones);
        }
        finally { await store.DeleteWorkflowInstanceAsync(id); }
    }

    private static WorkflowInstance NewInstance(string id) => new()
    { ExecutionStateJson = StoreTestSupport.CheckpointJson,
        InstanceId = id, WorkflowName = "LeaseIncarnation" + id,
        Status = WorkflowStatus.Running, WorkflowDataJson = StoredJson.Inline("{}")
    };

    private static async Task Apply(IWorkflowStateRepository store, CheckpointCommit commit)
    {
        Assert.DoesNotContain("\"Incarnation\"", commit.Payload);
        var result = await store.CommitWorkflowInstanceAsync(commit);
        Assert.Equal(WorkflowCommitStatus.Applied, result.Status);
        var row = (await store.GetWorkflowInstanceAsync(commit.InstanceId))!;
        Assert.NotEqual(Guid.Empty, row.Incarnation);
        if (commit.ExpectedRevision == 0)
            Assert.Equal(commit.Incarnation, row.Incarnation);
    }

    private static async Task VerifyFirstInsertReplay(IWorkflowStateRepository store, string prefix, TimeSpan duration,
        Func<string, TimeSpan, Task> advance)
    {
        foreach (var kind in new[] { "running", "parking", "completed" })
        {
            var id = prefix + "-" + kind;
            try
            {
                var instance = NewInstance(id);
                var grant = (await store.TryAcquireExecutionLeaseAsync(id, "owner", duration, new ClaimQuery(Guid.Empty, new Dictionary<string, string>())))!;
                if (kind == "completed")
                    instance.Status = WorkflowStatus.Completed;
                var commit = CheckpointCommit.Create(instance, grant.Epoch, 0,
                    parks: kind == "parking", wakeAfter: kind == "parking" ? TimeSpan.FromHours(1) : null);
                Assert.NotEqual(Guid.Empty, commit.Incarnation);
                Assert.Equal(commit.Incarnation, instance.Incarnation);
                var applied = await store.CommitWorkflowInstanceAsync(commit); // The acknowledgement is lost.
                CommitAssert.Equal(new WorkflowCommitResult(WorkflowCommitStatus.Applied, 1,
                    Parked: kind == "parking"), applied with { Notice = null, MadeRunnable = [] });
                Assert.Empty(applied.MadeRunnable);
                if (kind == "completed")
                {
                    Assert.NotNull(applied.Notice);
                    Assert.Equal(id, applied.Notice.InstanceId);
                    Assert.True(applied.Notice.Idle);
                    Assert.False(applied.Notice.Claimable);
                }
                else
                    Assert.Null(applied.Notice);
                await CommitAssert.SchedulingAsync(store, id, applied);
                await advance(id, TimeSpan.FromSeconds(1));
                await Task.Delay(20); // Separate server clock samples too.
                var receipt = await store.CommitWorkflowInstanceAsync(commit); // Resend the same immutable commit.
                CommitAssert.Equal(applied with { Status = WorkflowCommitStatus.AlreadyApplied }, receipt);
                await CommitAssert.SchedulingAsync(store, id, receipt);
                Assert.Equal(applied.RunnableAtUtc, receipt.RunnableAtUtc);
                Assert.True(receipt.StoreTimeUtc > applied.StoreTimeUtc);
                var row = (await store.GetWorkflowInstanceAsync(id))!;
                Assert.Equal(1, row.Revision);
                Assert.Equal(commit.Incarnation, row.Incarnation);
                Assert.Equal(commit.Incarnation, instance.Incarnation);

                // Even a newly created first-insert transition with identical content gets the
                // existing receipt before its newly stamped incarnation is checked.
                var recreated = CheckpointCommit.Create(instance, grant.Epoch, 0, commit.CommitId,
                    parks: commit.Parks, wakeAfter: commit.WakeAfter);
                Assert.NotEqual(commit.Incarnation, recreated.Incarnation);
                Assert.Equal(recreated.Incarnation, instance.Incarnation);
                Assert.Equal(commit.ContentHash, recreated.ContentHash);
                await advance(id, TimeSpan.FromSeconds(1));
                await Task.Delay(20);
                var recreatedReceipt = await store.CommitWorkflowInstanceAsync(recreated);
                CommitAssert.Equal(receipt, recreatedReceipt);
                await CommitAssert.SchedulingAsync(store, id, recreatedReceipt);
                Assert.Equal(receipt.RunnableAtUtc, recreatedReceipt.RunnableAtUtc);
                Assert.True(recreatedReceipt.StoreTimeUtc > receipt.StoreTimeUtc);
                Assert.Equal(commit.Incarnation, (await store.GetWorkflowInstanceAsync(id))!.Incarnation);
            }
            finally { await store.DeleteWorkflowInstanceAsync(id); }
        }
    }

    private static async Task AssertLeaseRows(Func<DbConnection>? open, string id, int expected)
    {
        if (open == null)
            return; // In-memory deletion is proved by the subsequent epoch and crash-count assertions.
        await using var connection = open();
        Assert.Equal(expected, await connection.ExecuteScalarAsync<int>(connection is SqlConnection
            ? "SELECT COUNT(*) FROM dbo.IxIFlowWorkflowLeases WHERE InstanceId=@Id"
            : "SELECT COUNT(*) FROM ixiflow_workflow_leases WHERE instance_id=@Id", new { Id = id }));
    }

    private static async Task VerifyAbsentDelete(IWorkflowStateRepository store, string id, TimeSpan duration,
        Func<DbConnection>? open)
    {
        try
        {
            var orphan = (await store.TryAcquireExecutionLeaseAsync(id, "orphan", duration, new ClaimQuery(Guid.Empty, new Dictionary<string, string>())))!;
            Assert.True(orphan.Epoch > 0);
            await store.DeleteWorkflowInstanceAsync(id);
            await AssertLeaseRows(open, id, 0);
            Assert.Null(await store.GetTombstoneAsync(id));
            Assert.False(await store.RenewExecutionLeaseAsync(id, "orphan", duration));
            Assert.False((await store.ReleaseExecutionLeaseAsync(id, "orphan")).Released);
            var grant = (await store.TryAcquireExecutionLeaseAsync(id, "after-delete", duration, new ClaimQuery(Guid.Empty, new Dictionary<string, string>())))!;
            Assert.True(grant.Epoch > orphan.Epoch);
            Assert.Equal(0, grant.CrashCount);
        }
        finally { await store.DeleteWorkflowInstanceAsync(id); }
    }

    private static async Task VerifyStaleEffects(IWorkflowStateRepository store, WorkflowInstance original,
        WorkflowInstance replacement, long epoch, TimeSpan duration)
    {
        var parentId = original.InstanceId + "-parent";
        var childId = original.InstanceId + "-child";
        var parent = NewInstance(parentId);
        parent.Status = WorkflowStatus.Suspended;
        try
        {
            await Apply(store, CheckpointCommit.Create(parent, (await StoreTestSupport.LeaseAsync(store, parent.InstanceId)).Epoch, 0,
                openedWaits: [new OpenedWait("notice", WaitKind.Child, "", "child-result", null)]));
            var parentBefore = (await store.GetWorkflowInstanceAsync(parentId))!;
            var parentWait = await store.GetWaitAsync(parentId, "notice");
            var sequence = (await store.GetInboxSequencesAsync([parentId]))[parentId];
            var replacementBefore = JsonSerializer.Serialize(await store.GetWorkflowInstanceAsync(replacement.InstanceId));
            var child = NewInstance(childId);
            child.ParentInstanceId = original.InstanceId;
            child.ParentWaitId = "late-child";
            var withChild = original.CopyForResume();
            withChild.Status = WorkflowStatus.Running;
            var childCommit = CheckpointCommit.Create(withChild, epoch, replacement.Revision,
                openedWaits: [new OpenedWait("late-child", WaitKind.Child, "", "child-result", null)],
                childStarts: [new ChildStart(childId, JsonSerializer.Serialize(child), "late-child")]);
            var withNotice = original.CopyForResume();
            withNotice.Status = WorkflowStatus.Completed;
            withNotice.ParentInstanceId = parentId;
            withNotice.ParentWaitId = "notice";
            var noticeCommit = CheckpointCommit.Create(withNotice, epoch, replacement.Revision,
                parentNotice: new ParentNotice(parentId, "notice", "late-notice", "child-result", "{}"));
            foreach (var late in new[] { childCommit, noticeCommit })
            {
                var result = await store.CommitWorkflowInstanceAsync(late);
                Assert.Equal(WorkflowCommitStatus.StaleEpoch, result.Status);
                Assert.Equal(replacement.Incarnation,
                    (await store.GetWorkflowInstanceAsync(replacement.InstanceId))!.Incarnation);
                Assert.Empty(result.MadeRunnable);
                Assert.Equal(replacementBefore, JsonSerializer.Serialize(await store.GetWorkflowInstanceAsync(replacement.InstanceId)));
                Assert.Null(await store.GetWorkflowInstanceAsync(childId));
                Assert.Null(await store.GetWaitAsync(replacement.InstanceId, "late-child"));
                Assert.Null(await store.GetEventAsync(parentId, "late-notice"));
                Assert.Equal(parentWait, await store.GetWaitAsync(parentId, "notice"));
                Assert.Equal(sequence, (await store.GetInboxSequencesAsync([parentId]))[parentId]);
                Assert.Equal(JsonSerializer.Serialize(parentBefore), JsonSerializer.Serialize(await store.GetWorkflowInstanceAsync(parentId)));
                Assert.True(await store.RenewExecutionLeaseAsync(replacement.InstanceId, "new-owner", duration));
            }
        }
        finally
        {
            await store.DeleteWorkflowInstanceAsync(childId);
            await store.DeleteWorkflowInstanceAsync(parentId);
        }
    }

    private static async Task VerifyJsonAndChildReuse(IWorkflowStateRepository store, WorkflowInstance saved,
        Func<string, TimeSpan, Task> advance, TimeSpan tombstones)
    {
        // Simulate old exported state, including a forged incarnation from before JsonIgnore was added.
        var json = JsonSerializer.SerializeToNode(saved)!;
        Assert.Null(json[nameof(WorkflowInstance.Incarnation)]);
        json[nameof(WorkflowInstance.Incarnation)] = saved.Incarnation;
        var incarnations = new HashSet<Guid> { saved.Incarnation };
        foreach (var releaseOwner in new[] { false, true })
        {
            await store.DeleteWorkflowInstanceAsync(saved.InstanceId, tombstones);
            await advance(saved.InstanceId, tombstones + TimeSpan.FromDays(1));
            var reused = JsonSerializer.Deserialize<WorkflowInstance>(json.ToJsonString())!;
            Assert.Equal(Guid.Empty, reused.Incarnation);
            reused.Revision = 0;
            if (releaseOwner)
                await store.SeedAsync(reused);
            else
                await Apply(store, CheckpointCommit.Create(reused, (await StoreTestSupport.LeaseAsync(store, reused.InstanceId)).Epoch, 0));
            var row = (await store.GetWorkflowInstanceAsync(saved.InstanceId))!;
            Assert.True(incarnations.Add(row.Incarnation));
            Assert.Equal(row.Incarnation, reused.Incarnation);
        }

        var parentId = saved.InstanceId + "-start";
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await store.DeleteWorkflowInstanceAsync(saved.InstanceId, tombstones);
                await advance(saved.InstanceId, tombstones + TimeSpan.FromDays(1));
                var parent = NewInstance(parentId);
                var child = JsonSerializer.Deserialize<WorkflowInstance>(json.ToJsonString())!;
                child.ParentInstanceId = parentId;
                child.ParentWaitId = "child";
                var payload = JsonSerializer.SerializeToNode(child)!;
                payload[nameof(WorkflowInstance.Incarnation)] = saved.Incarnation;
                await Apply(store, CheckpointCommit.Create(parent, (await StoreTestSupport.LeaseAsync(store, parent.InstanceId)).Epoch, 0,
                    openedWaits: [new OpenedWait("child", WaitKind.Child, "", "child-result", null)],
                    childStarts: [new ChildStart(child.InstanceId, payload.ToJsonString(), "child")]));
                var row = (await store.GetWorkflowInstanceAsync(child.InstanceId))!;
                Assert.True(incarnations.Add(row.Incarnation));
                Assert.Equal(row.Incarnation, Assert.Single(await Task.WhenAll((await store.QueryInstancesAsync(new InstanceQuery(ParentInstanceId: parentId, Limit: int.MaxValue))).Items.Select(async row => (await store.GetWorkflowInstanceAsync(row.InstanceId))!))).Incarnation);
                Assert.Equal(row.Incarnation, (await store.GetWorkflowInstanceSummaryAsync(child.InstanceId))!.Incarnation);
                await store.DeleteWorkflowInstanceAsync(parentId, tombstones);
                await advance(parentId, tombstones + TimeSpan.FromDays(1));
            }
        }
        finally { await store.DeleteWorkflowInstanceAsync(parentId); }
    }

    private static async Task AgeRows(Func<DbConnection> open, string id, TimeSpan by)
    {
        await using var connection = open();
        await connection.ExecuteAsync(connection is SqlConnection ? """
            UPDATE dbo.IxIFlowWorkflowInstances SET EndedAtUtc=DATEADD(SECOND, -@Seconds, EndedAtUtc) WHERE InstanceId=@Id;
            UPDATE dbo.IxIFlowWorkflowLeases SET ExpiresAtUtc=DATEADD(SECOND, -@Seconds, ExpiresAtUtc)
                WHERE InstanceId=@Id AND ExpiresAtUtc>CAST('0001-01-01' AS DATETIME2);
            UPDATE dbo.IxIFlowWorkflowTombstones SET ExpiresAtUtc=DATEADD(SECOND, -@Seconds, ExpiresAtUtc) WHERE InstanceId=@Id;
            """ : """
            UPDATE ixiflow_workflow_instances SET ended_at_utc=ended_at_utc - @Seconds * INTERVAL '1 second' WHERE instance_id=@Id;
            UPDATE ixiflow_workflow_leases SET expires_at_utc=expires_at_utc - @Seconds * INTERVAL '1 second'
                WHERE instance_id=@Id AND expires_at_utc>'0001-01-01'::timestamptz;
            UPDATE ixiflow_workflow_tombstones SET expires_at_utc=expires_at_utc - @Seconds * INTERVAL '1 second' WHERE instance_id=@Id;
            """, new { Id = id, Seconds = (int)by.TotalSeconds });
    }
}
