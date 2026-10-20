using System.Collections.Concurrent;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "host")]
[Trait("Spec", "B-04")]
[Trait("Spec", "C-02")]
[Trait("Spec", "C-08")]
public sealed class ClaimHandoffTests : IClassFixture<SqliteTestDatabase<ClaimHandoffTests>>
{
    [Fact]
    public Task MemoryHandoff() => Contract(new InMemoryWorkflowStateRepository());

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    public async Task SqliteHandoff()
    {
        await using var store = new SqliteWorkflowStateRepository(SqliteTestDatabase<ClaimHandoffTests>.ConnectionString);
        await Contract(store);
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlServerHandoff() => Contract(new SqlWorkflowStateRepository(SqlTestDatabase<ClaimHandoffTests>.ConnectionString));

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlHandoff() => Contract(new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<ClaimHandoffTests>.ConnectionString));

    private static ServiceProvider Provider(IWorkflowStateRepository store, string name, ConcurrentQueue<string> ran, TimeProvider? time = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow(options => { options.Slots = 1; options.HostName = name; options.Capabilities["gpu"] = name == "matching" ? "cuda" : "other"; });
        services.Replace(ServiceDescriptor.Singleton(store));
        services.Replace(ServiceDescriptor.Singleton(new InProcessInstanceGate()));
        services.AddSingleton(new Probe(name, ran));
        if (time != null) services.AddSingleton(time);
        return services.BuildServiceProvider();
    }

