using System.Text.Json;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.Conformance;

// Every host, including reopened and bundle hosts, uses the profile's actual links and repository.
internal sealed class InvocationRetryProfile : IAsyncDisposable
{
    private readonly List<Host> _hosts = [];
    private InvocationRetryProfile(ProfileStore database, TimeProvider clock) => (Database, Clock) = (database, clock);
    public ProfileStore Database { get; }
    public TimeProvider Clock { get; }
    public WorkflowCatalog Catalog { get; } = new();
    public InvocationRetryRecoveryScenarios.Ledger Ledger { get; } = new();
    public TimeSpan Delay => TimeSpan.FromSeconds(2);
    // Includes SQL poll/claim, coordinator notification and checkpoint latency. Only delays
    // the condition actually has to elapse belong in the budget (not an unconsumed backoff).
    internal static TimeSpan Patience(TimeSpan delay = default) => delay + TimeSpan.FromMinutes(2);

    internal static async Task<WorkflowExecutionResult> Finished(IWorkflowClient client, string id, TimeSpan delay = default)
    {
        var bound = Patience(delay);
        using var patience = new CancellationTokenSource(bound);
        try { return await client.AwaitAsync(id, WorkflowAwaitCondition.Finished, patience.Token); }
        catch (OperationCanceledException error) when (patience.IsCancellationRequested)
        { throw new TimeoutException($"Instance '{id}' did not finish within {bound} (scenario delay {delay}).", error); }
    }

    public static async Task<InvocationRetryProfile> Create(string profile)
    {
        TimeProvider clock = profile is StoreProfiles.LocalMemory or StoreProfiles.LocalSqlite
            ? new FakeTimeProvider(DateTimeOffset.UtcNow) : TimeProvider.System;
        return new(await StoreProfiles.CreateAsync(profile, clock), clock);
    }

    public async Task<Host> Open(Action<IServiceCollection>? configure = null, InvocationRetryFault? fault = null,
        IWorkflowCatalog? catalog = null, bool holdRetries = false)
    {
        var store = Database.NewRepository(Clock);
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow(options => { options.Debugging = true; options.VerifyCheckpointParts = true; options.ParkRetriesLongerThan = TimeSpan.Zero; });
        configure?.Invoke(services);
        services.AddSingleton(Clock);
        services.AddSingleton<IWorkflowCatalog>(catalog ?? Catalog);
        services.AddSingleton(Ledger);
        InvocationRetryPark? parking = holdRetries ? new() : null;
        if (parking != null)
        {
            fault ??= new();
            fault.Parking = parking;
        }
        services.AddSingleton(fault == null ? store : WithFault(store, fault));
        Database.ConfigureHost(services);
        var host = new Host(services.BuildServiceProvider(), store, Database, parking);
        _hosts.Add(host);
        await Database.StartHostLinksAsync(host.Services);
        return host;
    }

    internal static IWorkflowStateRepository WithFault(IWorkflowStateRepository store, InvocationRetryFault fault)
    {
        var proxy = DispatchProxy.Create<IWorkflowStateRepository, InvocationRetryFault>();
        ((InvocationRetryFault)(object)proxy).State = fault;
        fault.Inner = store;
        return proxy;
    }

    public Task Due(IWorkflowStateRepository store, string id, DateTime deadline) => Due(Clock, store, id, deadline);

    internal static DateTime RetryDeadline(WorkflowInstance saved)
    {
        var retryAt = Assert.Single(ExecutionCheckpoint.Read(saved.ExecutionStateJson).InvocationRecords.Values).RetryAfterUtc!.Value;
        // The store stamps WakeAfter independently of the run's estimated retry deadline.
        // Recovery must be past both, even when the stamp is a little earlier than the estimate.
        return saved.RunnableAtUtc is { } wakeAt && wakeAt > retryAt ? wakeAt : retryAt;
    }

