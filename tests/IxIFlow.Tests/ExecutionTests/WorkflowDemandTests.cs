using System.Text.Json;
using Dapper;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "host")]
[Trait("Spec", "C-07")]
[Trait("Spec", "D-07")]
public sealed class WorkflowDemandTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefinitionRegistrationAndStartMergeOnce(bool explicitDataType)
    {
        var store = new InMemoryWorkflowStateRepository();
        var services = Services(store);
        if (explicitDataType)
            services.RegisterWorkflow<Declared, Data>(r => r.Demand("region", inherit: true).Demand("gpu"));
        else services.RegisterWorkflow<Declared>(r => r.Demand("region", inherit: true).Demand("gpu"));
        await using var provider = services.BuildServiceProvider();
        var definition = Assert.Single(provider.GetServices<WorkflowDefinition>());
        var result = await provider.GetRequiredService<IWorkflowClient>().RunAsync(definition,
            new Data { Region = "eu" }, new WorkflowOptions { Demands = [new("gpu", "cuda"), new("region", "eu")] });
        var demands = await store.GetWorkflowDemandsAsync(result.InstanceId);
        Assert.Equal(new[] { "gpu", "region", "workflow:Declared@1" }, demands.Select(d => d.Key));
        Assert.Equal(new WorkflowDemand("region", "eu", Inherit: true), demands[1]);
        Assert.Equal("cuda", demands[0].Value);
        Assert.Equal(demands, WorkflowDemands.Saved((await store.GetWorkflowInstanceSummaryAsync(result.InstanceId))!));
    }

    [Fact]
    public async Task EquivalentCallerDefinitionStillIncludesRegistrationPolicy()
    {
        var store = new InMemoryWorkflowStateRepository();
        var services = Services(store);
        services.RegisterWorkflow<Declared>(r => r.Demand("zone", "eu", inherit: true));
        await using var provider = services.BuildServiceProvider();
        var standalone = ServiceCollectionExtensions.BuildWorkflowDefinition<Declared, Data>(new Declared());
        var client = provider.GetRequiredService<IWorkflowClient>();
        var result = await client.RunAsync(standalone, new Data { Region = "eu" });
        Assert.Contains(new WorkflowDemand("zone", "eu", Inherit: true), await store.GetWorkflowDemandsAsync(result.InstanceId));
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => client.RunAsync(standalone, new Data(),
            new() { Demands = [new("zone", "us")] }));
        Assert.Equal(WorkflowErrorKind.InvalidRequest, error.Kind);
    }

    [Fact]
    public void PlacementDoesNotChangeStructureIdentity()
    {
        var plain = Definition();
        var placed = Workflow.Create<Data>("DemandFlow").Demand("gpu").Step<Noop>().WithId("first")
            .DemandFrom("region", ctx => ctx.WorkflowData.Region, inherit: true)
            .WaitFor<Go>("go").WithId("wait").Build();
        Assert.Equal(new WorkflowScopeCatalog(plain).StructureHash, new WorkflowScopeCatalog(placed).StructureHash);
        Assert.Equal(2, placed.Demands.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImpureDemandFromFailsAtBuild(bool workflowClock)
    {
        var builder = Workflow.Create<Data>("ImpureDemand");
        if (workflowClock) builder.DemandFrom("time", ctx => ctx.UtcNow.ToString());
        else builder.DemandFrom("host", ctx => Environment.MachineName);
        var error = Assert.Throws<WorkflowEngineException>(() => builder.Step<Noop>().Build());
        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
    }

    [Fact]
    public async Task ConflictsRefuseCreationAndKeyComparisonIsOrdinal()
    {
        var store = new InMemoryWorkflowStateRepository();
        await using var provider = Services(store).BuildServiceProvider();
        var definition = Workflow.Create<Data>("Contradiction").Demand("region", "eu").Step<Noop>().Build();
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => provider.GetRequiredService<IWorkflowClient>()
            .RunAsync(definition, new Data(), new WorkflowOptions { InstanceId = "contradiction", Demands = [new("region", "us")] }));
        Assert.Equal(WorkflowErrorKind.InvalidRequest, error.Kind);
        Assert.Null(await store.GetWorkflowInstanceAsync("contradiction"));
        Assert.Empty(await store.GetWorkflowDemandsAsync("contradiction"));
        var resolved = WorkflowDemands.Resolve(definition, new Data(), new() { Demands = [new("Region", "us")] }, "hash");
        Assert.Contains(resolved, d => d.Key == "Region" && d.Value == "us");
    }

    [Fact]
    public async Task RecoveryAndRepeatedStartKeepTheSavedStartValues()
    {
        var store = new InMemoryWorkflowStateRepository();
        var first = Definition("eu");
        string id;
        await using (var provider = Services(store).BuildServiceProvider())
            id = (await provider.GetRequiredService<IWorkflowClient>().RunAsync(first, new Data { Region = "eu" })).InstanceId;
        var before = await store.GetWorkflowDemandsAsync(id);
        // A repeated start must not evaluate its dynamic declaration against the replacement data.
        var changed = Workflow.Create<Data>("DemandFlow").Demand("region", "us")
            .DemandFrom("explode", ctx => ctx.WorkflowData.StartOnly)
            .Step<Noop>().WithId("first").WaitFor<Go>("go").WithId("wait").Build();
        var services = Services(store);
        services.AddSingleton(changed);
        await using var second = services.BuildServiceProvider();
        var client = second.GetRequiredService<IWorkflowClient>();
        await client.RunAsync(changed, new Data { Region = "us" }, new() { InstanceId = id });
        var result = await client.ResumeAsync(id, "go", new Go());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(before, await store.GetWorkflowDemandsAsync(id));
        Assert.Equal(before, WorkflowDemands.Saved((await store.GetWorkflowInstanceSummaryAsync(id))!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChildrenAndGrandchildrenInheritOnlyExplicitResidency(bool conflict)
    {
        var store = new InMemoryWorkflowStateRepository();
        var child = Workflow.Create<Data>("Child").Demand("region", conflict ? "us" : "eu")
            .Demand("child-only").Step<Noop>().Invoke<Data>("Grandchild", 1).Build();
        var grandchild = Workflow.Create<Data>("Grandchild").Step<Noop>().WaitFor<Go>("go").Build();
        var parent = Workflow.Create<Data>("Parent").Demand("region", inherit: true).Demand("region", "eu")
            .Demand("parent-only").Step<Noop>().Invoke<Data>("Child", 1).WithId("invoke").Build();
        var services = Services(store);
        services.AddSingleton(child);
        services.AddSingleton(grandchild);
        await using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<IWorkflowClient>().RunAsync(parent, new Data());
        if (conflict)
        {
            Assert.Equal(WorkflowExecutionStatus.NeedsResolution, result.Status);
            var saved = await store.GetWorkflowInstanceAsync(result.InstanceId);
            var pending = Assert.Single(ExecutionCheckpoint.Read(saved!.ExecutionStateJson).Continuations).PendingActivity!;
            Assert.Contains("Conflicting demands", pending.ResolutionReason);
            Assert.Empty(await store.GetChildInstancesAsync(result.InstanceId));
            return;
        }
        var savedChild = Assert.Single(await store.GetChildInstancesAsync(result.InstanceId));
        var childDemands = await store.GetWorkflowDemandsAsync(savedChild.InstanceId);
        Assert.Contains(new WorkflowDemand("region", "eu", Inherit: true), childDemands);
        Assert.DoesNotContain(childDemands, d => d.Key == "parent-only");
        // Explicit recovery makes this deterministic without starting a background recovery service.
        await provider.GetRequiredService<IWorkflowClient>().RecoverAsync(savedChild.InstanceId);
        var savedGrandchild = Assert.Single(await store.GetChildInstancesAsync(savedChild.InstanceId));
        var grandchildDemands = await store.GetWorkflowDemandsAsync(savedGrandchild.InstanceId);
        Assert.Contains(new WorkflowDemand("region", "eu", Inherit: true), grandchildDemands);
        Assert.DoesNotContain(grandchildDemands, d => d.Key == "child-only" || d.Key == "parent-only");
    }

    [Fact]
    public async Task ParallelForEachChildrenKeepTheSameResidencySnapshot()
    {
        var store = new InMemoryWorkflowStateRepository();
        var child = Workflow.Create<Data>("ParallelChild").Step<Noop>().WaitFor<Go>("go").Build();
        var parent = Workflow.Create<Data>("ParallelParent").DemandFrom("region", ctx => ctx.WorkflowData.Region, inherit: true)
            .Demand("parent-only").Step<Noop>()
            .ForEach(ctx => new[] { 1, 2, 3 }, (body, item) => body.Invoke<Data>("ParallelChild", 1),
                setup => setup.Parallel()).Build();
        var services = Services(store);
        services.AddSingleton(child);
        await using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<IWorkflowClient>().RunAsync(parent, new Data { Region = "eu" });
        var children = (await store.GetChildInstancesAsync(result.InstanceId)).ToArray();
        Assert.Equal(3, children.Length);
        foreach (var saved in children)
        {
            var demands = await store.GetWorkflowDemandsAsync(saved.InstanceId);
            Assert.Contains(new WorkflowDemand("region", "eu", Inherit: true), demands);
            Assert.DoesNotContain(demands, d => d.Key == "parent-only");
        }
    }

    [Theory]
    [InlineData("matching")]
    [InlineData("missing")]
    [InlineData("stale")]
    [InlineData("split")]
    [InlineData("coordinator")]
    public async Task FailRequiresOneLiveHostThatMeetsEveryDemand(string scenario)
    {
        var time = new FakeTimeProvider();
        var store = new InMemoryWorkflowStateRepository(time);
        var definition = Workflow.Create<Data>("Placement").Demand("gpu").Demand("region", "eu").Step<Noop>().Build();
        var demands = WorkflowDemands.Resolve(definition, new Data(), new(), new WorkflowScopeCatalog(definition).StructureHash);
        var capabilities = demands.ToDictionary(d => d.Key, d => d.Value ?? "present");
        if (scenario == "split") capabilities.Remove("region");
        if (scenario != "missing") await store.RegisterMemberAsync(new(Guid.NewGuid(),
            scenario == "coordinator" ? MemberKind.Coordinator : MemberKind.Host, "test", null, 1, "1",
            TimeSpan.FromSeconds(5), capabilities));
        if (scenario == "split") await store.RegisterMemberAsync(new(Guid.NewGuid(), MemberKind.Host, "other", null, 1,
            "1", TimeSpan.FromSeconds(5), new Dictionary<string, string> { ["region"] = "eu" }));
        if (scenario == "stale") time.Advance(TimeSpan.FromSeconds(15));
        await using var provider = Services(store).BuildServiceProvider();
        var client = provider.GetRequiredService<IWorkflowClient>();
        var options = new WorkflowOptions { InstanceId = "fail-" + scenario, WhenUnschedulable = WhenUnschedulable.Fail };
        if (scenario == "matching")
            Assert.Equal(WorkflowExecutionStatus.Success, (await client.RunAsync(definition, new Data(), options)).Status);
        else
        {
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => client.RunAsync(definition, new Data(), options));
            Assert.Equal(WorkflowErrorKind.Unschedulable, error.Kind);
            Assert.Null(await store.GetWorkflowInstanceAsync(options.InstanceId));
            Assert.Empty(await store.GetWorkflowDemandsAsync(options.InstanceId));
        }
        Assert.Equal(WhenUnschedulable.Wait, new WorkflowOptions().WhenUnschedulable);
        Assert.Equal(WorkflowExecutionStatus.Success, (await client.RunAsync(definition, new Data())).Status);
    }

    [Fact]
    public async Task AHostPinDoesNotOverrideOtherDemandsDuringFailAdmission()
    {
        var store = new InMemoryWorkflowStateRepository();
        var definition = Workflow.Create<Data>("Pinned").Demand("gpu", "cuda").Step<Noop>().Build();
        var pinned = Guid.NewGuid();
        Dictionary<string, string> Capabilities(Guid id, bool gpu)
        {
            var result = new Dictionary<string, string>
            {
                ["host"] = id.ToString("D"),
                ["workflow:Pinned@1"] = new WorkflowScopeCatalog(definition).StructureHash
            };
            if (gpu) result["gpu"] = "cuda";
            return result;
        }
        await store.RegisterMemberAsync(new(pinned, MemberKind.Host, "pinned", null, 1, "1",
            TimeSpan.FromSeconds(5), Capabilities(pinned, false)));
        var other = Guid.NewGuid();
        await store.RegisterMemberAsync(new(other, MemberKind.Host, "other", null, 1, "1",
            TimeSpan.FromSeconds(5), Capabilities(other, true)));
        await using var provider = Services(store).BuildServiceProvider();
        var client = provider.GetRequiredService<IWorkflowClient>();
        var options = new WorkflowOptions { RequiredHostId = pinned, WhenUnschedulable = WhenUnschedulable.Fail };
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => client.RunAsync(definition, new Data(), options));
        Assert.Equal(WorkflowErrorKind.Unschedulable, error.Kind);
        await store.HeartbeatAsync(pinned, Capabilities(pinned, true));
        Assert.Equal(WorkflowExecutionStatus.Success, (await client.RunAsync(definition, new Data(), options)).Status);
    }

    [Fact]
    public Task MemoryCreationIsAtomic() => StoreContract(new InMemoryWorkflowStateRepository());

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    public async Task SqliteCreationIsAtomic()
    {
        await using var store = new SqliteWorkflowStateRepository(SqliteTestDatabase<WorkflowDemandTests>.ConnectionString);
        await StoreContract(store);
    }

    [SqlServerFact]
    [Trait("Category", "SqlIntegration")]
    public Task SqlServerCreationIsAtomic() => StoreContract(new SqlWorkflowStateRepository(SqlTestDatabase<WorkflowDemandTests>.ConnectionString));

    [PostgreSqlFact]
    [Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlCreationIsAtomic() => StoreContract(new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<WorkflowDemandTests>.ConnectionString));

    private static async Task StoreContract(IWorkflowStateRepository store)
    {
        var id = Guid.NewGuid().ToString("N");
        var (instance, checkpoint, scopes) = WorkflowHost.InstanceOperations.NewInstance(Definition("eu"), new Data(),
            new() { InstanceId = id, RequiredHostId = Guid.NewGuid() }, DateTime.UtcNow.AddYears(20));
        ExecutionCheckpointWriter.Prepare(instance, checkpoint, new Data(), scopes, DateTime.UtcNow);
        var lease = (await store.TryAcquireExecutionLeaseAsync(id, "owner", TimeSpan.FromMinutes(1)))!;
        var childId = Guid.NewGuid().ToString("N");
        var wait = Guid.NewGuid().ToString("N");
        var (child, _, _) = WorkflowHost.InstanceOperations.NewInstance(
            Workflow.Create<Data>("ChildRows").Demand("residency", "eu", inherit: true).Step<Noop>().Build(),
            new Data(), new() { InstanceId = childId, ParentInstanceId = id }, DateTime.UtcNow);
        child.ParentWaitId = wait;
        var starts = new[] { new ChildStart(childId, JsonSerializer.Serialize(child), wait) };
        var waits = new[] { new OpenedWait(wait, WaitKind.Child, "", "IxIFlow.ChildCompleted", null) };
        var refused = await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, lease.Epoch, 1,
            childStarts: starts, openedWaits: waits));
        Assert.Equal(WorkflowCommitStatus.Conflict, refused.Status);
        Assert.Null(await store.GetWorkflowInstanceAsync(id));
        Assert.Empty(await store.GetWorkflowDemandsAsync(id));
        Assert.Null(await store.GetWorkflowInstanceAsync(childId));
        Assert.Empty(await store.GetWorkflowDemandsAsync(childId));
        var commit = CheckpointCommit.Create(instance, lease.Epoch, 0, childStarts: starts, openedWaits: waits);
        Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(commit)).Status);
        var saved = (await store.GetWorkflowInstanceAsync(id))!;
        var demands = await store.GetWorkflowDemandsAsync(id);
        Assert.Equal(demands, WorkflowDemands.Saved(saved));
        var pin = Assert.Single(demands, d => d.Key == "host");
        Assert.InRange(pin.ExpiresAtUtc!.Value, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(6));
        Assert.False(pin.Inherit);
        var childRows = await store.GetWorkflowDemandsAsync(childId);
        Assert.Equal(WorkflowDemands.Saved((await store.GetWorkflowInstanceAsync(childId))!), childRows);
        Assert.Contains(new WorkflowDemand("residency", "eu", Inherit: true), childRows);
        Assert.Equal(WorkflowCommitStatus.AlreadyApplied, (await store.CommitWorkflowInstanceAsync(commit)).Status);
        Assert.Equal(demands, await store.GetWorkflowDemandsAsync(id));
        // A taken child refuses an entire new parent creation, including its demand rows.
        var (loser, _, _) = WorkflowHost.InstanceOperations.NewInstance(Definition("us"), new Data(), new(), DateTime.UtcNow);
        child.ParentInstanceId = loser.InstanceId;
        var loserOwner = (await store.TryAcquireExecutionLeaseAsync(loser.InstanceId, "loser", TimeSpan.FromMinutes(1)))!;
        await Assert.ThrowsAnyAsync<Exception>(() => store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(loser,
            loserOwner.Epoch, 0, childStarts: [new(childId, JsonSerializer.Serialize(child), wait)], openedWaits: waits)));
        Assert.Null(await store.GetWorkflowInstanceAsync(loser.InstanceId));
        Assert.Empty(await store.GetWorkflowDemandsAsync(loser.InstanceId));
        Assert.Equal(childRows, await store.GetWorkflowDemandsAsync(childId));
        await store.DeleteWorkflowInstanceAsync(childId);
        await store.DeleteWorkflowInstanceAsync(id);
        Assert.Empty(await store.GetWorkflowDemandsAsync(id));
        Assert.Empty(await store.GetWorkflowDemandsAsync(childId));
    }

    [Theory]
    [InlineData(300)]
    [InlineData(17)]
    public async Task PinExpiryUsesStoreClockAndConfiguredLifetime(int seconds)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var store = new InMemoryWorkflowStateRepository(time);
        Assert.Equal(TimeSpan.FromMinutes(5), new IxIFlowOptions().HostPinLifetime);
        var services = Services(store, TimeSpan.FromSeconds(seconds));
        await using var provider = services.BuildServiceProvider();
        var host = Guid.NewGuid();
        var result = await provider.GetRequiredService<IWorkflowClient>().RunAsync(Definition(), new Data(),
            new() { RequiredHostId = host });
        var pin = Assert.Single(await store.GetWorkflowDemandsAsync(result.InstanceId), d => d.Key == "host");
        Assert.Equal(host.ToString("D"), pin.Value);
        Assert.Equal(time.GetUtcNow().UtcDateTime.AddSeconds(seconds), pin.ExpiresAtUtc);
        Assert.Equal(pin, Assert.Single(WorkflowDemands.Saved((await store.GetWorkflowInstanceAsync(result.InstanceId))!), d => d.Key == "host"));
    }

    [Fact]
    [Trait("Category", "SqliteIntegration")]
    public async Task DemandInsertFailureRollsBackTheInstanceAndItsParts()
    {
        var cs = SqliteTestDatabase<WorkflowDemandTests>.ConnectionString;
        await using var store = new SqliteWorkflowStateRepository(cs);
        await store.GetWorkflowDemandsAsync("initialize");
        await using var connection = new SqliteConnection(cs);
        await connection.ExecuteAsync("""
            CREATE TRIGGER refuse_demands BEFORE INSERT ON ixiflow_workflow_demands
            WHEN NEW.demand_key = 'refuse' BEGIN SELECT RAISE(ABORT, 'refused demand'); END;
            """);
        try
        {
            var (instance, checkpoint, scopes) = WorkflowHost.InstanceOperations.NewInstance(
                Workflow.Create<Data>("Rollback").Demand("refuse").Step<Noop>().Build(), new Data(), new(), DateTime.UtcNow);
            ExecutionCheckpointWriter.Prepare(instance, checkpoint, new Data(), scopes, DateTime.UtcNow);
            var owner = (await store.TryAcquireExecutionLeaseAsync(instance.InstanceId, "owner", TimeSpan.FromMinutes(1)))!;
            await Assert.ThrowsAnyAsync<Exception>(() => store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, owner.Epoch, 0)));
            Assert.Null(await store.GetWorkflowInstanceAsync(instance.InstanceId));
            Assert.Empty(await store.GetWorkflowDemandsAsync(instance.InstanceId));
        }
        finally { await connection.ExecuteAsync("DROP TRIGGER refuse_demands"); }
    }

    private static ServiceCollection Services(IWorkflowStateRepository store, TimeSpan? pinLifetime = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(store);
        services.AddIxIFlow(options => options.HostPinLifetime = pinLifetime ?? TimeSpan.FromMinutes(5));
        return services;
    }

    private static WorkflowDefinition Definition(string? region = null)
    {
        var builder = Workflow.Create<Data>("DemandFlow");
        if (region != null) builder.Demand("region", region);
        return builder.Step<Noop>().WithId("first").WaitFor<Go>("go").WithId("wait").Build();
    }

    public sealed class Data
    {
        public string Region { get; set; } = "";
        [System.Text.Json.Serialization.JsonIgnore]
        public string StartOnly => throw new InvalidOperationException("evaluated again");
    }
    public sealed class Go;
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    [IxIFlow.Core.Workflow("Declared")]
    public sealed class Declared : IWorkflow<Data>
    {
        public void Build(IWorkflowBuilder<Data> builder) => builder.DemandFrom("region", ctx => ctx.WorkflowData.Region)
            .Step<Noop>().WaitFor<Go>("go");
    }
}