    private static async Task Contract(IWorkflowStateRepository store)
    {
        var ran = new ConcurrentQueue<string>();
        await using var wrong = Provider(store, "wrong", ran);
        await using var matching = Provider(store, "matching", ran);
        var definition = Workflow.Create<Data>("Handoff").Demand("gpu", "cuda")
            .Step<Mark>().WaitFor<Go>("go").Step<Mark>().Build();
        await wrong.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(definition);
        await matching.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(definition);
        await using var wrongScope = wrong.CreateAsyncScope();
        await using var matchingScope = matching.CreateAsyncScope();
        var from = wrongScope.ServiceProvider.GetRequiredService<WorkflowHost.InstanceOperations>();
        var to = matchingScope.ServiceProvider.GetRequiredService<WorkflowHost.InstanceOperations>();
        var notified = new List<string>();
        wrongScope.ServiceProvider.GetRequiredService<RunControl>().OnRunnable(notified.Add);
        var start = await from.StartAsync(definition, new Data(), new(), CancellationToken.None);
        Assert.Equal(WorkflowExecutionStatus.Scheduled, start.Status);
        Assert.Empty(ran);
        Assert.Contains(start.InstanceId, notified);
        var saved = (await store.GetWorkflowInstanceAsync(start.InstanceId))!;
        Assert.Equal(1, saved.Revision); // creation and lease release are one commit
        Assert.NotNull(saved.RunnableAtUtc);
        var before = saved.LastHostId;
        Assert.Equal(WorkflowExecutionStatus.Scheduled, (await from.RecoverAsync(start.InstanceId, CancellationToken.None)).Status);
        Assert.Equal(before, (await store.GetWorkflowInstanceSummaryAsync(start.InstanceId))!.LastHostId);
        Assert.Equal(WorkflowExecutionStatus.Suspended, (await to.RecoverAsync(start.InstanceId, CancellationToken.None)).Status);
        Assert.Equal(new[] { "matching" }, ran);
        Assert.Equal(WorkflowExecutionStatus.Scheduled,
            (await from.ResumeAsync(start.InstanceId, "go", new Go(), CancellationToken.None)).Status);
        Assert.Equal(new[] { "matching" }, ran);
        Assert.Equal(matching.GetRequiredService<HostSettings>().Id,
            (await store.GetWorkflowInstanceSummaryAsync(start.InstanceId))!.LastHostId);
        Assert.Equal(WorkflowExecutionStatus.Success, (await to.RecoverAsync(start.InstanceId, CancellationToken.None)).Status);
        Assert.Equal(new[] { "matching", "matching" }, ran);

        // A real resolution checkpoint takes the separate operator lease, with the same placement rule.
        var resolutionDefinition = Workflow.Create<Data>("PlacedResolution").Demand("gpu", "cuda").Step<Mark>().Build();
        await wrong.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(resolutionDefinition);
        await matching.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(resolutionDefinition);
        var (instance, checkpoint, scopes) = WorkflowInstances.NewInstance(resolutionDefinition, new Data(), new(), DateTime.UtcNow);
        instance.Status = WorkflowStatus.NeedsResolution;
        var invocation = new ActivityInvocationState { StepId = scopes.Id(resolutionDefinition.Steps[0]) };
        checkpoint.Continuations[0].Status = ContinuationStatus.WaitingResolution;
        checkpoint.Continuations[0].PendingActivity = invocation;
        ExecutionCheckpointWriter.Prepare(instance, checkpoint, new Data(), scopes, DateTime.UtcNow);
        var seed = (await store.TryAcquireExecutionLeaseAsync(instance.InstanceId, "seed", TimeSpan.FromMinutes(1),
            new ClaimQuery(Guid.NewGuid(), new Dictionary<string, string>())))!;
        Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(
            CheckpointCommit.Create(instance, seed.Epoch, 0, wakeAfter: TimeSpan.FromHours(1)))).Status);
        Assert.True((await store.GetWorkflowInstanceSummaryAsync(instance.InstanceId))!.RunnableAtUtc > DateTime.UtcNow);
        var decision = ActivityResolution.Completed(new Dictionary<string, object?>());
        Assert.Equal(WorkflowExecutionStatus.Scheduled,
            (await from.ResolveAsync(instance.InstanceId, invocation.Id, decision, CancellationToken.None)).Status);
        Assert.Equal(WorkflowExecutionStatus.Success,
            (await to.ResolveAsync(instance.InstanceId, invocation.Id, decision, CancellationToken.None)).Status);
        Assert.Equal(2, ran.Count); // observed completion never executes Mark again
    }

    [Fact]
    public async Task RunAndSendAwaitAHandoffWithoutHoldingTheOnlySlot()
    {
        var store = new InMemoryWorkflowStateRepository();
        var ran = new ConcurrentQueue<string>();
        await using var wrong = Provider(store, "wrong", ran);
        await using var matching = Provider(store, "matching", ran);
        var definition = Workflow.Create<Data>("AwaitHandoff").Demand("gpu", "cuda")
            .Step<Mark>().WaitFor<Go>("go").Step<Mark>().Build();
        await matching.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(definition);
        var client = wrong.GetRequiredService<IWorkflowClient>();
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var id = Guid.NewGuid().ToString("N");
        var run = client.RunAsync(definition, new Data(), new() { InstanceId = id }, patience.Token);
        while (await store.GetWorkflowInstanceSummaryAsync(id) == null) await Task.Delay(10, patience.Token);
        Assert.False(run.IsCompleted);
        Assert.Equal(WorkflowExecutionStatus.Success, (await client.RunAsync(
            Workflow.Create<Data>("OtherWork").Step<Noop>().Build(), new Data(), cancellationToken: patience.Token)).Status);
        await using var scope = matching.CreateAsyncScope();
        var to = scope.ServiceProvider.GetRequiredService<WorkflowHost.InstanceOperations>();
        await to.RecoverAsync(id, patience.Token);
        Assert.Equal(WorkflowExecutionStatus.Suspended, (await run).Status);
        var send = client.ResumeAsync(id, "go", new Go(), patience.Token);
        while ((await store.GetPendingEventsAsync(id, 0, 10)).Events.Count == 0) await Task.Delay(10, patience.Token);
        Assert.False(send.IsCompleted);
        await to.RecoverAsync(id, patience.Token);
        Assert.Equal(WorkflowExecutionStatus.Success, (await send).Status);
        Assert.All(ran, name => Assert.Equal("matching", name));
    }

    [Theory]
    [InlineData(false, -10)]
    [InlineData(false, 10)]
    [InlineData(true, -10)]
    [InlineData(true, 10)]
    public async Task HostPinChecksUseStoreTimeWithSkewedHostClocks(bool sqlite, int skewYears)
    {
        var storeTime = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var hostTime = new FakeTimeProvider(storeTime.GetUtcNow().AddYears(skewYears));
        IWorkflowStateRepository store = sqlite
            ? new SqliteWorkflowStateRepository(SqliteTestDatabase<ClaimHandoffTests>.ConnectionString, storeTime)
            : new InMemoryWorkflowStateRepository(storeTime);
        try
        {
            var ran = new ConcurrentQueue<string>();
            await using var first = Provider(store, "matching", ran, hostTime);
            await using var second = Provider(store, "matching", ran, hostTime);
            var definition = Workflow.Create<Data>("ClockedPin").Step<Mark>().WaitFor<Go>("go").Step<Mark>().Build();
            await first.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(definition);
            await second.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(definition);
            await using var firstScope = first.CreateAsyncScope();
            await using var secondScope = second.CreateAsyncScope();
            var from = firstScope.ServiceProvider.GetRequiredService<WorkflowHost.InstanceOperations>();
            var to = secondScope.ServiceProvider.GetRequiredService<WorkflowHost.InstanceOperations>();
            var started = await from.StartAsync(definition, new Data(),
                new() { RequiredHostId = first.GetRequiredService<HostSettings>().Id }, CancellationToken.None);
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
            Assert.Equal(WorkflowExecutionStatus.Scheduled,
                (await to.ResumeAsync(started.InstanceId, "go", new Go(), CancellationToken.None)).Status);
            Assert.Single(ran);
            storeTime.Advance(TimeSpan.FromMinutes(5));
            Assert.Equal(WorkflowExecutionStatus.Success, (await to.RecoverAsync(started.InstanceId, CancellationToken.None)).Status);
            Assert.Equal(2, ran.Count);
        }
        finally { if (store is IAsyncDisposable disposable) await disposable.DisposeAsync(); }
    }

    public sealed class Data;
    public sealed class Go;
    public sealed record Probe(string Name, ConcurrentQueue<string> Ran);
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public sealed class Mark(Probe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { probe.Ran.Enqueue(probe.Name); return Task.CompletedTask; }
    }
}