    internal static async Task Due(TimeProvider clock, IWorkflowStateRepository store, string id, DateTime deadline)
    {
        if (clock is FakeTimeProvider fake)
        {
            if (deadline >= fake.GetUtcNow().UtcDateTime)
                fake.Advance(deadline - fake.GetUtcNow().UtcDateTime + TimeSpan.FromMilliseconds(1));
            return;
        }
        // Eligibility also checks workflow capabilities, pins and leases. Sample only the store's
        // clock here: the caller will recover the instance with the real host's placement snapshot.
        async Task<DateTime> StoreNow() => (await store.GetPendingEventsAsync(id, 0, 0)).StoreTimeUtc
            ?? throw new InvalidOperationException("The store did not return its clock");
        var now = await StoreNow().WaitAsync(Patience());
        if (now >= deadline) return;
        using var patience = new CancellationTokenSource(Patience(deadline - now));
        while (now < deadline)
        {
            await Task.Delay(25, patience.Token);
            now = await StoreNow().WaitAsync(patience.Token);
        }
    }

    public static async Task<WorkflowInstance> Until(IWorkflowStateRepository store, string id,
        Func<WorkflowInstance, bool> predicate, TimeSpan delay = default,
        [CallerArgumentExpression(nameof(predicate))] string condition = "")
    {
        var bound = Patience(delay);
        using var patience = new CancellationTokenSource(bound);
        WorkflowInstance? saved = null;
        try
        {
            while (true)
            {
                saved = await store.GetWorkflowInstanceAsync(id).WaitAsync(patience.Token);
                if (saved != null && predicate(saved)) return saved;
                await Task.Delay(25, patience.Token);
            }
        }
        catch (OperationCanceledException error) when (patience.IsCancellationRequested)
        {
            throw new TimeoutException($"Instance '{id}' did not satisfy {condition} within {bound}. " +
                $"Last status: {saved?.Status}; revision: {saved?.Revision}; runnable: {saved?.RunnableAtUtc:O}; " +
                $"checkpoint: {saved?.ExecutionStateJson}", error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts) await host.DisposeAsync();
        await Database.DisposeAsync();
    }

    internal sealed class Host(ServiceProvider services, IWorkflowStateRepository store, ProfileStore database,
        InvocationRetryPark? parking) : IAsyncDisposable
    {
        private bool _disposed;
        public ServiceProvider Services => services;
        public IWorkflowStateRepository Store => store;
        public IWorkflowClient Client => services.GetRequiredService<IWorkflowClient>();
        public IWorkflowOperations Operations => services.GetRequiredService<IWorkflowOperations>();
        public Task<WorkflowInstance> Parked(string id, int retries = 1) => parking!.Wait(id, retries);
        public void ReleaseRetry() => parking!.Release();
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            var stopped = services.GetRequiredService<WorkflowHost>().StopAsync(CancellationToken.None);
            parking?.Close();
            await stopped;
            await database.StopHostLinksAsync(services);
            await services.DisposeAsync();
            if (store is IAsyncDisposable disposable) await disposable.DisposeAsync();
        }
    }
}

// Latch an acknowledged park before the host can arm its automatic retry timer. These profiles
// deliberately run no recovery worker. Tests release the acknowledgement after observing the
// store deadline, or stop the host first when testing reopening. Signals retain early arrivals.
internal sealed class InvocationRetryPark
{
    private sealed class Stop
    {
        public TaskCompletionSource<WorkflowInstance> Saved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly ConcurrentDictionary<(string Id, int Retries), Stop> _stops = new();
    private bool _closed;

    public async Task<WorkflowInstance> Wait(string id, int retries = 1)
    {
        try { return await _stops.GetOrAdd((id, retries), _ => new()).Saved.Task.WaitAsync(InvocationRetryProfile.Patience()); }
        catch (TimeoutException error)
        { throw new TimeoutException($"Instance '{id}' did not commit a park for retry {retries}.", error); }
    }

