using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace IxIFlow.Tests.ExecutionTests;

public sealed class WorkflowCompletionOutboxTests
{
    [Fact]
    public async Task HostStartupRejectsARepositoryWithoutCompletionOutbox()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new Mock<IWorkflowStateRepository>().Object);
        using var provider = services.BuildServiceProvider();
        using var service = new WorkflowCompletionOutboxService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new CompletionBus(), new WorkflowHostOptions(),
            NullLogger<WorkflowCompletionOutboxService>.Instance);

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.StartAsync(CancellationToken.None));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task TerminalCheckpointPublishesAfterTheExecutingHostStops()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.Replace(ServiceDescriptor.Singleton<IWorkflowStateRepository>(repository));
        services.AddTransient<CompletedActivity>();
        using var host = services.BuildServiceProvider();
        var definition = Workflow.Create<CompletionData>("OutboxMemory")
            .Step<CompletedActivity>()
            .Build();
        var result = await host.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new CompletionData());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var bus = new CompletionBus();
        using var recovery = new WorkflowCompletionOutboxService(
            host.GetRequiredService<IServiceScopeFactory>(), bus,
            new WorkflowHostOptions { HostId = "recovery-host" },
            NullLogger<WorkflowCompletionOutboxService>.Instance);

        await recovery.PublishOnceAsync();
        await recovery.PublishOnceAsync();

        var completion = Assert.Single(bus.Completions);
        Assert.Equal(result.InstanceId, completion.InstanceId);
        Assert.Equal(WorkflowExecutionStatus.Success, completion.Status);
    }

    [Fact]
    public async Task FailedPublicationRemainsPendingForRetry()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.Replace(ServiceDescriptor.Singleton<IWorkflowStateRepository>(repository));
        services.AddTransient<CompletedActivity>();
        using var host = services.BuildServiceProvider();
        var definition = Workflow.Create<CompletionData>("OutboxRetry")
            .Step<CompletedActivity>()
            .Build();
        var result = await host.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new CompletionData());
        var bus = new CompletionBus { FailNextPublish = true };
        var publisher = new WorkflowCompletionPublisher(repository, bus, "recovery-host");

        await Assert.ThrowsAsync<IOException>(() => publisher.PublishPendingAsync());
        Assert.NotNull(await repository.GetUnpublishedCompletionAsync(result.InstanceId));
        await publisher.PublishPendingAsync();

        Assert.Single(bus.Completions);
        Assert.Null(await repository.GetUnpublishedCompletionAsync(result.InstanceId));
    }

    [Fact]
    public async Task OneFailedCompletionDoesNotBlockOtherPendingCompletions()
    {
        var repository = new InMemoryWorkflowStateRepository();
        for (var index = 0; index < 2; index++)
        {
            var instance = new WorkflowInstance
            {
                InstanceId = Guid.NewGuid().ToString("N"),
                WorkflowName = "OutboxBatch",
                Status = WorkflowStatus.Completed
            };
            Assert.Equal(WorkflowCommitStatus.Applied,
                (await repository.CommitWorkflowInstanceAsync(instance, 0,
                    Guid.NewGuid().ToString("N"))).Status);
        }
        var pending = (await repository.GetUnpublishedCompletionsAsync()).ToArray();
        Assert.Equal(2, pending.Length);
        var bus = new CompletionBus { FailInstanceId = pending[0].InstanceId };
        var publisher = new WorkflowCompletionPublisher(repository, bus, "host");

        await Assert.ThrowsAsync<IOException>(() => publisher.PublishPendingAsync());

        Assert.Equal(pending[1].InstanceId, Assert.Single(bus.Completions).InstanceId);
        await publisher.PublishPendingAsync();
        Assert.Equal(2, bus.Completions.Count);
    }

    [Fact]
    public async Task ConcurrentPublishersDoNotSendTheSameCompletion()
    {
        var repository = new InMemoryWorkflowStateRepository();
        var instance = new WorkflowInstance
        {
            InstanceId = Guid.NewGuid().ToString("N"),
            WorkflowName = "ConcurrentOutbox",
            Status = WorkflowStatus.Completed
        };
        var committed = await repository.CommitWorkflowInstanceAsync(instance, 0,
            Guid.NewGuid().ToString("N"));
        Assert.Equal(WorkflowCommitStatus.Applied, committed.Status);
        var bus = new BlockingCompletionBus();
        var first = new WorkflowCompletionPublisher(repository, bus, "host-a");
        var second = new WorkflowCompletionPublisher(repository, bus, "host-b");

        var publishing = first.PublishPendingAsync();
        await bus.Publishing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var secondPublishing = second.PublishPendingAsync();
        bus.Release.TrySetResult();
        await Task.WhenAll(publishing, secondPublishing).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, bus.PublishedCount);
    }

    [Fact]
    public async Task ExpiredMemoryOutboxClaimCanBeRecovered()
    {
        await VerifyExpiredClaimCanBeRecovered(new InMemoryWorkflowStateRepository());
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task ExpiredSqlOutboxClaimCanBeRecovered()
    {
        var repository = new SqlWorkflowStateRepository(
            Environment.GetEnvironmentVariable("IXIFLOW_TEST_SQL_CONNECTION_STRING")!);
        await VerifyExpiredClaimCanBeRecovered(repository);
    }

    private static async Task VerifyExpiredClaimCanBeRecovered(IWorkflowStateRepository repository)
    {
        var outbox = Assert.IsAssignableFrom<IWorkflowCompletionOutbox>(repository);
        var instance = new WorkflowInstance
        {
            InstanceId = Guid.NewGuid().ToString("N"),
            WorkflowName = "ExpiredOutboxClaim",
            Status = WorkflowStatus.Completed
        };
        try
        {
            Assert.Equal(WorkflowCommitStatus.Applied,
                (await repository.CommitWorkflowInstanceAsync(instance, 0,
                    Guid.NewGuid().ToString("N"))).Status);
            Assert.Single(await outbox.ClaimUnpublishedCompletionsAsync(
                instance.InstanceId, "dead-host", TimeSpan.FromMilliseconds(50)));
            Assert.Empty(await outbox.ClaimUnpublishedCompletionsAsync(
                instance.InstanceId, "new-host", TimeSpan.FromSeconds(5)));

            await Task.Delay(200);

            var recovered = Assert.Single(await outbox.ClaimUnpublishedCompletionsAsync(
                instance.InstanceId, "new-host", TimeSpan.FromSeconds(5)));
            Assert.False(await outbox.MarkCompletionPublishedAsync(
                instance.InstanceId, recovered.Revision, "dead-host"));
            Assert.True(await outbox.MarkCompletionPublishedAsync(
                instance.InstanceId, recovered.Revision, "new-host"));
        }
        finally
        {
            await repository.DeleteWorkflowInstanceAsync(instance.InstanceId);
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task TerminalSqlCheckpointPublishesOnAnotherHost()
    {
        var connectionString = Environment.GetEnvironmentVariable("IXIFLOW_TEST_SQL_CONNECTION_STRING")!;
        var firstRepository = new SqlWorkflowStateRepository(connectionString);
        var secondRepository = new SqlWorkflowStateRepository(connectionString);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.Replace(ServiceDescriptor.Singleton<IWorkflowStateRepository>(firstRepository));
        services.AddTransient<CompletedActivity>();
        using var host = services.BuildServiceProvider();
        var definition = Workflow.Create<CompletionData>("OutboxSql")
            .Step<CompletedActivity>()
            .Build();
        var result = await host.GetRequiredService<IWorkflowEngine>()
            .ExecuteWorkflowAsync(definition, new CompletionData());
        try
        {
            var bus = new CompletionBus();
            var publisher = new WorkflowCompletionPublisher(secondRepository, bus, "second-host");

            await publisher.PublishPendingAsync();
            await publisher.PublishPendingAsync();

            Assert.Equal(result.InstanceId, Assert.Single(bus.Completions).InstanceId);
        }
        finally
        {
            await firstRepository.DeleteWorkflowInstanceAsync(result.InstanceId);
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task SqlOutboxClaimExcludesASecondHost()
    {
        var connectionString = Environment.GetEnvironmentVariable("IXIFLOW_TEST_SQL_CONNECTION_STRING")!;
        var firstRepository = new SqlWorkflowStateRepository(connectionString);
        var secondRepository = new SqlWorkflowStateRepository(connectionString);
        var instance = new WorkflowInstance
        {
            InstanceId = Guid.NewGuid().ToString("N"),
            WorkflowName = "ConcurrentSqlOutbox",
            Status = WorkflowStatus.Completed
        };
        var committed = await firstRepository.CommitWorkflowInstanceAsync(instance, 0,
            Guid.NewGuid().ToString("N"));
        Assert.Equal(WorkflowCommitStatus.Applied, committed.Status);
        try
        {
            var bus = new BlockingCompletionBus();
            var first = new WorkflowCompletionPublisher(firstRepository, bus, "host-a");
            var second = new WorkflowCompletionPublisher(secondRepository, bus, "host-b");

            var publishing = first.PublishPendingAsync(instance.InstanceId);
            await bus.Publishing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var secondPublishing = second.PublishPendingAsync(instance.InstanceId);
            bus.Release.TrySetResult();
            await Task.WhenAll(publishing, secondPublishing).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, bus.PublishedCount);
        }
        finally
        {
            await firstRepository.DeleteWorkflowInstanceAsync(instance.InstanceId);
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task AddingOutboxColumnDoesNotRepublishHistoricalCompletions()
    {
        var masterConnectionString = Environment.GetEnvironmentVariable("IXIFLOW_TEST_SQL_CONNECTION_STRING")!;
        var databaseName = "IxIFlowOutboxMigration_" + Guid.NewGuid().ToString("N");
        var builder = new SqlConnectionStringBuilder(masterConnectionString)
        {
            InitialCatalog = databaseName
        };
        await using var master = new SqlConnection(masterConnectionString);
        await master.OpenAsync();
        await master.ExecuteAsync($"CREATE DATABASE [{databaseName}]");
        try
        {
            await using (var connection = new SqlConnection(builder.ConnectionString))
            {
                await connection.OpenAsync();
                await connection.ExecuteAsync("""
                    CREATE TABLE dbo.IxIFlowWorkflowInstances (
                        InstanceId NVARCHAR(100) NOT NULL PRIMARY KEY,
                        WorkflowName NVARCHAR(200) NOT NULL,
                        Status NVARCHAR(50) NOT NULL,
                        CorrelationId NVARCHAR(100) NULL,
                        SuspensionExpiresAt DATETIME2 NULL,
                        StateJson NVARCHAR(MAX) NOT NULL
                    );
                    """);
                var historical = new WorkflowInstance
                {
                    InstanceId = Guid.NewGuid().ToString("N"),
                    WorkflowName = "Historical",
                    Status = WorkflowStatus.Completed
                };
                await connection.ExecuteAsync("""
                    INSERT INTO dbo.IxIFlowWorkflowInstances
                        (InstanceId, WorkflowName, Status, StateJson)
                    VALUES (@InstanceId, @WorkflowName, @Status, @StateJson)
                    """, new { historical.InstanceId, historical.WorkflowName,
                        Status = historical.Status.ToString(),
                        StateJson = System.Text.Json.JsonSerializer.Serialize(historical) });
            }

            var repository = new SqlWorkflowStateRepository(builder.ConnectionString);
            Assert.Empty(await repository.GetUnpublishedCompletionsAsync());
        }
        finally
        {
            await master.ExecuteAsync($"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
            await master.ExecuteAsync($"DROP DATABASE [{databaseName}]");
        }
    }

    public sealed class CompletionData;

    public sealed class CompletedActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class CompletionBus : IMessageBus
    {
        public bool FailNextPublish { get; set; }
        public string? FailInstanceId { get; set; }
        public List<WorkflowExecutionCompletedEvent> Completions { get; } = [];

        public Task PublishAsync<T>(T message) where T : class
        {
            if (FailNextPublish)
            {
                FailNextPublish = false;
                throw new IOException("Message bus unavailable");
            }
            if (message is WorkflowExecutionCompletedEvent targeted &&
                targeted.InstanceId == FailInstanceId)
            {
                FailInstanceId = null;
                throw new IOException("One completion could not be published");
            }
            if (message is WorkflowExecutionCompletedEvent completion)
                Completions.Add(completion);
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<T> ConsumeAsync<T>() where T : class
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task StopAsync() => Task.CompletedTask;
    }

    private sealed class BlockingCompletionBus : IMessageBus
    {
        private int _published;
        public TaskCompletionSource Publishing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int PublishedCount => _published;

        public async Task PublishAsync<T>(T message) where T : class
        {
            Interlocked.Increment(ref _published);
            Publishing.TrySetResult();
            await Release.Task;
        }

        public async IAsyncEnumerable<T> ConsumeAsync<T>() where T : class
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task StopAsync() => Task.CompletedTask;
    }
}
