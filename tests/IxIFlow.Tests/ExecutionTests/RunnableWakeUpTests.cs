using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// A store can wake a host when an instance becomes runnable, so the host claims it before its next poll; PostgreSQL
/// does it through LISTEN/NOTIFY. The host keeps polling, so a store that cannot wake it costs only latency (C-13).
/// Hosts run on a fake clock that the tests do not move, so nothing but a wake-up can make them claim.
/// </summary>
[Trait("Feature", "recovery")]
[Trait("Spec", "C-13")]
public sealed class RunnableWakeUpTests
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task AWakeUpClaimsWithoutWaitingForThePoll()
    {
        var time = new FakeTimeProvider();
        var repository = new InMemoryWorkflowStateRepository(time);
        var wakeUps = new ManualWakeSource();
        var instanceId = await SuspendAsync(Services(time, repository, null));
        var scans = ScanCounter.Wrap(repository);
        await using var host = await StartAsync(Services(time, scans.Store, wakeUps));
        await wakeUps.Listening.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await scans.SettledAsync(after: 0);
        await AppendApprovalAsync(repository, instanceId);
        Assert.Equal(WorkflowStatus.Suspended, (await repository.GetWorkflowInstanceAsync(instanceId))!.Status);

        wakeUps.Wake();

        await CompletedAsync(repository, instanceId);
    }

    [Fact]
    public async Task AHostPollsWhileItsStoreCannotWakeIt()
    {
        var time = new FakeTimeProvider();
        var repository = new InMemoryWorkflowStateRepository(time);
        var wakeUps = new FailingWakeSource();
        var instanceId = await SuspendAsync(Services(time, repository, null));
        await using var host = await StartAsync(Services(time, repository, wakeUps));
        await AppendApprovalAsync(repository, instanceId);

        // Each poll interval the host polls, and tries to listen again.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while ((await repository.GetWorkflowInstanceAsync(instanceId))!.Status != WorkflowStatus.Completed ||
               wakeUps.Attempts < 2)
        {
            Assert.True(DateTime.UtcNow < deadline, "The host never polled");
            time.Advance(PollInterval);
            await Task.Delay(20);
        }
    }

    // The clock never moves, so the host scans twice before the event is stored: when it starts, and when its store
    // wakes it as it starts listening. Both are over before the event is stored, so only the notification of its
    // append can make the host claim the instance.
    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task AnEventStoredElsewhereWakesAPostgreSqlHost() => VerifyEventStoredElsewhereWakesHost(PostgreSqlTestDatabase<RunnableWakeUpTests>.ConnectionString);

    // The connection drops; the host polls, listens again a poll interval later, and is woken again.
    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task APostgreSqlHostListensAgainAfterItsConnectionDrops() => VerifyHostListensAfterConnectionDrops(PostgreSqlTestDatabase<RunnableWakeUpTests>.ConnectionString);

    // The process ID of the host's listening connection, once it is listening: one not in except, which holds the
    // connections of hosts that are gone or about to go. Moving the clock lets a host that lost its connection try again.
    private static async Task<int> ListenerAsync(string connectionString, IReadOnlyCollection<int> except,
        FakeTimeProvider? time = null)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            if ((await ListenersAsync(connectionString)).Except(except).ToArray() is [var pid])
                return pid;
            Assert.True(DateTime.UtcNow < deadline, "The host never listened");
            time?.Advance(PollInterval);
            await Task.Delay(20);
        }
    }

    // The process IDs of the connections listening for wake-ups in this database.
    private static async Task<int[]> ListenersAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var find = new NpgsqlCommand($"""
            SELECT pid FROM pg_stat_activity
            WHERE datname = current_database() AND application_name LIKE '%IxIFlow wake-ups'
              AND query = 'LISTEN {PostgreSqlWorkflowStateRepository.RunnableChannel}'
            """, connection);
        var pids = new List<int>();
        await using var reader = await find.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            pids.Add(reader.GetInt32(0));
        return pids.ToArray();
    }

    private static async Task<string> SuspendAsync(ServiceProvider services)
    {
        using (services)
        {
            var result = await services.GetRequiredService<IWorkflowClient>()
                .RunAsync(Definition(), new WakeData());
            Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
            return result.InstanceId;
        }
    }

    private static async Task AppendApprovalAsync(IWorkflowStateRepository repository, string instanceId) =>
        Assert.Equal(EventAppendStatus.Stored, (await repository.AppendEventAsync(new WorkflowEvent(instanceId,
            Guid.NewGuid().ToString("N"), typeof(Approval).AssemblyQualifiedName!,
            JsonSerializer.Serialize(new Approval()), Key: "approval"))).Status);

    private static async Task CompletedAsync(IWorkflowStateRepository repository, string instanceId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while ((await repository.GetWorkflowInstanceAsync(instanceId))!.Status != WorkflowStatus.Completed)
        {
            Assert.True(DateTime.UtcNow < deadline, "The host was never woken");
            await Task.Delay(20);
        }
    }

    private static WorkflowDefinition Definition() => Workflow.Create<WakeData>("WokenByTheStore")
        .Step<Noop>()
        .WaitFor<Approval>("approval")
        .Step<Noop>()
        .Build();

    private static ServiceProvider Services(TimeProvider time, IWorkflowStateRepository repository,
        IRunnableWakeSource? wakeUps)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(time);
        services.AddSingleton(repository);
        if (wakeUps != null)
            services.AddSingleton(wakeUps);
        services.AddIxIFlow(options => { options.ReplyTokenKeys = TestReplyKeys.Ring; options.PollInterval = PollInterval; });
        services.AddTransient<Noop>();
        return Registered(services.BuildServiceProvider());
    }

    private static ServiceProvider PostgreSqlServices(TimeProvider time, string connectionString)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(time);
        services.AddIxIFlow(options => { options.ReplyTokenKeys = TestReplyKeys.Ring; options.PollInterval = PollInterval; }).UsePostgreSql(connectionString);
        services.AddTransient<Noop>();
        return Registered(services.BuildServiceProvider());
    }

    private static ServiceProvider Registered(ServiceProvider services)
    {
        services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(Definition())
            .GetAwaiter().GetResult();
        return services;
    }

    private static async Task<RunningHost> StartAsync(ServiceProvider services)
    {
        var hosted = services.GetServices<IHostedService>().ToArray();
        foreach (var service in hosted)
            await service.StartAsync(CancellationToken.None);
        return new RunningHost(services, hosted);
    }

    private sealed class RunningHost(ServiceProvider services, IHostedService[] hosted) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            foreach (var service in hosted.Reverse())
                await service.StopAsync(CancellationToken.None);
            await services.DisposeAsync();
        }
    }

    /// <summary>Counts the host's scans for runnable instances, and forwards everything to the store it wraps.</summary>
    public class ScanCounter : DispatchProxy
    {
        private IWorkflowStateRepository _inner = null!;
        private int _started;
        private int _finished;

        public IWorkflowStateRepository Store { get; private set; } = null!;

        public int Started => Volatile.Read(ref _started);

        public static ScanCounter Wrap(IWorkflowStateRepository inner)
        {
            var store = Create<IWorkflowStateRepository, ScanCounter>();
            var counter = (ScanCounter)(object)store;
            counter._inner = inner;
            counter.Store = store;
            return counter;
        }

        // Waits until the host has finished this many scans and is running none.
        public async Task FinishedAsync(int scans)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (Volatile.Read(ref _finished) < scans || Started != Volatile.Read(ref _finished))
            {
                Assert.True(DateTime.UtcNow < deadline, "The host's scans never finished");
                await Task.Delay(20);
            }
        }

        // Waits until a scan has started since the count after, none is running, and none starts for a while.
        public async Task SettledAsync(int after)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            var last = -1;
            for (var quiet = 0; quiet < 5;)
            {
                Assert.True(DateTime.UtcNow < deadline, "The host's scans never settled");
                var started = Started;
                quiet = started > after && Volatile.Read(ref _finished) == started && started == last ? quiet + 1 : 0;
                last = started;
                await Task.Delay(20);
            }
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            try
            {
                if (method!.Name != nameof(IWorkflowStateRepository.GetRunnableInstancesAsync))
                    return method.Invoke(_inner, args);
                Interlocked.Increment(ref _started);
                return CountAsync((Task<IReadOnlyList<RunnableInstance>>)method.Invoke(_inner, args)!);
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Throw(error.InnerException);
                throw;
            }
        }

        private async Task<IReadOnlyList<RunnableInstance>> CountAsync(Task<IReadOnlyList<RunnableInstance>> scan)
        {
            try
            {
                return await scan;
            }
            finally
            {
                Interlocked.Increment(ref _finished);
            }
        }
    }

    /// <summary>
    /// A store's wake-ups, counted, with the number of scans the host had started when it was last woken. The store
    /// wakes the host once each time it starts listening, so a count is also a barrier for that wake-up.
    /// </summary>
    private sealed class ProbedWakeSource(IRunnableWakeSource inner, ScanCounter scans) : IRunnableWakeSource
    {
        private int _scansAtLastWake = -1;
        private int _wakes;

        public int ScansAtLastWake => Volatile.Read(ref _scansAtLastWake);

        public async Task WokenAsync(int times)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (Volatile.Read(ref _wakes) < times)
            {
                Assert.True(DateTime.UtcNow < deadline, "The store never woke the host");
                await Task.Delay(20);
            }
        }

        public Task ListenAsync(Action wake, CancellationToken cancellationToken) =>
            inner.ListenAsync(() =>
            {
                Volatile.Write(ref _scansAtLastWake, scans.Started);
                Interlocked.Increment(ref _wakes);
                wake();
            }, cancellationToken);
    }

    private sealed class ManualWakeSource : IRunnableWakeSource
    {
        private Action? _wake;

        public TaskCompletionSource Listening { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Wake() => _wake!();

        public async Task ListenAsync(Action wake, CancellationToken cancellationToken)
        {
            _wake = wake;
            Listening.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    private sealed class FailingWakeSource : IRunnableWakeSource
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public Task ListenAsync(Action wake, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            throw new IOException("The store cannot be listened to");
        }
    }

    public sealed class WakeData;

    public sealed class Approval;

    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
    private static async Task VerifyEventStoredElsewhereWakesHost(string connectionString)
    {
        var time = new FakeTimeProvider();
        var instanceId = await SuspendAsync(PostgreSqlServices(time, connectionString));
        var store = new PostgreSqlWorkflowStateRepository(connectionString);
        var scans = ScanCounter.Wrap(store);
        var wakeUps = new ProbedWakeSource(store, scans);
        var gone = await ListenersAsync(connectionString);
        await using var host = await StartAsync(Services(time, scans.Store, wakeUps));
        await ListenerAsync(connectionString, except: gone);
        await wakeUps.WokenAsync(times: 1);
        await scans.FinishedAsync(scans: 2);

        await AppendApprovalAsync(store, instanceId);

        await CompletedAsync(store, instanceId);
        await store.DeleteWorkflowInstanceAsync(instanceId);
    }


    private static async Task VerifyHostListensAfterConnectionDrops(string connectionString)
    {
        var time = new FakeTimeProvider();
        var store = new PostgreSqlWorkflowStateRepository(connectionString);
        var scans = ScanCounter.Wrap(store);
        var wakeUps = new ProbedWakeSource(store, scans);
        var gone = await ListenersAsync(connectionString);
        await using var host = await StartAsync(Services(time, scans.Store, wakeUps));
        var dropped = await ListenerAsync(connectionString, except: gone);
        await using (var admin = new NpgsqlConnection(connectionString))
        {
            await admin.OpenAsync();
            await using var terminate = new NpgsqlCommand("SELECT pg_terminate_backend(@Pid)", admin);
            terminate.Parameters.AddWithValue("Pid", dropped);
            await terminate.ExecuteScalarAsync();
        }
        await ListenerAsync(connectionString, except: [.. gone, dropped], time);
        var instanceId = await SuspendAsync(PostgreSqlServices(time, connectionString));
        // The polls the moving clock caused are not counted, so the scans are taken as over once the one the new
        // connection's wake-up asked for has started and none has started for a while.
        await wakeUps.WokenAsync(times: 2);
        await scans.SettledAsync(after: wakeUps.ScansAtLastWake);

        await AppendApprovalAsync(store, instanceId);

        await CompletedAsync(store, instanceId);
        await store.DeleteWorkflowInstanceAsync(instanceId);
    }


}
