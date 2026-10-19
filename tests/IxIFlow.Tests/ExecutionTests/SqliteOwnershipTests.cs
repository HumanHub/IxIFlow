using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "registration")]
[Trait("Spec", "C-18")]
[Trait("Category", "SqliteIntegration")]
public sealed class SqliteOwnershipTests : IClassFixture<SqliteTestDatabase<SqliteOwnershipTests>>
{
    private static readonly string ConnectionString = SqliteTestDatabase<SqliteOwnershipTests>.ConnectionString;

    private static TestHost Host(string connectionString)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        services.AddIxIFlow(options => options.ReplyTokenKeys = TestReplyKeys.Ring).UseSqlite(connectionString);
        return new TestHost(services.BuildServiceProvider());
    }

    private static Task StartAsync(TestHost host) => host.StartAsync();

    private sealed class TestHost(ServiceProvider services) : IServiceProvider, IAsyncDisposable
    {
        private IHostedService[] _hosted = [];

        public object? GetService(Type serviceType) => services.GetService(serviceType);

        public async Task StartAsync()
        {
            _hosted = services.GetServices<IHostedService>().ToArray();
            foreach (var service in _hosted)
                await service.StartAsync(CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            // Match the generic host: stop recovery and drain runs before releasing ownership.
            try
            {
                foreach (var service in _hosted.Reverse())
                    await service.StopAsync(CancellationToken.None);
            }
            finally
            {
                await services.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task DirectConstructionTakesOwnershipUntilDisposal()
    {
        var first = new SqliteWorkflowStateRepository(ConnectionString);
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() => new SqliteWorkflowStateRepository(ConnectionString));
            Assert.Contains("already owned", error.Message);
        }
        finally
        {
            await first.DisposeAsync();
        }
        await using var restarted = new SqliteWorkflowStateRepository(ConnectionString);
        await Task.WhenAll((await restarted.QueryInstancesAsync(new InstanceQuery(Statuses: [WorkflowStatus.Suspended], Limit: int.MaxValue))).Items.Select(async row => (await restarted.GetWorkflowInstanceAsync(row.InstanceId))!));
    }

    [Fact]
    public void AMissingFolderIsNotReportedAsAlreadyOwned()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "workflows.db");
        var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = file }.ConnectionString;

        Assert.Throws<DirectoryNotFoundException>(() => new SqliteWorkflowStateRepository(connectionString));
    }

    [Theory]
    [InlineData(@"\\server\share\workflows.db")]
    [InlineData("//server/share/workflows.db")]
    public void UncPathsAreRefused(string file)
    {
        var connectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = file }.ConnectionString;

        Assert.Contains("local", Assert.Throws<ArgumentException>(() =>
            new SqliteWorkflowStateRepository(connectionString)).Message);
    }

    [Fact]
    public async Task ASecondHostFailsAtStartupBeforeItHasWork()
    {
        await using var first = Host(ConnectionString);
        await StartAsync(first);
        await using var second = Host(ConnectionString);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(second));
        Assert.Contains("already owned", error.Message);
        Assert.Contains("one host and no coordinator", error.Message);
    }

    [Fact]
    public async Task ADisposedHostLeavesNoStaleOwnership()
    {
        await using (var first = Host(ConnectionString))
            await StartAsync(first);
        await using var restarted = Host(ConnectionString);
        await StartAsync(restarted);
        Assert.IsType<SqliteWorkflowStateRepository>(restarted.GetRequiredService<IWorkflowStateRepository>());
    }

    [Fact]
    public async Task AnExistingOwnerFilePreventsStartupAndCanBeReclaimedAfterItsHandleCloses()
    {
        var path = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(ConnectionString).DataSource + ".ixiflow-owner";
        using (var owner = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await using var host = Host(ConnectionString);
            await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(host));
        }
        // The file remains, as after a crash; the OS handle decides whether there is still an owner.
        Assert.True(File.Exists(path));
        await using var recovered = Host(ConnectionString);
        await StartAsync(recovered);
    }

    [Fact]
    [Trait("Spec", "C-11")]
    public async Task TheBuilderInjectsTheStoresClock()
    {
        await using var host = Host(ConnectionString);
        var store = host.GetRequiredService<IWorkflowStateRepository>();
        var time = host.GetRequiredService<TimeProvider>();
        var instance = new WorkflowInstance { ExecutionStateJson = StoreTestSupport.CheckpointJson, InstanceId = Guid.NewGuid().ToString("N"), Status = WorkflowStatus.Suspended };
        await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, (await StoreTestSupport.LeaseAsync(store, instance.InstanceId)).Epoch, 0,
            openedWaits: [new OpenedWait("delay", WaitKind.Delay, "", "", TimeSpan.FromMinutes(1))]));
        Assert.Equal(time.GetUtcNow().UtcDateTime.AddMinutes(1),
            (await store.GetWaitAsync(instance.InstanceId, "delay"))!.DeadlineUtc);
    }

    [Fact]
    [Trait("Spec", "C-11")]
    public void ASecondStoreChoiceIsRefused()
    {
        var builder = new ServiceCollection().AddLogging().AddIxIFlow(options => options.ReplyTokenKeys = TestReplyKeys.Ring).UseSqlite(ConnectionString);
        Assert.Contains("choose one store", Assert.Throws<InvalidOperationException>(() =>
            builder.UseSqlite(ConnectionString)).Message);
        var other = new ServiceCollection().AddLogging().AddIxIFlow(options => options.ReplyTokenKeys = TestReplyKeys.Ring).UsePostgreSql("Host=unused");
        Assert.Contains("choose one store", Assert.Throws<InvalidOperationException>(() =>
            other.UseSqlite(ConnectionString)).Message);
    }

    [Fact]
    [Trait("Spec", "C-11")]
    public async Task WalPersistsAcrossConnections()
    {
        await using var store = new SqliteWorkflowStateRepository(ConnectionString, new FakeTimeProvider());
        var instance = new WorkflowInstance { ExecutionStateJson = StoreTestSupport.CheckpointJson, InstanceId = Guid.NewGuid().ToString("N") };
        await store.SeedAsync(instance);
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var mode = new Microsoft.Data.Sqlite.SqliteCommand("PRAGMA journal_mode", connection);
        Assert.Equal("wal", await mode.ExecuteScalarAsync());
    }

    [Fact]
    [Trait("Spec", "C-11")]
    public async Task UseSqliteKeepsInstancesInTheDatabase()
    {
        await using var host = Host(ConnectionString);
        var repository = host.GetRequiredService<IWorkflowStateRepository>();
        Assert.IsType<SqliteWorkflowStateRepository>(repository);
        var instance = new WorkflowInstance
        { ExecutionStateJson = StoreTestSupport.CheckpointJson,
            InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "SQLiteRegistration",
            Status = WorkflowStatus.Suspended, WorkflowDataJson = "{}"
        };
        await repository.SeedAsync(instance);

        await host.DisposeAsync();
        await using var reopened = new SqliteWorkflowStateRepository(ConnectionString);
        Assert.Equal(WorkflowStatus.Suspended, (await reopened.GetWorkflowInstanceAsync(instance.InstanceId))!.Status);
    }
}
