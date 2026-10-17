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
        await using var host = await StartAsync(Services(time, repository, wakeUps));
        await wakeUps.Listening.Task.WaitAsync(TimeSpan.FromSeconds(10));
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

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task AnEventStoredElsewhereWakesAPostgreSqlHost()
    {
        var connectionString = PostgreSqlTestDatabase<RunnableWakeUpTests>.ConnectionString;
        var time = new FakeTimeProvider();
        var instanceId = await SuspendAsync(PostgreSqlServices(time, connectionString));
        await using var host = await StartAsync(PostgreSqlServices(time, connectionString));
        await ListenerAsync(connectionString, except: null);
        var repository = new PostgreSqlWorkflowStateRepository(connectionString);

        await AppendApprovalAsync(repository, instanceId);

        await CompletedAsync(repository, instanceId);
        await repository.DeleteWorkflowInstanceAsync(instanceId);
    }

    // The connection drops; the host polls, listens again a poll interval later, and is woken again.
    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public async Task APostgreSqlHostListensAgainAfterItsConnectionDrops()
    {
        var connectionString = PostgreSqlTestDatabase<RunnableWakeUpTests>.ConnectionString;
        var time = new FakeTimeProvider();
        await using var host = await StartAsync(PostgreSqlServices(time, connectionString));
        var dropped = await ListenerAsync(connectionString, except: null);
        await using (var admin = new NpgsqlConnection(connectionString))
        {
            await admin.OpenAsync();
            await using var terminate = new NpgsqlCommand("SELECT pg_terminate_backend(@Pid)", admin);
            terminate.Parameters.AddWithValue("Pid", dropped);
            await terminate.ExecuteScalarAsync();
        }
        await ListenerAsync(connectionString, except: dropped, time);
        var instanceId = await SuspendAsync(PostgreSqlServices(time, connectionString));
        var repository = new PostgreSqlWorkflowStateRepository(connectionString);

        await AppendApprovalAsync(repository, instanceId);

        await CompletedAsync(repository, instanceId);
        await repository.DeleteWorkflowInstanceAsync(instanceId);
    }

    // The process ID of the host's listening connection, other than except, once it is listening. Moving the clock
    // lets a host that lost its connection try again.
    private static async Task<int> ListenerAsync(string connectionString, int? except, FakeTimeProvider? time = null)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            await using var find = new NpgsqlCommand($"""
                SELECT pid FROM pg_stat_activity
                WHERE datname = current_database() AND application_name LIKE '%IxIFlow wake-ups'
                  AND query = 'LISTEN {PostgreSqlWorkflowStateRepository.RunnableChannel}'
                  AND pid IS DISTINCT FROM @Except
                """, connection);
            find.Parameters.AddWithValue("Except", NpgsqlTypes.NpgsqlDbType.Integer, (object?)except ?? DBNull.Value);
            if (await find.ExecuteScalarAsync() is int pid)
                return pid;
            Assert.True(DateTime.UtcNow < deadline, "The host never listened");
            time?.Advance(PollInterval);
            await Task.Delay(20);
        }
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
        services.AddIxIFlow(options => options.PollInterval = PollInterval);
        services.AddTransient<Noop>();
        return Registered(services.BuildServiceProvider());
    }

    private static ServiceProvider PostgreSqlServices(TimeProvider time, string connectionString)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(time);
        services.AddIxIFlow(options => options.PollInterval = PollInterval).UsePostgreSql(connectionString);
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
}