    public async Task Observe(IWorkflowStateRepository store, CheckpointCommit commit, WorkflowCommitResult result)
    {
        if (!commit.Parks || !result.Parked) return;
        var saved = (await store.GetWorkflowInstanceAsync(commit.InstanceId))!;
        var record = ExecutionCheckpoint.Read(saved.ExecutionStateJson).InvocationRecords.Values.SingleOrDefault();
        if (record?.RetryAfterUtc == null) return;
        var stop = _stops.GetOrAdd((commit.InstanceId, record.ScheduledRetries), _ => new());
        stop.Saved.TrySetResult(saved);
        if (Volatile.Read(ref _closed)) stop.Released.TrySetResult();
        await stop.Released.Task;
    }

    public void Release()
    {
        foreach (var stop in _stops.Values.Where(stop => stop.Saved.Task.IsCompleted)) stop.Released.TrySetResult();
    }

    public void Close() { Volatile.Write(ref _closed, true); Release(); }
}

public class InvocationRetryFault : DispatchProxy
{
    internal InvocationRetryFault? State;
    internal IWorkflowStateRepository Inner = null!;
    internal Func<CheckpointCommit, bool>? Before;
    internal Func<CheckpointCommit, bool>? After;
    internal Func<CheckpointCommit, bool>? Observe;
    internal InvocationRetryPark? Parking;
    internal Func<CheckpointCommit, CheckpointCommit>? RejectPayload;
    internal CheckpointCommit? Captured;
    internal WorkflowCommitResult? Receipt;
    internal int Remaining;
    internal int RefuseNextClaim;
    internal TaskCompletionSource Exhausted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal List<CheckpointCommit> Commits = [];

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var state = State!;
        if (method!.Name == nameof(IWorkflowStateRepository.TryAcquireExecutionLeaseAsync) &&
            Interlocked.Exchange(ref state.RefuseNextClaim, 0) == 1)
            return Task.FromResult<ExecutionLeaseGrant?>(null);
        if (method!.Name == nameof(IWorkflowStateRepository.CommitWorkflowInstanceAsync))
            return state.Commit((CheckpointCommit)args![0]!);
        try { return method.Invoke(state.Inner, args); }
        catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException!).Throw(); throw; }
    }

    private async Task<WorkflowCommitResult> Commit(CheckpointCommit commit)
    {
        if (Remaining > 0 && RejectPayload != null && Replacement(commit))
        {
            Captured ??= commit;
            try { return await Inner.CommitWorkflowInstanceAsync(RejectPayload(commit)); }
            finally { Failed(); }
        }
        if (Remaining > 0 && Before?.Invoke(commit) == true)
        {
            Failed();
            throw new IOException("injected before invocation commit");
        }
        var result = await Inner.CommitWorkflowInstanceAsync(commit);
        if (Parking != null) await Parking.Observe(Inner, commit, result);
        lock (Commits) Commits.Add(commit);
        if (Observe?.Invoke(commit) == true) { Captured = commit; Receipt = result; }
        if (Remaining > 0 && After?.Invoke(commit) == true)
        {
            Captured ??= commit;
            Receipt ??= result;
            Failed();
            throw new IOException("injected lost invocation acknowledgement");
        }
        return result;
    }

    private void Failed() { if (Interlocked.Decrement(ref Remaining) == 0) Exhausted.TrySetResult(); }
    internal static bool Schedule(CheckpointCommit commit) => commit.Parts.Any(part =>
        part.Key.StartsWith("v/") &&
            JsonSerializer.Deserialize<InvocationRecordState>(part.Json.InlineText)!.ScheduledRetries == 1) && commit.ChildStarts.Count == 0;
    internal static bool Replacement(CheckpointCommit commit) => commit.ChildStarts.Count != 0 && commit.Parts.Any(part =>
        part.Key.StartsWith("v/") &&
            JsonSerializer.Deserialize<InvocationRecordState>(part.Json.InlineText)!.Attempts.Any(attempt => attempt.Number == 2));
}
