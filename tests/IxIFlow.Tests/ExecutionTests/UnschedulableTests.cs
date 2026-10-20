using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "host")]
[Trait("Spec", "C-10")]
public sealed class UnschedulableTests
{
    [Fact]
    public Task MemoryContract() => Contract(new InMemoryWorkflowStateRepository());

    [Fact, Trait("Category", "SqliteIntegration")]
    public async Task SqliteContract()
    {
        await using var store = new SqliteWorkflowStateRepository(SqliteTestDatabase<UnschedulableTests>.ConnectionString);
        await Contract(store);
    }

    [SqlServerFact, Trait("Category", "SqlIntegration")]
    public Task SqlServerContract() => Contract(new SqlWorkflowStateRepository(SqlTestDatabase<UnschedulableTests>.ConnectionString));

    [PostgreSqlFact, Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlContract() => Contract(new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<UnschedulableTests>.ConnectionString));

    internal static MemberRegistration Host(Guid id, IReadOnlyDictionary<string, string> capabilities,
        TimeSpan? interval = null) => new(id, MemberKind.Host, "diagnostics", null, 4, "test",
        interval ?? TimeSpan.FromMinutes(1), capabilities);

    internal static async Task<WorkflowInstance> Seed(IWorkflowStateRepository store, WorkflowDemand[] demands,
        TimeSpan? park = null)
    {
        var instance = new WorkflowInstance
        {
            InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "UnschedulableContract", WorkflowVersion = 7,
            Status = WorkflowStatus.Running, WorkflowDataJson = "{}", WorkflowDataType = typeof(object).AssemblyQualifiedName!,
            ExecutionStateJson = JsonSerializer.Serialize(new ExecutionCheckpoint
            {
                StructureHash = "saved-hash", Demands = demands.ToList(),
                Continuations = [new() { Id = "root", Stack = [new() { ScopeId = "root" }] }]
            })
        };
        var grant = (await store.TryAcquireExecutionLeaseAsync(instance.InstanceId, "seed", TimeSpan.FromMinutes(1),
            new ClaimQuery(Guid.NewGuid(), new Dictionary<string, string>())))!;
        Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(
            instance, grant.Epoch, 0, parks: park != null, wakeAfter: park))).Status);
        await store.ReleaseExecutionLeaseAsync(instance.InstanceId, "seed");
        return (await store.GetWorkflowInstanceAsync(instance.InstanceId))!;
    }

    private static async Task Contract(IWorkflowStateRepository store)
    {
        var host = Guid.NewGuid();
        var caps = new Dictionary<string, string> { ["host"] = host.ToString("D"), ["region"] = "eu",
            ["workflow:UnschedulableContract@7"] = "changed-hash" };
        var ids = new List<string>();
        async Task<WorkflowInstance> Add(WorkflowDemand[] demands, TimeSpan? park = null)
        {
            var row = await Seed(store, demands, park);
            ids.Add(row.InstanceId);
            return row;
        }
        await store.RegisterMemberAsync(Host(host, caps));
        try
        {
            var missing = await Add([new("gpu"), new("region", "eu"), new("obsolete", "x", DateTime.UnixEpoch)]);
            var matched = await Add([new("region", "eu")]);
            // Whole seconds: PostgreSQL keeps microseconds, not .NET ticks.
            var deadPin = new WorkflowDemand("host", Guid.NewGuid().ToString("D"),
                DateTime.UnixEpoch.AddSeconds(Math.Floor((DateTime.UtcNow.AddHours(1) - DateTime.UnixEpoch).TotalSeconds)));
            var pinned = await Add([new("region", "eu"), deadPin]);
            var expired = await Add([new("region", "eu"), deadPin with { ExpiresAtUtc = DateTime.UnixEpoch }]);
            var future = await Add([new("future-only")], TimeSpan.FromHours(1));
            var hashed = await Add([new("workflow:UnschedulableContract@7", "saved-hash")]);
            var leased = await Add([new("leased-only")]);
            Assert.NotNull(await store.TryAcquireExecutionLeaseAsync(leased.InstanceId, "active", TimeSpan.FromMinutes(1),
                new ClaimQuery(host, new Dictionary<string, string> { ["leased-only"] = "" })));
            var page = await store.GetUnschedulableAsync();
            Assert.Equal(new WorkflowDemand("gpu"), Assert.Single(page.Items.Single(r => r.InstanceId == missing.InstanceId).UnmetDemands));
            Assert.Equal(deadPin, Assert.Single(page.Items.Single(r => r.InstanceId == pinned.InstanceId).UnmetDemands));
            var hashRow = page.Items.Single(r => r.InstanceId == hashed.InstanceId);
            Assert.Equal("UnschedulableContract", hashRow.WorkflowName);
            Assert.Equal(7, hashRow.WorkflowVersion);
            Assert.Equal(hashed.RunnableAtUtc, hashRow.RunnableAtUtc);
            Assert.Equal(new WorkflowDemand("workflow:UnschedulableContract@7", "saved-hash"), Assert.Single(hashRow.UnmetDemands));
            Assert.DoesNotContain(page.Items, r => new[] { matched.InstanceId, expired.InstanceId, future.InstanceId, leased.InstanceId }.Contains(r.InstanceId));

            // Cursor order is global and stable across sets, including repeated reads of a page.
            var expected = page.Items.Select(r => r.InstanceId).ToArray();
            var actual = new List<string>();
            UnschedulableCursor? cursor = null;
            do
            {
                var next = await store.GetUnschedulableAsync(1, cursor);
                Assert.Equal(next.Items.Select(r => r.InstanceId), (await store.GetUnschedulableAsync(1, cursor)).Items.Select(r => r.InstanceId));
                actual.AddRange(next.Items.Select(r => r.InstanceId));
                cursor = next.NextCursor;
            } while (cursor != null);
            Assert.Equal(expected, actual);

            // Same persisted capability snapshot and liveness rule as the Fail policy.
            Assert.True(await store.CanScheduleAsync([new("region", "eu"), new("obsolete", "x", DateTime.UnixEpoch)]));
            Assert.False(await store.CanScheduleAsync([new("gpu")]));
            await store.RegisterMemberAsync(Host(host, caps, TimeSpan.FromMilliseconds(1)));
            await Task.Delay(30);
            Assert.False(await store.CanScheduleAsync([new("region", "eu")]));
            Assert.Contains((await store.GetUnschedulableAsync()).Items, r => r.InstanceId == matched.InstanceId);
        }
        finally
        {
            foreach (var id in ids) await store.DeleteWorkflowInstanceAsync(id);
            await store.UnregisterMemberAsync(host);
        }
    }

    [Fact]
    public async Task DifferentLiveHostsCannotSatisfyACombination()
    {
        var store = new InMemoryWorkflowStateRepository();
        await store.RegisterMemberAsync(Host(Guid.NewGuid(), new Dictionary<string, string> { ["gpu"] = "" }));
        await store.RegisterMemberAsync(Host(Guid.NewGuid(), new Dictionary<string, string> { ["region"] = "eu" }));
        var demands = new WorkflowDemand[] { new("gpu"), new("region", "eu") };
        await Seed(store, demands);
        Assert.Equal(demands, Assert.Single((await store.GetUnschedulableAsync()).Items).UnmetDemands);
        Assert.False(await store.CanScheduleAsync(demands));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoreClockControlsExactLivenessAndExpiry(bool sqlite)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2035, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var local = sqlite ? new SqliteWorkflowStateRepository(
            SqliteTestDatabase<UnschedulableClock>.ConnectionString, time) : null;
        IWorkflowStateRepository store = local ?? (IWorkflowStateRepository)new InMemoryWorkflowStateRepository(time);
        var id = Guid.NewGuid();
        var caps = new Dictionary<string, string> { ["host"] = id.ToString("D"), ["region"] = "eu" };
        await store.RegisterMemberAsync(Host(id, caps, TimeSpan.FromSeconds(5)));
        var expires = time.GetUtcNow().UtcDateTime.AddSeconds(15);
        var pinned = await Seed(store, [new("region", "eu"), new("host", Guid.NewGuid().ToString("D"), expires)]);
        Assert.Equal("host", Assert.Single(Assert.Single((await store.GetUnschedulableAsync()).Items).UnmetDemands).Key);
        time.Advance(TimeSpan.FromSeconds(15));
        // At exactly three intervals the host is dead, and at exact pin expiry the pin is ignored.
        Assert.False(await store.CanScheduleAsync([new("region", "eu")]));
        Assert.Equal(new WorkflowDemand("region", "eu"), Assert.Single(Assert.Single((await store.GetUnschedulableAsync()).Items).UnmetDemands));
        await store.HeartbeatAsync(id, caps);
        Assert.Empty((await store.GetUnschedulableAsync()).Items);
        Assert.Equal(2, (await store.GetWorkflowDemandsAsync(pinned.InstanceId)).Count);
    }

    [Fact]
    public async Task OperatorReadUsesTheConfiguredStore()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow();
        using var provider = services.BuildServiceProvider();
        await Seed(provider.GetRequiredService<IWorkflowStateRepository>(), [new("missing")]);
        Assert.Single((await provider.GetRequiredService<IWorkflowOperations>().GetUnschedulableAsync()).Items);
        Assert.Single((await provider.GetRequiredService<IWorkflowClient>().GetUnschedulableAsync()).Items);
    }

    [Fact]
    public async Task ChangedDefinitionAdvertisesOnlyItsNewStructureHash()
    {
        var original = Workflow.Create<HashData>("ChangedDefinition").Step<HashNoop>().WaitFor<HashEvent>("original").Build();
        var changed = Workflow.Create<HashData>("ChangedDefinition").Step<HashNoop>().WaitFor<HashEvent>("changed").Build();
        var savedHash = new WorkflowScopeCatalog(original).StructureHash;
        Assert.NotEqual(savedHash, new WorkflowScopeCatalog(changed).StructureHash);
        var store = new InMemoryWorkflowStateRepository();
        await Seed(store, WorkflowDemands.Resolve(original, new HashData(), new WorkflowOptions(), savedHash).ToArray());
        var registry = new WorkflowCatalog();
        await registry.RegisterWorkflowAsync(changed);
        var settings = HostSettings.Default;
        var capabilities = await WorkflowHeartbeatService.CapabilitiesAsync(registry, settings);
        await store.RegisterMemberAsync(Host(settings.Id, capabilities));
        var missing = Assert.Single(Assert.Single((await store.GetUnschedulableAsync()).Items).UnmetDemands);
        Assert.Equal(new WorkflowDemand("workflow:ChangedDefinition@1", savedHash), missing);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailAdmissionUsesStoreLivenessAndIgnoresExpiredDemands(bool sqlite)
    {
        var time = new FakeTimeProvider();
        await using var local = sqlite ? new SqliteWorkflowStateRepository(
            SqliteTestDatabase<UnschedulableFail>.ConnectionString, time) : null;
        IWorkflowStateRepository store = local ?? (IWorkflowStateRepository)new InMemoryWorkflowStateRepository(time);
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow();
        services.AddSingleton(store);
        services.AddSingleton<TimeProvider>(time);
        using var provider = services.BuildServiceProvider();
        var definition = Workflow.Create<HashData>("FailExpiry").Step<HashNoop>().WaitFor<HashEvent>("wait").Build();
        var registry = provider.GetRequiredService<IWorkflowCatalog>();
        await registry.RegisterWorkflowAsync(definition);
        var settings = provider.GetRequiredService<HostSettings>();
        await store.RegisterMemberAsync(Host(settings.Id, await WorkflowHeartbeatService.CapabilitiesAsync(registry, settings), TimeSpan.FromSeconds(5)));
        var options = new WorkflowOptions { WhenUnschedulable = WhenUnschedulable.Fail,
            Demands = [new("expired", "absent", time.GetUtcNow().UtcDateTime)] };
        var client = provider.GetRequiredService<IWorkflowClient>();
        Assert.Equal(WorkflowExecutionStatus.Suspended, (await client.RunAsync(definition, new HashData(), options)).Status);
        time.Advance(TimeSpan.FromSeconds(15));
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => client.RunAsync(definition, new HashData(), options));
        Assert.Equal(WorkflowErrorKind.Unschedulable, error.Kind);
    }

    private sealed class UnschedulableClock;
    private sealed class UnschedulableFail;
    public sealed class HashData;
    public sealed class HashEvent;
    public sealed class HashNoop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

