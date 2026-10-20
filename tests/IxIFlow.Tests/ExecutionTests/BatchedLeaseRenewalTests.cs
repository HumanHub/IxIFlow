using System.Collections.Concurrent;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
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
        using var watch = new CancellationWatch(store, time, renewer: renewer);
        var leases = new List<WorkflowExecutionLease>();
        try
        {
            for (var index = 0; index < 32; index++)
                leases.Add((await AcquireAsync(store, watch, time, $"batch-{index}"))!);
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
        using var watch = new CancellationWatch(store, time);
        await using var first = (await AcquireAsync(store, watch, time, "first"))!;
        time.Advance(Settings.RenewalInterval);
        await ConfirmRenewalsAsync(time, time.Start + Settings.RenewalInterval + Settings.Duration, 1);
        time.Advance(TimeSpan.FromMilliseconds(50));
        var acquiredAt = time.GetUtcNow();
        var interval = TimeSpan.FromMilliseconds(intervalMilliseconds);
        await using var second = (await AcquireAsync(store, watch, time, "second", settings: Settings with { RenewalInterval = interval }))!;
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
        using var watch = new CancellationWatch(store, time);
        var instances = new List<WorkflowInstance>();
        for (var index = 0; index < 3; index++) instances.Add(await OperationsTests.SeedAsync(store, WorkflowStatus.Running));
        var losses = Enumerable.Range(0, 3).Select(_ => new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var cancellations = Enumerable.Range(0, 3).Select(_ => new TaskCompletionSource<CancellationReason>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var leases = new List<WorkflowExecutionLease>();
        try
        {
            for (var index = 0; index < 3; index++)
                leases.Add((await AcquireAsync(store, watch, time, instances[index].InstanceId, losses[index], cancellations[index]))!);
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
        using var watch = new CancellationWatch(store, time);
        var firstInstance = await OperationsTests.SeedAsync(store, WorkflowStatus.Running);
        var secondInstance = await OperationsTests.SeedAsync(store, WorkflowStatus.Running);
        var firstLoss = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondLoss = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = new TaskCompletionSource<CancellationReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var first = (await AcquireAsync(store, watch, time, firstInstance.InstanceId, firstLoss, cancellation))!;
        time.Advance(TimeSpan.FromMilliseconds(50));
        await using var second = (await AcquireAsync(store, watch, time, secondInstance.InstanceId, secondLoss, cancellation))!;
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
        using var watch = new CancellationWatch(store, time);
        var firstLoss = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondLoss = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var first = (await AcquireAsync(store, watch, time, "first", firstLoss))!;
        await using var second = (await AcquireAsync(store, watch, time, "second", secondLoss))!;
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
        using var watch = new CancellationWatch(store, time);
        await using var first = (await AcquireAsync(store, watch, time, "first"))!;
        time.Advance(Settings.RenewalInterval);
        Assert.Equal("first", Assert.Single(Assert.Single(store.Attempts).Leases).InstanceId);
        time.Advance(TimeSpan.FromMilliseconds(50));
        await using var second = (await AcquireAsync(store, watch, time, "second"))!;
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
        using var watch = new CancellationWatch(store, time, renewer: renewer);
        await using var lease = (await AcquireAsync(store, watch, time, "shutdown"))!;
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
        TimeProvider time, string id, TaskCompletionSource<Exception>? lost = null,
        TaskCompletionSource<CancellationReason>? cancellation = null, ExecutionLeaseSettings? settings = null) =>
        WorkflowExecutionLease.TryAcquireAsync(store, new() { InstanceId = id }, settings ?? Settings, watch, Claim,
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
}
