using System.Collections.Concurrent;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
[Trait("Spec", "B-09")]
public sealed class BatchedLeaseRenewalTests : IClassFixture<SqliteTestDatabase<BatchedLeaseRenewalTests>>
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);
    private static readonly ExecutionLeaseSettings Settings = new(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(100));
    private static readonly ClaimQuery Claim = new(Guid.Empty, new Dictionary<string, string>());

    [Fact]
    public async Task OneStoreCallRenewsEveryLeaseTheHostHolds()
    {
        var time = new TimerRecordingClock();
        var store = new RecordingStore(time);
        using var renewer = new ExecutionLeaseRenewer(store, time);
        using var watch = new CancellationWatch(store, time);
        var leases = new List<WorkflowExecutionLease>();
        try
        {
            for (var index = 0; index < 32; index++)
                leases.Add((await AcquireAsync(store, watch, renewer, time, $"batch-{index}"))!);
            Assert.Empty(store.Attempts);
            time.Advance(Settings.RenewalInterval);
            await ConfirmRenewalsAsync(time, time.Start + Settings.RenewalInterval + Settings.Duration, leases.Count);
            var first = Assert.Single(store.Attempts);
            Assert.Equal(32, first.Leases.Length);
            Assert.Equal(32, first.Leases.Select(lease => lease.InstanceId).Distinct().Count());
            time.Advance(Settings.RenewalInterval);
            await ConfirmRenewalsAsync(time, time.Start + Settings.RenewalInterval * 2 + Settings.Duration, leases.Count);
            Assert.Equal(2, store.Attempts.Count);
            Assert.All(store.Attempts, attempt => Assert.Equal(32, attempt.Leases.Length));
            Assert.All(leases, lease => lease.ThrowIfLost());
        }
        finally { foreach (var lease in leases) await lease.DisposeAsync(); }
    }

    [Theory]
    [InlineData(100)]
    [InlineData(20)]
    public async Task ALeaseAcquiredBetweenBatchesIsAttemptedWithinItsInterval(int intervalMilliseconds)
    {
        var time = new TimerRecordingClock();
        var store = new RecordingStore(time);
        using var renewer = new ExecutionLeaseRenewer(store, time);
        using var watch = new CancellationWatch(store, time);
        await using var first = (await AcquireAsync(store, watch, renewer, time, "first"))!;
        time.Advance(Settings.RenewalInterval);
        await ConfirmRenewalsAsync(time, time.Start + Settings.RenewalInterval + Settings.Duration, 1);
        time.Advance(TimeSpan.FromMilliseconds(50));
        var acquiredAt = time.GetUtcNow();
        var interval = TimeSpan.FromMilliseconds(intervalMilliseconds);
        await using var second = (await AcquireAsync(store, watch, renewer, time, "second", settings: Settings with { RenewalInterval = interval }))!;
        var due = intervalMilliseconds == 20 ? TimeSpan.FromMilliseconds(20) : TimeSpan.FromMilliseconds(50);
        time.Advance(due);
        await ConfirmRenewalsAsync(time, time.GetUtcNow() + Settings.Duration, 2);
        var attempt = Assert.Single(store.Attempts, attempt => attempt.Leases.Any(lease => lease.InstanceId == "second"));
        Assert.Equal(2, attempt.Leases.Length);
        Assert.True(attempt.SentAt - acquiredAt <= interval);
        Assert.Equal(2, store.Attempts.Count);
        first.ThrowIfLost();
        second.ThrowIfLost();
    }

    [Fact]
    public async Task OneRefusalOnlyLosesItsLeaseAndTerminationSignalsItsOwnRun()
    {
        var time = new TimerRecordingClock();
        var store = new RecordingStore(time);
        using var renewer = new ExecutionLeaseRenewer(store, time);
        using var watch = new CancellationWatch(store, time);
        var instances = new List<WorkflowInstance>();
        for (var index = 0; index < 3; index++) instances.Add(await OperationsTests.SeedAsync(store, WorkflowStatus.Running));
        var losses = Enumerable.Range(0, 3).Select(_ => new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var cancellations = Enumerable.Range(0, 3).Select(_ => new TaskCompletionSource<CancellationReason>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var leases = new List<WorkflowExecutionLease>();
        try
        {
            for (var index = 0; index < 3; index++)
                leases.Add((await AcquireAsync(store, watch, renewer, time, instances[index].InstanceId, losses[index], cancellations[index]))!);
            Assert.True((await store.RequestTerminationAsync(instances[1].InstanceId, new() { ReasonCode = "stop" })).Accepted);
            await store.DeleteWorkflowInstanceAsync(instances[2].InstanceId);
            time.Advance(Settings.RenewalInterval);
            Assert.Equal("stop", (await cancellations[1].Task.WaitAsync(Patience)).ReasonCode);
            Assert.IsType<InvalidOperationException>(await losses[2].Task.WaitAsync(Patience));
            await ConfirmRenewalsAsync(time, time.Start + Settings.RenewalInterval + Settings.Duration, 1);
            Assert.Single(store.Attempts);
            Assert.False(losses[0].Task.IsCompleted);
            Assert.False(losses[1].Task.IsCompleted);
            Assert.False(cancellations[0].Task.IsCompleted);
            Assert.False(cancellations[2].Task.IsCompleted);
            leases[0].ThrowIfLost();
            leases[1].ThrowIfLost();
            Assert.Throws<ExecutionOwnershipLostException>(leases[2].ThrowIfLost);
        }
        finally { foreach (var lease in leases) await lease.DisposeAsync(); }
    }

    [Fact]
    public async Task AHungBatchLosesEachLeaseByItsOwnClockAndTerminationFallbackClaimsAtExpiry()
    {
        var time = new TimerRecordingClock();
        var store = new RecordingStore(time) { Hang = true };
        using var renewer = new ExecutionLeaseRenewer(store, time);
        using var watch = new CancellationWatch(store, time);
        var firstInstance = await OperationsTests.SeedAsync(store, WorkflowStatus.Running);
        var secondInstance = await OperationsTests.SeedAsync(store, WorkflowStatus.Running);
        var firstLoss = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondLoss = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = new TaskCompletionSource<CancellationReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var first = (await AcquireAsync(store, watch, renewer, time, firstInstance.InstanceId, firstLoss, cancellation))!;
        time.Advance(TimeSpan.FromMilliseconds(50));
        await using var second = (await AcquireAsync(store, watch, renewer, time, secondInstance.InstanceId, secondLoss, cancellation))!;
        foreach (var instance in new[] { firstInstance, secondInstance })
            Assert.True((await store.RequestTerminationAsync(instance.InstanceId, new() { ReasonCode = "stop" })).Accepted);
        time.Advance(TimeSpan.FromMilliseconds(50));
        Assert.Equal(2, Assert.Single(store.Attempts).Leases.Length);
        time.Advance(TimeSpan.FromMilliseconds(299));
        Assert.False(firstLoss.Task.IsCompleted);
        Assert.Null(await store.TryAcquireExecutionLeaseAsync(firstInstance.InstanceId, "fallback", Settings.Duration, Claim, ExecutionClaimKind.Termination));
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.IsType<TimeoutException>(await firstLoss.Task.WaitAsync(Patience));
        var firstFallback = await store.TryAcquireExecutionLeaseAsync(firstInstance.InstanceId, "fallback", Settings.Duration, Claim, ExecutionClaimKind.Termination);
        Assert.NotNull(firstFallback);
        Assert.True(firstFallback.Epoch > first.Epoch);
        Assert.False(secondLoss.Task.IsCompleted);
        Assert.Null(await store.TryAcquireExecutionLeaseAsync(secondInstance.InstanceId, "fallback", Settings.Duration, Claim, ExecutionClaimKind.Termination));
        time.Advance(TimeSpan.FromMilliseconds(50));
        Assert.IsType<TimeoutException>(await secondLoss.Task.WaitAsync(Patience));
        var secondFallback = await store.TryAcquireExecutionLeaseAsync(secondInstance.InstanceId, "fallback", Settings.Duration, Claim, ExecutionClaimKind.Termination);
        Assert.NotNull(secondFallback);
        Assert.True(secondFallback.Epoch > second.Epoch);
        store.Reply.TrySetResult([new(ExecutionLeaseRenewalStatus.Renewed), new(ExecutionLeaseRenewalStatus.TerminationRequested,
            new() { Mode = WorkflowControlMode.Terminate })]);
        Assert.Throws<ExecutionOwnershipLostException>(first.ThrowIfLost);
        Assert.Throws<ExecutionOwnershipLostException>(second.ThrowIfLost);
        Assert.False(cancellation.Task.IsCompleted);
        Assert.Single(store.Attempts);
    }

    [Fact]
    public async Task AFailingBatchKeepsEachLeasesOriginalExpiryAndRetriesTogether()
    {
        var time = new TimerRecordingClock();
        var store = new RecordingStore(time) { Fail = true };
        using var renewer = new ExecutionLeaseRenewer(store, time);
        using var watch = new CancellationWatch(store, time);
        var firstLoss = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondLoss = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var first = (await AcquireAsync(store, watch, renewer, time, "first", firstLoss))!;
        await using var second = (await AcquireAsync(store, watch, renewer, time, "second", secondLoss))!;
        var beforeAttempt = time.Armed;
        time.Advance(Settings.RenewalInterval);
        await time.WaitForTimerDueAtAsync(time.Start + TimeSpan.FromMilliseconds(175));
        await time.WaitUntilAsync(timers => timers.Count(timer => timer.Number > beforeAttempt && timer.Pending &&
            timer.DueAt == time.Start + Settings.Duration) >= 2, "both leases to schedule their retry");
        Assert.False(firstLoss.Task.IsCompleted);
        Assert.False(secondLoss.Task.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(75));
        Assert.Equal(2, store.Attempts.Count);
        Assert.All(store.Attempts, attempt => Assert.Equal(2, attempt.Leases.Length));
        time.Advance(TimeSpan.FromMilliseconds(225));
        await firstLoss.Task.WaitAsync(Patience);
        await secondLoss.Task.WaitAsync(Patience);
        Assert.Throws<ExecutionOwnershipLostException>(first.ThrowIfLost);
        Assert.Throws<ExecutionOwnershipLostException>(second.ThrowIfLost);
        Assert.NotNull(await store.TryAcquireExecutionLeaseAsync("first", "fallback", Settings.Duration, Claim));
        Assert.NotNull(await store.TryAcquireExecutionLeaseAsync("second", "fallback", Settings.Duration, Claim));
    }

    [Fact]
    public async Task ANewLeaseIsAttemptedOnTimeWhileAnOlderBatchIsHung()
    {
        var time = new TimerRecordingClock();
        var store = new RecordingStore(time) { Hang = true };
        using var renewer = new ExecutionLeaseRenewer(store, time);
        using var watch = new CancellationWatch(store, time);
        await using var first = (await AcquireAsync(store, watch, renewer, time, "first"))!;
        time.Advance(Settings.RenewalInterval);
        Assert.Equal("first", Assert.Single(Assert.Single(store.Attempts).Leases).InstanceId);
        time.Advance(TimeSpan.FromMilliseconds(50));
        await using var second = (await AcquireAsync(store, watch, renewer, time, "second"))!;
        var acquiredAt = time.GetUtcNow();
        time.Advance(Settings.RenewalInterval);
        Assert.Equal(2, store.Attempts.Count);
        var next = store.Attempts.Last();
        Assert.Equal("second", Assert.Single(next.Leases).InstanceId);
        Assert.Equal(Settings.RenewalInterval, next.SentAt - acquiredAt);
        first.ThrowIfLost();
        second.ThrowIfLost();
    }

    [Fact]
    public async Task AsyncShutdownJoinsAnOutstandingBatchBeforeTheStoreIsDisposed()
    {
        var time = new TimerRecordingClock();
        var store = new RecordingStore(time) { Hang = true };
        var renewer = new ExecutionLeaseRenewer(store, time);
        using var watch = new CancellationWatch(store, time);
        await using var lease = (await AcquireAsync(store, watch, renewer, time, "shutdown"))!;
        time.Advance(Settings.RenewalInterval);
        Assert.Single(store.Attempts);
        lease.StopRenewing();
        var disposing = renewer.DisposeAsync().AsTask();
        try
        {
            Assert.False(disposing.IsCompleted);
            store.Reply.TrySetResult([new(ExecutionLeaseRenewalStatus.Renewed)]);
            await disposing.WaitAsync(Patience);
            Assert.True(store.Reply.Task.IsCompletedSuccessfully);
            Assert.Single(store.Attempts);
        }
        finally
        {
            store.Reply.TrySetResult([new(ExecutionLeaseRenewalStatus.Refused)]);
            await renewer.DisposeAsync();
        }
    }

    [Fact]
    public async Task ASlowBatchCompletingOneMillisecondAfterTheNextTickRenewsAgainImmediately()
    {
        var time = new TimerRecordingClock();
        var store = new RecordingStore(time) { Hang = true };
        using var renewer = new ExecutionLeaseRenewer(store, time);
        using var watch = new CancellationWatch(store, time);
        await using var lease = (await AcquireAsync(store, watch, renewer, time, "slow"))!;
        try
        {
            time.Advance(Settings.RenewalInterval);
            time.Advance(Settings.RenewalInterval + TimeSpan.FromMilliseconds(1));
            Assert.Single(store.Attempts);
            var beforeReply = time.Armed;
            store.Hang = false;
            store.Reply.TrySetResult([new(ExecutionLeaseRenewalStatus.Renewed)]);
            // The overdue interval is exactly -1 ms, which must not mean an infinite delay.
            await time.WaitForTimerAsync(timer => timer.DueTime == TimeSpan.Zero, beforeReply);
            time.Advance(TimeSpan.Zero);
            await ConfirmRenewalsAsync(time, time.GetUtcNow() + Settings.Duration, 1);
            Assert.Equal(2, store.Attempts.Count);
            Assert.Equal(time.Start + TimeSpan.FromMilliseconds(201), store.Attempts.Last().SentAt);
            lease.ThrowIfLost();
        }
        finally { store.Reply.TrySetResult([new(ExecutionLeaseRenewalStatus.Refused)]); }
    }

    [Fact]
    public async Task TerminationRequestedWithoutAReasonOnlyLosesItsLease()
    {
        var time = new TimerRecordingClock();
        var store = new RecordingStore(time) { Hang = true };
        using var renewer = new ExecutionLeaseRenewer(store, time);
        using var watch = new CancellationWatch(store, time);
        var malformedLoss = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var healthyLoss = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = new TaskCompletionSource<CancellationReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var malformed = (await AcquireAsync(store, watch, renewer, time, "malformed", malformedLoss, cancellation))!;
        await using var healthy = (await AcquireAsync(store, watch, renewer, time, "healthy", healthyLoss, cancellation))!;
        try
        {
            time.Advance(Settings.RenewalInterval);
            var attempt = Assert.Single(store.Attempts);
            store.Hang = false;
            store.Reply.TrySetResult(attempt.Leases.Select(lease => new ExecutionLeaseRenewalResult(
                lease.InstanceId == "malformed" ? ExecutionLeaseRenewalStatus.TerminationRequested : ExecutionLeaseRenewalStatus.Renewed)).ToArray());
            Assert.IsType<InvalidOperationException>(await malformedLoss.Task.WaitAsync(Patience));
            await ConfirmRenewalsAsync(time, time.Start + Settings.RenewalInterval + Settings.Duration, 1);
            Assert.Throws<ExecutionOwnershipLostException>(malformed.ThrowIfLost);
            Assert.False(healthyLoss.Task.IsCompleted);
            Assert.False(cancellation.Task.IsCompleted);
            healthy.ThrowIfLost();
            time.Advance(Settings.RenewalInterval);
            await ConfirmRenewalsAsync(time, time.Start + Settings.RenewalInterval * 2 + Settings.Duration, 1);
            Assert.Equal("healthy", Assert.Single(store.Attempts.Last().Leases).InstanceId);
        }
        finally { store.Reply.TrySetResult([new(ExecutionLeaseRenewalStatus.Refused), new(ExecutionLeaseRenewalStatus.Refused)]); }
    }

    [Fact]
    public async Task DuplicateInstanceRegistrationsAreSentInSeparatePasses()
    {
        var time = new TimerRecordingClock();
        var store = new RecordingStore(time);
        using var renewer = new ExecutionLeaseRenewer(store, time);
        Assert.NotNull(await store.TryAcquireExecutionLeaseAsync("duplicate", "current", Settings.Duration, Claim));
        Assert.NotNull(await store.TryAcquireExecutionLeaseAsync("healthy", "owner", Settings.Duration, Claim));
        var due = (time.Start + Settings.RenewalInterval).UtcDateTime;
        using var stale = renewer.Register(new("duplicate", "stale", Settings.Duration), due);
        using var current = renewer.Register(new("duplicate", "current", Settings.Duration), due);
        using var healthy = renewer.Register(new("healthy", "owner", Settings.Duration), due);
        time.Advance(Settings.RenewalInterval);
        time.Advance(TimeSpan.Zero);
        Assert.Equal(ExecutionLeaseRenewalStatus.Refused, (await stale.Next.WaitAsync(Patience)).Result.Status);
        Assert.Equal(ExecutionLeaseRenewalStatus.Renewed, (await current.Next.WaitAsync(Patience)).Result.Status);
        Assert.Equal(ExecutionLeaseRenewalStatus.Renewed, (await healthy.Next.WaitAsync(Patience)).Result.Status);
        Assert.Equal(2, store.Attempts.Count);
        Assert.All(store.Attempts, attempt => Assert.Equal(attempt.Leases.Length,
            attempt.Leases.Select(lease => lease.InstanceId).Distinct(StringComparer.Ordinal).Count()));
        Assert.Equal(3, store.Attempts.Sum(attempt => attempt.Leases.Length));
    }

    [Fact]
    public async Task AFailedBatchLogsOneRedactedWarningForAllItsMembers()
    {
        var time = new TimerRecordingClock();
        var store = new RecordingStore(time) { Fail = true };
        var logger = new RenewalLogger();
        using var renewer = new ExecutionLeaseRenewer(store, time, logger);
        var due = (time.Start + Settings.RenewalInterval).UtcDateTime;
        using var first = renewer.Register(new("first", "token", Settings.Duration), due);
        using var second = renewer.Register(new("second", "token", Settings.Duration), due);
        time.Advance(Settings.RenewalInterval);
        await Assert.ThrowsAsync<WorkflowEngineException>(() => first.Next);
        await Assert.ThrowsAsync<WorkflowEngineException>(() => second.Next);
        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Null(warning.Error);
        Assert.Contains("2 workflow instances", warning.Text);
        Assert.Contains(typeof(WorkflowEngineException).FullName!, warning.Text);
        Assert.Matches("\\([0-9a-f]{32}\\)$", warning.Text);
        Assert.DoesNotContain("Renewal store unavailable", warning.Text);
        Assert.Single(store.Attempts);
    }

    [Fact]
    public async Task SqliteAChunkFailureKeepsAnEarlierChunkCommitted()
    {
        var time = new TimerRecordingClock();
        var connectionString = SqliteTestDatabase<BatchedLeaseRenewalTests>.ConnectionString;
        await using var store = new SqliteWorkflowStateRepository(connectionString, time);
        var prefix = $"partial-{Guid.NewGuid():N}-";
        var requests = Enumerable.Range(0, 302).Select(index =>
            new ExecutionLeaseRenewal(prefix + index, "owner", TimeSpan.FromMinutes(10))).ToArray();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        try
        {
            foreach (var request in requests)
                Assert.NotNull(await store.TryAcquireExecutionLeaseAsync(request.InstanceId, request.Token, TimeSpan.FromMinutes(1), Claim));
            await connection.ExecuteAsync($"""
                CREATE TRIGGER fail_renewal_chunk BEFORE UPDATE ON ixiflow_workflow_leases
                WHEN OLD.instance_id = '{requests[301].InstanceId}'
                BEGIN SELECT RAISE(ABORT, 'renewal chunk unavailable'); END;
                """);
            var failure = await Assert.ThrowsAsync<SqliteException>(() => store.RenewExecutionLeasesAsync(requests));
            Assert.Contains("renewal chunk unavailable", failure.Message);
            await connection.ExecuteAsync("DROP TRIGGER fail_renewal_chunk");
            time.Advance(TimeSpan.FromMinutes(2));
            Assert.Null(await store.TryAcquireExecutionLeaseAsync(requests[0].InstanceId, "replacement", Settings.Duration, Claim));
            Assert.Null(await store.TryAcquireExecutionLeaseAsync(requests[299].InstanceId, "replacement", Settings.Duration, Claim));
            Assert.NotNull(await store.TryAcquireExecutionLeaseAsync(requests[300].InstanceId, "replacement", Settings.Duration, Claim));
            Assert.NotNull(await store.TryAcquireExecutionLeaseAsync(requests[301].InstanceId, "replacement", Settings.Duration, Claim));
            Assert.All(await store.RenewExecutionLeasesAsync(requests[..300]),
                result => Assert.Equal(ExecutionLeaseRenewalStatus.Renewed, result.Status));
        }
        finally
        {
            await connection.ExecuteAsync("DROP TRIGGER IF EXISTS fail_renewal_chunk");
            foreach (var request in requests) await store.DeleteWorkflowInstanceAsync(request.InstanceId);
        }
    }

    [Fact]
    public async Task SqliteMalformedCancellationDoesNotFailTheChunk()
    {
        var connectionString = SqliteTestDatabase<BatchedLeaseRenewalTests>.ConnectionString;
        await using var store = new SqliteWorkflowStateRepository(connectionString);
        await using var connection = new SqliteConnection(connectionString);
        await VerifyMalformedCancellationsAsync(store, (id, json) => connection.ExecuteAsync(
            "UPDATE ixiflow_workflow_instances SET cancellation_json=@json WHERE instance_id=@id", new { id, json }));
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task SqlServerMalformedCancellationDoesNotFailTheChunk()
    {
        var connectionString = SqlTestDatabase<BatchedLeaseRenewalTests>.ConnectionString;
        var store = new SqlWorkflowStateRepository(connectionString);
        await using var connection = new SqlConnection(connectionString);
        await VerifyMalformedCancellationsAsync(store, (id, json) => connection.ExecuteAsync(
            "UPDATE dbo.IxIFlowWorkflowInstances SET CancellationJson=@json WHERE InstanceId=@id", new { id, json }));
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSqlMalformedCancellationDoesNotFailTheChunk()
    {
        var connectionString = PostgreSqlTestDatabase<BatchedLeaseRenewalTests>.ConnectionString;
        var store = new PostgreSqlWorkflowStateRepository(connectionString);
        await using var connection = new NpgsqlConnection(connectionString);
        await VerifyMalformedCancellationsAsync(store, (id, json) => connection.ExecuteAsync(
            "UPDATE ixiflow_workflow_instances SET cancellation_json=@json WHERE instance_id=@id", new { id, json }));
    }

    private static async Task VerifyMalformedCancellationsAsync(IWorkflowStateRepository store, Func<string, string, Task> corrupt)
    {
        var ids = new List<string>();
        try
        {
            for (var index = 0; index < 5; index++)
            {
                var instance = await OperationsTests.SeedAsync(store, WorkflowStatus.Running);
                ids.Add(instance.InstanceId);
                Assert.NotNull(await store.TryAcquireExecutionLeaseAsync(instance.InstanceId, "owner", TimeSpan.FromMinutes(1), Claim));
            }
            await corrupt(ids[0], "{broken-json");
            await corrupt(ids[1], "{\"Mode\":\"invalid\"}");
            await corrupt(ids[2], "{\"Mode\":1,\"RequestedAt\":\"invalid\"}");
            Assert.True((await store.RequestTerminationAsync(ids[4], new() { ReasonCode = "stop" })).Accepted);
            var results = await store.RenewExecutionLeasesAsync(ids.Select(id =>
                new ExecutionLeaseRenewal(id, "owner", TimeSpan.FromMinutes(2))).ToArray());
            Assert.Equal(new[] { ExecutionLeaseRenewalStatus.Renewed, ExecutionLeaseRenewalStatus.Renewed,
                ExecutionLeaseRenewalStatus.Renewed, ExecutionLeaseRenewalStatus.Renewed,
                ExecutionLeaseRenewalStatus.TerminationRequested }, results.Select(result => result.Status));
            Assert.Equal("stop", results[4].PendingTermination!.ReasonCode);
            Assert.All(results.Take(4), result => Assert.Null(result.PendingTermination));
        }
        finally { foreach (var id in ids) await store.DeleteWorkflowInstanceAsync(id); }
    }

    [Fact]
    public async Task MemoryBatchReturnsIndependentOutcomes()
    {
        var time = new TimerRecordingClock();
        await VerifyOutcomesAsync(new InMemoryWorkflowStateRepository(time), _ =>
        { time.Advance(TimeSpan.FromMilliseconds(200)); return Task.CompletedTask; });
    }

    [Fact]
    public async Task SqliteBatchReturnsIndependentOutcomes()
    {
        var time = new TimerRecordingClock();
        await using var store = new SqliteWorkflowStateRepository(SqliteTestDatabase<BatchedLeaseRenewalTests>.ConnectionString, time);
        await VerifyOutcomesAsync(store, _ => { time.Advance(TimeSpan.FromMilliseconds(200)); return Task.CompletedTask; });
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task SqlServerBatchReturnsIndependentOutcomes()
    {
        var connectionString = SqlTestDatabase<BatchedLeaseRenewalTests>.ConnectionString;
        await VerifyOutcomesAsync(new SqlWorkflowStateRepository(connectionString), async id =>
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.ExecuteAsync("UPDATE dbo.IxIFlowWorkflowLeases SET ExpiresAtUtc=DATEADD(SECOND,-1,SYSUTCDATETIME()) WHERE InstanceId=@id", new { id });
        });
    }

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task PostgreSqlBatchReturnsIndependentOutcomes()
    {
        var connectionString = PostgreSqlTestDatabase<BatchedLeaseRenewalTests>.ConnectionString;
        await VerifyOutcomesAsync(new PostgreSqlWorkflowStateRepository(connectionString), async id =>
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.ExecuteAsync("UPDATE ixiflow_workflow_leases SET expires_at_utc=clock_timestamp()-INTERVAL '1 second' WHERE instance_id=@id", new { id });
        });
    }

    private static async Task VerifyOutcomesAsync(IWorkflowStateRepository store, Func<string, Task> expire)
    {
        var ids = new List<string>();
        try
        {
            for (var index = 0; index < 7; index++)
            {
                var instance = await OperationsTests.SeedAsync(store, WorkflowStatus.Running);
                ids.Add(instance.InstanceId);
                Assert.NotNull(await store.TryAcquireExecutionLeaseAsync(instance.InstanceId, "owner",
                    index == 0 ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromMinutes(5), Claim));
            }
            await expire(ids[0]);
            Assert.True((await store.RequestTerminationAsync(ids[2], new() { ReasonCode = "stop" })).Accepted);
            Assert.True((await store.RequestCancellationAsync(ids[4], new() { ReasonCode = "graceful" })).Accepted);
            await store.DeleteWorkflowInstanceAsync(ids[5]);
            Assert.True((await store.RequestTerminationAsync(ids[6], new() { ReasonCode = "wrong-token" })).Accepted);
            var requests = ids.Select((id, index) => new ExecutionLeaseRenewal(id,
                index is 3 or 6 ? "wrong" : "owner", TimeSpan.FromMinutes(10))).ToList();
            requests.Add(new(Guid.NewGuid().ToString("N"), "missing", TimeSpan.FromMinutes(10)));
            var results = await store.RenewExecutionLeasesAsync(requests);
            Assert.Equal(new[] { ExecutionLeaseRenewalStatus.Refused, ExecutionLeaseRenewalStatus.Renewed,
                ExecutionLeaseRenewalStatus.TerminationRequested, ExecutionLeaseRenewalStatus.Refused,
                ExecutionLeaseRenewalStatus.Renewed, ExecutionLeaseRenewalStatus.Refused,
                ExecutionLeaseRenewalStatus.Refused, ExecutionLeaseRenewalStatus.Refused }, results.Select(result => result.Status));
            Assert.Equivalent(await store.GetCancellationRequestAsync(ids[2]), results[2].PendingTermination, strict: true);
            Assert.All(results.Where((_, index) => index != 2), result => Assert.Null(result.PendingTermination));
            Assert.Empty(await store.RenewExecutionLeasesAsync([]));
            Assert.NotNull(await store.TryAcquireExecutionLeaseAsync(ids[0], "fallback", TimeSpan.FromMinutes(1), Claim));
        }
        finally { foreach (var id in ids) await store.DeleteWorkflowInstanceAsync(id); }
    }

    [Fact]
    public async Task SqliteChunksLargeBatches()
    {
        await using var store = new SqliteWorkflowStateRepository(SqliteTestDatabase<BatchedLeaseRenewalTests>.ConnectionString);
        await VerifyLargeBatchAsync(store);
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlServerChunksBeyond2100Parameters() =>
        VerifyLargeBatchAsync(new SqlWorkflowStateRepository(SqlTestDatabase<BatchedLeaseRenewalTests>.ConnectionString));

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlUsesArrayChunksBeyond2100Leases() =>
        VerifyLargeBatchAsync(new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<BatchedLeaseRenewalTests>.ConnectionString));

    private static async Task VerifyLargeBatchAsync(IWorkflowStateRepository store)
    {
        var requests = Enumerable.Range(0, 2201).Select(index =>
            new ExecutionLeaseRenewal($"batch-{Guid.NewGuid():N}-{index}", $"token-{index}", TimeSpan.FromMinutes(30))).ToArray();
        try
        {
            foreach (var request in requests)
                Assert.NotNull(await store.TryAcquireExecutionLeaseAsync(request.InstanceId, request.Token, TimeSpan.FromMinutes(5), Claim));
            // Straddle every provider's chunk boundaries with refusals and preserve input ordering.
            var sent = requests.Select((request, index) => index % 299 == 0 ? request with { Token = "wrong" } : request).ToArray();
            var results = await store.RenewExecutionLeasesAsync(sent);
            Assert.Equal(requests.Length, results.Count);
            for (var index = 0; index < results.Count; index++)
                Assert.Equal(index % 299 == 0 ? ExecutionLeaseRenewalStatus.Refused : ExecutionLeaseRenewalStatus.Renewed, results[index].Status);
            var correct = await store.RenewExecutionLeasesAsync(requests);
            Assert.Equal(requests.Length, correct.Count);
            Assert.All(correct, result => Assert.Equal(ExecutionLeaseRenewalStatus.Renewed, result.Status));
        }
        finally { foreach (var request in requests) await store.DeleteWorkflowInstanceAsync(request.InstanceId); }
    }

    private static Task<WorkflowExecutionLease?> AcquireAsync(IWorkflowStateRepository store, CancellationWatch watch,
        ExecutionLeaseRenewer renewer, TimeProvider time, string id, TaskCompletionSource<Exception>? lost = null,
        TaskCompletionSource<CancellationReason>? cancellation = null, ExecutionLeaseSettings? settings = null) =>
        WorkflowExecutionLease.TryAcquireAsync(store, new() { InstanceId = id }, settings ?? Settings, watch, renewer, Claim,
            error => { if (lost != null) lost.TrySetResult(error); else throw new InvalidOperationException("Unexpected ownership loss", error); },
            reason => cancellation?.TrySetResult(reason), _ => { }, time);

    private static Task ConfirmRenewalsAsync(TimerRecordingClock time, DateTimeOffset expiresAt, int count) =>
        time.WaitUntilAsync(timers => timers.Count(timer => timer.Pending && timer.DueAt == expiresAt) >= count,
            $"{count} leases to confirm renewal through {expiresAt:O}");

    private sealed class RecordingStore : FaultInjectingRepository
    {
        private readonly TimeProvider _time;
        public RecordingStore(TimeProvider time) : base(time) => _time = time;
        public ConcurrentQueue<(DateTimeOffset SentAt, ExecutionLeaseRenewal[] Leases)> Attempts { get; } = new();
        public bool Hang;
        public bool Fail;
        public TaskCompletionSource<IReadOnlyList<ExecutionLeaseRenewalResult>> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override Task<IReadOnlyList<ExecutionLeaseRenewalResult>> RenewExecutionLeasesAsync(IReadOnlyList<ExecutionLeaseRenewal> leases)
        {
            Attempts.Enqueue((_time.GetUtcNow(), leases.ToArray()));
            if (Fail) throw new IOException("Renewal store unavailable");
            return Hang ? Reply.Task : base.RenewExecutionLeasesAsync(leases);
        }
        // Keep cancellation delivery independent so the renewal response is the only termination signal.
        public override Task<IReadOnlyDictionary<string, CancellationReason>> GetCancellationRequestsAsync(IReadOnlyCollection<string> instanceIds) =>
            Task.FromResult<IReadOnlyDictionary<string, CancellationReason>>(new Dictionary<string, CancellationReason>());
    }

    private sealed class RenewalLogger : ILogger<ExecutionLeaseRenewer>
    {
        public List<(LogLevel Level, string Text, Exception? Error)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
