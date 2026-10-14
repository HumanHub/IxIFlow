using Dapper;
using IxIFlow.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace IxIFlow.Tests.ExecutionTests;

public sealed class SqlHostRegistryTests
{
    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task FreshDatabaseSupportsHostRegistrationAndMetrics()
    {
        var masterConnectionString = Environment.GetEnvironmentVariable("IXIFLOW_TEST_SQL_CONNECTION_STRING")!;
        var databaseName = "IxIFlowHostRegistry_" + Guid.NewGuid().ToString("N");
        var databaseConnectionString = new SqlConnectionStringBuilder(masterConnectionString)
        {
            InitialCatalog = databaseName
        }.ConnectionString;

        await using var master = new SqlConnection(masterConnectionString);
        await master.OpenAsync();
        await master.ExecuteAsync($"CREATE DATABASE [{databaseName}]");
        try
        {
            var registry = new SqlHostRegistry(databaseConnectionString);
            var secondRegistry = new SqlHostRegistry(databaseConnectionString);
            var hostId = Guid.NewGuid().ToString("N");
            var secondHostId = Guid.NewGuid().ToString("N");
            await Task.WhenAll(
                registry.RegisterHostAsync(hostId, "http://localhost:8080", new HostCapabilities()),
                secondRegistry.RegisterHostAsync(secondHostId, "http://localhost:8081", new HostCapabilities()));
            await registry.UpdateHostStatusAsync(hostId, new HostStatus
            {
                IsHealthy = true,
                CurrentWorkflowCount = 2,
                MaxConcurrentWorkflows = 10,
                Status = "Available"
            });

            Assert.Equal(hostId, (await registry.GetHostAsync(hostId))?.HostId);
            Assert.Equal(2, (await registry.GetRegisteredHostsAsync()).Length);
            Assert.Equal(2, Assert.Single(await registry.GetAllHostStatusAsync()).CurrentWorkflowCount);
        }
        finally
        {
            await master.ExecuteAsync($"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
            await master.ExecuteAsync($"DROP DATABASE [{databaseName}]");
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task MetricsPruningKeepsRecentSamples()
    {
        var connectionString = Environment.GetEnvironmentVariable("IXIFLOW_TEST_SQL_CONNECTION_STRING")!;
        var registry = new SqlHostRegistry(connectionString);
        var hostId = Guid.NewGuid().ToString("N");
        await registry.RegisterHostAsync(hostId, "http://localhost:8080", new HostCapabilities());
        try
        {
            await registry.UpdateHostStatusAsync(hostId, new HostStatus { Status = "Available" });
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync("""
                INSERT INTO dbo.HostMetrics
                    (Id, HostId, CurrentWorkflowCount, MaxWorkflowCount, CpuUsage, MemoryUsage, Status, RecordedAt)
                VALUES (@Id, @HostId, 0, 10, 0, 0, 'Old', DATEADD(DAY, -40, SYSUTCDATETIME()))
                """, new { Id = Guid.NewGuid().ToString("N"), HostId = hostId });

            var maintenance = Assert.IsAssignableFrom<IHostRegistryMaintenance>(registry);
            Assert.Equal(1, await maintenance.PruneMetricsAsync(TimeSpan.FromDays(30), 100));
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.HostMetrics WHERE HostId = @HostId", new { HostId = hostId }));
        }
        finally
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync("DELETE FROM dbo.HostMetrics WHERE HostId = @HostId", new { HostId = hostId });
            await connection.ExecuteAsync("DELETE FROM dbo.HostRegistry WHERE HostId = @HostId", new { HostId = hostId });
        }
    }

    [Fact]
    public async Task BackgroundMaintenanceCleansStaleHostsAndMetrics()
    {
        var maintained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new Mock<IHostRegistry>();
        var hostMaintenance = registry.As<IHostRegistryMaintenance>();
        registry.Setup(x => x.CleanupStaleHostsAsync(TimeSpan.FromSeconds(30)))
            .ReturnsAsync(0);
        hostMaintenance.Setup(x => x.PruneMetricsAsync(TimeSpan.FromDays(7), 1000,
                It.IsAny<CancellationToken>()))
            .Callback(() => maintained.TrySetResult())
            .ReturnsAsync(0);
        using var service = new WorkflowMessageCleanupService(
            new Mock<IMessageBus>().Object, registry.Object,
            new WorkflowHostOptions
            {
                HealthCheckInterval = TimeSpan.FromSeconds(10),
                HostMetricsRetention = TimeSpan.FromDays(7)
            }, NullLogger<WorkflowMessageCleanupService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await maintained.Task.WaitAsync(TimeSpan.FromSeconds(5));
            registry.Verify(x => x.CleanupStaleHostsAsync(TimeSpan.FromSeconds(30)), Times.Once);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task RequiredHostTagsMatchWholeTags()
    {
        var connectionString = Environment.GetEnvironmentVariable("IXIFLOW_TEST_SQL_CONNECTION_STRING")!;
        var registry = new SqlHostRegistry(connectionString);
        var matchingId = Guid.NewGuid().ToString("N");
        var substringId = Guid.NewGuid().ToString("N");
        await registry.RegisterHostAsync(matchingId, "http://localhost:8081",
            new HostCapabilities { Tags = ["gpu", "eu"] });
        await registry.RegisterHostAsync(substringId, "http://localhost:8082",
            new HostCapabilities { Tags = ["notgpu", "eu"] });
        try
        {
            var hosts = await registry.FindHostsByTagsAsync(["gpu", "eu"]);
            Assert.Contains(hosts, host => host.HostId == matchingId);
            Assert.DoesNotContain(hosts, host => host.HostId == substringId);
        }
        finally
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                "DELETE FROM dbo.HostRegistry WHERE HostId IN @HostIds",
                new { HostIds = new[] { matchingId, substringId } });
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public async Task UnhealthyHostIsNotRoutable()
    {
        var connectionString = Environment.GetEnvironmentVariable("IXIFLOW_TEST_SQL_CONNECTION_STRING")!;
        var registry = new SqlHostRegistry(connectionString);
        var hostId = Guid.NewGuid().ToString("N");
        await registry.RegisterHostAsync(hostId, "http://localhost:8080", new HostCapabilities());
        try
        {
            await registry.UpdateHostStatusAsync(hostId, new HostStatus
            {
                IsHealthy = false,
                Status = "Unhealthy"
            });

            Assert.Null(await registry.GetHostAsync(hostId));
            Assert.DoesNotContain(await registry.GetActiveHostsAsync(), host => host.HostId == hostId);
            Assert.Contains(await registry.GetAllHostStatusAsync(), host =>
                host.HostId == hostId && !host.IsHealthy);
        }
        finally
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync("DELETE FROM dbo.HostMetrics WHERE HostId = @HostId", new { HostId = hostId });
            await connection.ExecuteAsync("DELETE FROM dbo.HostRegistry WHERE HostId = @HostId", new { HostId = hostId });
        }
    }
}
