using System.Text.Json;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
[Trait("Spec", "J-06")]
public class PostgreSqlWorkflowStateRepositoryTests
{
    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task SuspendedDeadlineAppearsInRecoveryScanWhenDue()
    {
        var connectionString = PostgreSqlTestDatabase<PostgreSqlWorkflowStateRepositoryTests>.ConnectionString;
        var instanceId = Guid.NewGuid().ToString("N");
        var repository = new PostgreSqlWorkflowStateRepository(connectionString);
        try
        {
            var instance = new WorkflowInstance
            {
                InstanceId = instanceId,
                Status = WorkflowStatus.Suspended,
                RunnableAtUtc = DateTime.UtcNow.AddMinutes(1)
            };
            await repository.SaveWorkflowInstanceAsync(instance);
            Assert.DoesNotContain(await repository.GetRunnableInstancesAsync(100),
                candidate => candidate.InstanceId == instanceId);

            instance.RunnableAtUtc = DateTime.UtcNow.AddMinutes(-1);
            await repository.SaveWorkflowInstanceAsync(instance);
            Assert.Contains(await repository.GetRunnableInstancesAsync(100),
                candidate => candidate.InstanceId == instanceId);

            instance.Status = WorkflowStatus.NeedsResolution;
            await repository.SaveWorkflowInstanceAsync(instance);
            Assert.Contains(await repository.GetRunnableInstancesAsync(100),
                candidate => candidate.InstanceId == instanceId);
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instanceId);
        }
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    [Trait("Spec", "A-08")]
    public async Task ACommitIsStoredAsItCameAndReadsTakeTheRevisionAndRunnableTimeFromTheirColumns()
    {
        var connectionString = PostgreSqlTestDatabase<PostgreSqlWorkflowStateRepositoryTests>.ConnectionString;
        var instanceId = Guid.NewGuid().ToString("N");
        var repository = new PostgreSqlWorkflowStateRepository(connectionString);
        try
        {
            var instance = new WorkflowInstance
            {
                InstanceId = instanceId, WorkflowName = "Columns", Status = WorkflowStatus.Suspended,
                ExecutionStateJson = "{\"Waits\":[]}", Revision = 0, RunnableAtUtc = DateTime.UtcNow.AddYears(5)
            };
            var commit = CheckpointCommit.Create(instance, 0, 0);
            Assert.Equal(WorkflowCommitStatus.Applied, (await repository.CommitWorkflowInstanceAsync(commit)).Status);

            await using (var connection = new Npgsql.NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var read = new Npgsql.NpgsqlCommand(
                    "SELECT state_json FROM ixiflow_workflow_instances WHERE instance_id = @InstanceId", connection);
                read.Parameters.AddWithValue("InstanceId", instanceId);
                Assert.Equal(commit.Payload, (string?)await read.ExecuteScalarAsync());
            }
            // The payload says revision 0 and a runnable time years away; the columns say what the store set.
            var stored = (await repository.GetWorkflowInstanceAsync(instanceId))!;
            Assert.Equal(1, stored.Revision);
            Assert.Null(stored.RunnableAtUtc);
            Assert.Equal(1, Assert.Single(await repository.GetWorkflowInstancesByNameAsync("Columns"),
                candidate => candidate.InstanceId == instanceId).Revision);
            Assert.Equal("{\"Waits\":[]}", stored.ExecutionStateJson);
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instanceId);
        }
    }

    // Rows are deleted by key in every table; the lease row stays, released, so a later owner gets a higher epoch.
    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    [Trait("Spec", "C-11")]
    public async Task DeletingAnInstanceDeletesItsRowsInEveryTable()
    {
        var connectionString = PostgreSqlTestDatabase<PostgreSqlWorkflowStateRepositoryTests>.ConnectionString;
        var instanceId = Guid.NewGuid().ToString("N");
        var repository = new PostgreSqlWorkflowStateRepository(connectionString);
        var instance = new WorkflowInstance
        {
            InstanceId = instanceId, WorkflowName = "Deleted", Status = WorkflowStatus.Suspended,
            WorkflowDataJson = "{\"Value\":1}"
        };
        Assert.NotNull(await repository.TryAcquireExecutionLeaseAsync(instanceId, "owner", TimeSpan.FromMinutes(1)));
        Assert.Equal(WorkflowCommitStatus.Applied, (await repository.CommitWorkflowInstanceAsync(
            CheckpointCommit.Create(instance, 1, 0,
                openedWaits: [new OpenedWait("approval", WaitKind.Event, "approval", "Approval", null)],
                traceEntries: [new ExecutionTraceEntry { ActivityName = "first" }]))).Status);
        Assert.Equal(EventAppendStatus.Stored, (await repository.AppendEventAsync(
            new WorkflowEvent(instanceId, "event-1", "Approval", "{}", Key: "approval"))).Status);
        Assert.Equal(6, await CountRowsAsync(connectionString, instanceId));

        await repository.DeleteWorkflowInstanceAsync(instanceId);

        Assert.Equal(0, await CountRowsAsync(connectionString, instanceId));
        Assert.Equal(2, (await repository.TryAcquireExecutionLeaseAsync(instanceId, "next", TimeSpan.FromMinutes(1)))!
            .Epoch);
        await repository.ReleaseExecutionLeaseAsync(instanceId, "next");
    }

    // One row in each of the instance, parts, traces, waits, events and commits tables while the instance exists.
    private static async Task<long> CountRowsAsync(string connectionString, string instanceId)
    {
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var count = new Npgsql.NpgsqlCommand("""
            SELECT (SELECT count(*) FROM ixiflow_workflow_instances WHERE instance_id = @InstanceId)
                + (SELECT count(*) FROM ixiflow_workflow_parts WHERE instance_id = @InstanceId)
                + (SELECT count(*) FROM ixiflow_workflow_traces WHERE instance_id = @InstanceId)
                + (SELECT count(*) FROM ixiflow_workflow_waits WHERE instance_id = @InstanceId)
                + (SELECT count(*) FROM ixiflow_workflow_events WHERE instance_id = @InstanceId)
                + (SELECT count(*) FROM ixiflow_workflow_commits WHERE instance_id = @InstanceId)
            """, connection);
        count.Parameters.AddWithValue("InstanceId", instanceId);
        return (long)(await count.ExecuteScalarAsync())!;
    }

    // A row that does not exist cannot be locked, so a first commit, a claim and a delete of one instance take a lock
    // on its ID instead, which SQL Server's range locks give it: a claim cannot grant a lease while a commit made
    // without one is being applied, and a delete cannot interleave with a first commit.
    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    [Trait("Spec", "C-11")]
    public async Task CommitsClaimsAndDeletesOfANewInstanceWaitForEachOther()
    {
        var connectionString = PostgreSqlTestDatabase<PostgreSqlWorkflowStateRepositoryTests>.ConnectionString;
        var repository = new PostgreSqlWorkflowStateRepository(connectionString);
        var instance = new WorkflowInstance
        {
            InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "NewInstance", Status = WorkflowStatus.Running,
            WorkflowDataJson = "{}"
        };
        // The schema is made first, so only the lock can hold the store up.
        await repository.GetWorkflowInstanceAsync(instance.InstanceId);
        try
        {
            Task<WorkflowCommitResult> commit;
            Task<ExecutionLeaseGrant?> claim;
            await using (var holder = await HoldIdentityAsync(connectionString, instance.InstanceId))
            {
                commit = repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, 0, 0));
                claim = repository.TryAcquireExecutionLeaseAsync(instance.InstanceId, "owner", TimeSpan.FromMinutes(1));
                await Task.Delay(TimeSpan.FromMilliseconds(500));
                Assert.False(commit.IsCompleted || claim.IsCompleted);
            }
            // Whichever went first, the other saw it: a commit after the claim is refused, one before it is not.
            Assert.NotNull(await claim);
            Assert.Contains((await commit).Status, new[] { WorkflowCommitStatus.Applied, WorkflowCommitStatus.StaleEpoch });

            Task deleting;
            await using (var holder = await HoldIdentityAsync(connectionString, instance.InstanceId))
            {
                deleting = repository.DeleteWorkflowInstanceAsync(instance.InstanceId);
                await Task.Delay(TimeSpan.FromMilliseconds(500));
                Assert.False(deleting.IsCompleted);
            }
            await deleting;
            Assert.Null(await repository.GetWorkflowInstanceAsync(instance.InstanceId));
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instance.InstanceId);
        }
    }

    // A delete reads the parent to tell before it takes its locks. A child its parent starts in between is found
    // under the locks, and the delete starts over, so the parent's wait still ends as the child's termination.
    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    [Trait("Spec", "D-06")]
    public async Task ADeleteThatMeetsAChildStartedMeanwhileTellsItsParent()
    {
        var connectionString = PostgreSqlTestDatabase<PostgreSqlWorkflowStateRepositoryTests>.ConnectionString;
        var repository = new PostgreSqlWorkflowStateRepository(connectionString);
        var parent = new WorkflowInstance
        {
            InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "Parent", Status = WorkflowStatus.Suspended,
            WorkflowDataJson = "{}"
        };
        var child = new WorkflowInstance
        {
            InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "Child", Status = WorkflowStatus.Running,
            WorkflowDataJson = "{}", ParentInstanceId = parent.InstanceId, ParentWaitId = "child"
        };
        Assert.Equal(WorkflowCommitStatus.Applied,
            (await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(parent, 0, 0))).Status);
        parent.Revision = 1;
        try
        {
            Task deleting;
            await using (var holder = await HoldIdentityAsync(connectionString, child.InstanceId))
            {
                deleting = repository.DeleteWorkflowInstanceAsync(child.InstanceId);
                await Task.Delay(TimeSpan.FromMilliseconds(500));
                Assert.False(deleting.IsCompleted);
                Assert.Equal(WorkflowCommitStatus.Applied, (await repository.CommitWorkflowInstanceAsync(
                    CheckpointCommit.Create(parent, 0, 1,
                        openedWaits: [new OpenedWait("child", WaitKind.Child, "", "IxIFlow.ChildCompleted", null)],
                        childStarts: [new ChildStart(child.InstanceId, JsonSerializer.Serialize(child), "child")])))
                    .Status);
            }
            await deleting;

            Assert.Null(await repository.GetWorkflowInstanceAsync(child.InstanceId));
            Assert.Equal("child",
                (await repository.GetEventAsync(parent.InstanceId, $"child-completed-{child.InstanceId}"))!.TargetWaitId);
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(child.InstanceId);
            await repository.DeleteWorkflowInstanceAsync(parent.InstanceId);
        }
    }

    // A transaction holding the lock the store takes on an instance's ID, until it is disposed.
    private static async Task<IdentityHold> HoldIdentityAsync(string connectionString, string instanceId)
    {
        var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync();
        await using var hold = new Npgsql.NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@InstanceId, 0))", connection, transaction);
        hold.Parameters.AddWithValue("InstanceId", instanceId);
        await hold.ExecuteNonQueryAsync();
        return new IdentityHold(connection, transaction);
    }

    private sealed class IdentityHold(Npgsql.NpgsqlConnection connection, Npgsql.NpgsqlTransaction transaction)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    // Each transaction that leaves an instance due with nobody holding it notifies listening hosts once, and the
    // steps that set the cases up notify nobody (C-13).
    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    [Trait("Spec", "C-13")]
    public async Task EveryWayAnInstanceBecomesClaimableNotifiesListeners()
    {
        var connectionString = PostgreSqlTestDatabase<PostgreSqlWorkflowStateRepositoryTests>.ConnectionString;
        var repository = new PostgreSqlWorkflowStateRepository(connectionString);
        await using var notifications = await Notifications.ListenAsync(connectionString);
        var ids = new List<string>();
        var reason = new CancellationReason { ReasonCode = "operator" };
        WorkflowInstance Instance(WorkflowStatus status, string? parentId = null, string? parentWaitId = null)
        {
            var instance = new WorkflowInstance
            {
                InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "Notified", Status = status,
                WorkflowDataJson = "{}", ParentInstanceId = parentId, ParentWaitId = parentWaitId
            };
            ids.Add(instance.InstanceId);
            return instance;
        }
        try
        {
            // Saves of a running instance and of an idle one whose time has passed.
            await notifications.ExpectAsync(() => repository.SaveWorkflowInstanceAsync(Instance(WorkflowStatus.Running)));
            var due = Instance(WorkflowStatus.Suspended);
            due.RunnableAtUtc = DateTime.UtcNow.AddMinutes(-1);
            await notifications.ExpectAsync(() => repository.SaveWorkflowInstanceAsync(due));

            // A cancellation request and an event for idle instances.
            var idle = Instance(WorkflowStatus.Suspended);
            await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(idle, 0, 0,
                openedWaits: [new OpenedWait("approval", WaitKind.Event, "approval", "Approval", null)]));
            await notifications.ExpectAsync(() => repository.RequestCancellationAsync(idle.InstanceId, reason));
            var waiting = Instance(WorkflowStatus.Suspended);
            await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(waiting, 0, 0,
                openedWaits: [new OpenedWait("approval", WaitKind.Event, "approval", "Approval", null)]));
            await notifications.ExpectAsync(() => repository.AppendEventAsync(
                new WorkflowEvent(waiting.InstanceId, "event-1", "Approval", "{}", Key: "approval")));

            // A running instance committed without a lease, and the release of a lease on a due instance.
            var unleased = Instance(WorkflowStatus.Running);
            await notifications.ExpectAsync(() => repository.CommitWorkflowInstanceAsync(
                CheckpointCommit.Create(unleased, 0, 0)));
            Assert.NotNull(await repository.TryAcquireExecutionLeaseAsync(unleased.InstanceId, "owner",
                TimeSpan.FromMinutes(1)));
            await notifications.ExpectAsync(() => repository.ReleaseExecutionLeaseAsync(unleased.InstanceId, "owner"));

            // A child's start, then its completion, which wakes its parent.
            var parent = Instance(WorkflowStatus.Suspended);
            var child = Instance(WorkflowStatus.Running, parent.InstanceId, "child");
            await notifications.ExpectAsync(() => repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(
                parent, 0, 0, openedWaits: [new OpenedWait("child", WaitKind.Child, "", "IxIFlow.ChildCompleted", null)],
                childStarts: [new ChildStart(child.InstanceId, JsonSerializer.Serialize(child), "child")])));
            var grant = (await repository.TryAcquireExecutionLeaseAsync(child.InstanceId, "child-owner",
                TimeSpan.FromMinutes(1)))!;
            child.Status = WorkflowStatus.Completed;
            await notifications.ExpectAsync(() => repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(
                child, grant.Epoch, 1, parentNotice: new ParentNotice(parent.InstanceId, "child",
                    $"completed-{child.InstanceId}", "IxIFlow.ChildCompleted", "{}"))));

            // A child that is gone when its idle parent asks it to cancel.
            var orphaned = Instance(WorkflowStatus.Suspended);
            await repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(orphaned, 0, 0,
                openedWaits: [new OpenedWait("missing", WaitKind.Child, "", "IxIFlow.ChildCompleted", null)]));
            orphaned.Revision = 1;
            await notifications.ExpectAsync(() => repository.CommitWorkflowInstanceAsync(CheckpointCommit.Create(
                orphaned, 0, 1, childCancellations: [new ChildCancellation("missing-child", reason, "missing")])));
        }
        finally
        {
            foreach (var id in ids)
                await repository.DeleteWorkflowInstanceAsync(id);
        }
    }

    /// <summary>Listens to the store's channel the way a host does, and counts what it hears.</summary>
    private sealed class Notifications : IAsyncDisposable
    {
        private readonly Npgsql.NpgsqlConnection _connection;
        private int _heard;
        private int _expected;

        private Notifications(Npgsql.NpgsqlConnection connection) => _connection = connection;

        public static async Task<Notifications> ListenAsync(string connectionString)
        {
            var connection = new Npgsql.NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            var notifications = new Notifications(connection);
            connection.Notification += (_, notification) =>
            {
                if (notification.Channel == PostgreSqlWorkflowStateRepository.RunnableChannel)
                    Interlocked.Increment(ref notifications._heard);
            };
            await using var listen = new Npgsql.NpgsqlCommand(
                $"LISTEN {PostgreSqlWorkflowStateRepository.RunnableChannel}", connection);
            await listen.ExecuteNonQueryAsync();
            return notifications;
        }

        // Nothing was heard since the last step, then the step is heard exactly once.
        public async Task ExpectAsync(Func<Task> step)
        {
            while (await _connection.WaitAsync(50))
            {
            }
            Assert.Equal(_expected, Volatile.Read(ref _heard));
            await step();
            _expected++;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (Volatile.Read(ref _heard) < _expected)
            {
                Assert.True(DateTime.UtcNow < deadline, "The step notified nobody");
                await _connection.WaitAsync(100);
            }
            while (await _connection.WaitAsync(50))
            {
            }
            Assert.Equal(_expected, Volatile.Read(ref _heard));
        }

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task SuspendedInstance_AppliesOneDeliveryOnceAcrossRepositories()
    {
        var connectionString = PostgreSqlTestDatabase<PostgreSqlWorkflowStateRepositoryTests>.ConnectionString;
        var instanceId = Guid.NewGuid().ToString("N");
        var firstRepository = new PostgreSqlWorkflowStateRepository(connectionString);
        var secondRepository = new PostgreSqlWorkflowStateRepository(connectionString);
        try
        {
            await ResumeOnceScenario.RunAsync(firstRepository, secondRepository, instanceId);
        }
        finally
        {
            await firstRepository.DeleteWorkflowInstanceAsync(instanceId);
        }
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task SuspendedInstance_SurvivesRepositoryRecreation()
    {
        var connectionString = PostgreSqlTestDatabase<PostgreSqlWorkflowStateRepositoryTests>.ConnectionString;
        var instanceId = Guid.NewGuid().ToString("N");
        var firstRepository = new PostgreSqlWorkflowStateRepository(connectionString);
        try
        {
            var instance = new WorkflowInstance
            {
                InstanceId = instanceId,
                WorkflowName = "DurableTest",
                WorkflowVersion = 2,
                Status = WorkflowStatus.Suspended,
                CorrelationId = "correlation-1",
                WorkflowDataJson = "{\"Value\":42}"
            };
            await firstRepository.SaveWorkflowInstanceAsync(instance);

            var secondRepository = new PostgreSqlWorkflowStateRepository(connectionString);
            var restored = await secondRepository.GetWorkflowInstanceAsync(instanceId);
            Assert.NotNull(restored);
            Assert.Equal(WorkflowStatus.Suspended, restored.Status);
            Assert.Equal("{\"Value\":42}", restored.WorkflowDataJson);
            Assert.Contains(await secondRepository.GetWorkflowInstancesByStatusAsync(WorkflowStatus.Suspended),
                candidate => candidate.InstanceId == instanceId);

            restored.Status = WorkflowStatus.Completed;
            await secondRepository.SaveWorkflowInstanceAsync(restored);
            Assert.DoesNotContain(await firstRepository.GetWorkflowInstancesByStatusAsync(WorkflowStatus.Suspended),
                candidate => candidate.InstanceId == instanceId);
            Assert.Contains(await firstRepository.GetWorkflowInstancesByNameAsync("DurableTest"),
                candidate => candidate.InstanceId == instanceId);
        }
        finally
        {
            await firstRepository.DeleteWorkflowInstanceAsync(instanceId);
        }
    }
}
