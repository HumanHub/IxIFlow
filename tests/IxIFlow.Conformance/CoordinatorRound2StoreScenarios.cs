using System.Reflection;
using Dapper;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.Conformance;

[Trait("Spec", "C-08")]
[Trait("Spec", "C-10")]
public sealed class CoordinatorRound2StoreScenarios
{
    public static IEnumerable<object[]> ExpiryProfiles => StoreProfiles.ProfileNames
        .SelectMany(profile => new[] { false, true }.Select(reverse => new object[] { profile, reverse }));

    [Theory]
    [MemberData(nameof(ExpiryProfiles))]
    public async Task MergedPresenceOutlivesValueRestriction(string profile, bool reverse)
    {
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        await using var database = await StoreProfiles.CreateAsync(profile, time);
        var store = database.NewRepository();
        await store.GetLiveMembersAsync();
        time.SetUtcNow(DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        var expiry = time.GetUtcNow().UtcDateTime.AddSeconds(5);
        var definition = Workflow.Create<Data>("ExpiryPlacement").Step<Noop>().Build();
        var declarations = new WorkflowDemand[] { new("region", Inherit: true), new("region", "eu", expiry, true) };
        var options = new WorkflowOptions { Demands = (reverse ? declarations.Reverse() : declarations).ToList() };
        var (instance, checkpoint, scopes) = WorkflowInstances.NewInstance(definition, new Data(), options, time.GetUtcNow().UtcDateTime);
        ExecutionCheckpointWriter.Prepare(instance, checkpoint, new Data(), scopes, time.GetUtcNow().UtcDateTime);
        await store.SeedAsync(instance, TimeSpan.Zero);
        var caps = new Dictionary<string, string> { ["workflow:ExpiryPlacement@1"] = scopes.StructureHash, ["region"] = "us" };
        var host = Guid.NewGuid();
        var query = new ClaimQuery(host, caps);
        await store.RegisterMemberAsync(new(host, MemberKind.Host, "us", null, 1, "1", TimeSpan.FromMinutes(5), caps));
        Assert.False(await store.CanScheduleAsync(await store.GetWorkflowDemandsAsync(instance.InstanceId)));
        Assert.Null(await store.TryAcquireExecutionLeaseAsync(instance.InstanceId, "early", TimeSpan.FromMinutes(1), query));
        if (profile.StartsWith("distributed-", StringComparison.Ordinal))
            await Task.Delay(expiry > DateTime.UtcNow ? expiry - DateTime.UtcNow + TimeSpan.FromMilliseconds(100) : TimeSpan.Zero);
        else time.Advance(TimeSpan.FromSeconds(5));
        var saved = await store.GetWorkflowDemandsAsync(instance.InstanceId);
        var merged = Assert.Single(saved.Where(demand => demand.Key == "region"));
        Assert.Null(merged.ExpiresAtUtc);
        Assert.Equal(expiry, merged.ValueExpiresAtUtc);
        Assert.Equal(saved, WorkflowDemands.Saved((await store.GetWorkflowInstanceAsync(instance.InstanceId))!));
        Assert.True(await store.CanScheduleAsync(saved));
        Assert.Empty((await store.GetUnschedulableAsync()).Items.Where(row => row.InstanceId == instance.InstanceId));
        Assert.Contains(await store.GetRunnableInstancesAsync(query), row => row.InstanceId == instance.InstanceId);
        var missing = new ClaimQuery(host, new Dictionary<string, string> { ["workflow:ExpiryPlacement@1"] = scopes.StructureHash });
        Assert.Null(await store.TryAcquireExecutionLeaseAsync(instance.InstanceId, "absent", TimeSpan.FromMinutes(1), missing));
        Assert.NotNull(await store.TryAcquireExecutionLeaseAsync(instance.InstanceId, "after", TimeSpan.FromMinutes(1), query));
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task TerminationClaimRequiresPendingCommandAndPreservesLeaseFencing(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var definition = Workflow.Create<Data>("PinnedTermination").Demand("region", "eu").Step<Noop>().Build();
        var (instance, checkpoint, scopes) = WorkflowInstances.NewInstance(definition, new Data(),
            new() { RequiredHostId = Guid.NewGuid() }, DateTime.UtcNow);
        ExecutionCheckpointWriter.Prepare(instance, checkpoint, new Data(), scopes, DateTime.UtcNow);
        await store.SeedAsync(instance);
        var wrong = new ClaimQuery(Guid.NewGuid(), new Dictionary<string, string>());
        var termination = ExecutionClaimKind.Termination;
        Assert.Null(await store.TryAcquireExecutionLeaseAsync("missing", "missing", TimeSpan.FromMinutes(1), wrong, termination));
        Assert.Null(await store.TryAcquireExecutionLeaseAsync(instance.InstanceId, "before", TimeSpan.FromMinutes(1), wrong, termination));
        await store.RequestCancellationAsync(instance.InstanceId, new() { ReasonCode = "graceful" });
        Assert.Null(await store.TryAcquireExecutionLeaseAsync(instance.InstanceId, "cancel", TimeSpan.FromMinutes(1), wrong, termination));
        Assert.True((await store.RequestTerminationAsync(instance.InstanceId, new() { ReasonCode = "stop" })).Accepted);
        Assert.Null(await store.TryAcquireExecutionLeaseAsync(instance.InstanceId, "ordinary", TimeSpan.FromMinutes(1), wrong));
        var first = Assert.IsType<ExecutionLeaseGrant>(await store.TryAcquireExecutionLeaseAsync(instance.InstanceId,
            "terminator", TimeSpan.FromMinutes(1), wrong, termination));
        Assert.Null(await store.TryAcquireExecutionLeaseAsync(instance.InstanceId, "contender", TimeSpan.FromMinutes(1), wrong, termination));
        Assert.True((await store.ReleaseExecutionLeaseAsync(instance.InstanceId, "terminator")).Released);
        var second = Assert.IsType<ExecutionLeaseGrant>(await store.TryAcquireExecutionLeaseAsync(instance.InstanceId,
            "next", TimeSpan.FromMinutes(1), wrong, termination));
        Assert.True(second.Epoch > first.Epoch);
        Assert.False((await store.RenewExecutionLeasesAsync([new(instance.InstanceId, "terminator", TimeSpan.FromMinutes(1))]))[0].Status == ExecutionLeaseRenewalStatus.Renewed);
        var saved = (await store.GetWorkflowInstanceAsync(instance.InstanceId))!;
        saved.Status = WorkflowStatus.Terminated;
        saved.CancellationReason = await store.GetCancellationRequestAsync(instance.InstanceId);
        Assert.Equal(WorkflowCommitStatus.StaleEpoch, (await store.CommitWorkflowInstanceAsync(
            CheckpointCommit.Create(saved, first.Epoch, saved.Revision))).Status);
        saved.Incarnation = Guid.NewGuid();
        Assert.Equal(WorkflowCommitStatus.StaleEpoch, (await store.CommitWorkflowInstanceAsync(
            CheckpointCommit.Create(saved, second.Epoch, saved.Revision))).Status);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ANonMatchingHostFinishesTerminationAfterTheAcceptingHostIsGone(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var acceptingId = Guid.NewGuid();
        var definition = Workflow.Create<Data>("RequesterLost").Demand("region", "eu").Step<Noop>().Build();
        var (instance, checkpoint, catalog) = WorkflowInstances.NewInstance(definition, new Data(),
            new() { RequiredHostId = acceptingId }, DateTime.UtcNow);
        ExecutionCheckpointWriter.Prepare(instance, checkpoint, new Data(), catalog, DateTime.UtcNow);
        await store.SeedAsync(instance);
        var accepting = new ServiceCollection().AddLogging();
        accepting.AddSingleton(store);
        accepting.AddIxIFlow(options => options.Capabilities["region"] = "eu");
        await using (var requester = accepting.BuildServiceProvider())
        {
            // Crash boundary: the durable command was accepted, but no finishing pass ran.
            var receipt = await requester.GetRequiredService<IWorkflowStateRepository>()
                .RequestTerminationAsync(instance.InstanceId, new() { ReasonCode = "operator" }, instance.Incarnation);
            Assert.True(receipt.Accepted);
            Assert.Equal(instance.Incarnation, receipt.Incarnation);
        }
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(store);
        services.AddIxIFlow(options => options.Capabilities["region"] = "us");
        await using var survivor = services.BuildServiceProvider();
        var settings = survivor.GetRequiredService<HostSettings>();
        await store.RegisterMemberAsync(new(settings.Id, MemberKind.Host, "survivor", null, 1, "1",
            TimeSpan.FromMinutes(5), new Dictionary<string, string> { ["region"] = "us" }));
        var scanner = survivor.GetServices<IHostedService>().OfType<WorkflowRecoveryService>().Single();
        Assert.Empty(await store.GetRunnableInstancesAsync(await StoreTestSupport.ClaimAsync(survivor)));
        Assert.Equal(instance.Incarnation, Assert.Single((await store.GetUnschedulableAsync()).Items).Incarnation);
        await scanner.ScanUnschedulableOnceAsync(CancellationToken.None);
        var ended = (await store.GetWorkflowInstanceAsync(instance.InstanceId))!;
        Assert.Equal(WorkflowStatus.Terminated, ended.Status);
        Assert.Equal(instance.Incarnation, ended.Incarnation);
        Assert.Equal("operator", ended.CancellationReason!.ReasonCode);
        Assert.Empty((await store.GetUnschedulableAsync()).Items);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task DormantSetsAreExcludedFromTheCandidateDemandRead(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        for (var index = 0; index <= 32; index++)
        {
            var definition = Workflow.Create<Data>("DormantSets").Demand("set", index.ToString()).Step<Noop>().Build();
            var (instance, checkpoint, catalog) = WorkflowInstances.NewInstance(definition, new Data(), new(), DateTime.UtcNow);
            ExecutionCheckpointWriter.Prepare(instance, checkpoint, new Data(), catalog, DateTime.UtcNow);
            if (index != 0) instance.Status = WorkflowStatus.Completed;
            await store.SeedAsync(instance, TimeSpan.Zero);
        }
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        if (store is InMemoryWorkflowStateRepository)
        {
            var sets = (IEnumerable<KeyValuePair<string, WorkflowDemand[]>>)store.GetType()
                .GetMethod("DueDemandSets", flags)!.Invoke(store, [DateTime.UtcNow])!;
            Assert.Single(sets);
        }
        else
        {
            var sql = (string)store.GetType().GetProperty("AllDemandSetsSql", flags)!.GetValue(store)!;
            await using var connection = database.NewConnection();
            var rows = (await connection.QueryAsync(sql, new { Now = DateTime.UtcNow })).ToArray();
            Assert.Equal(2, rows.Length); // one tag plus the immutable workflow hash
        }
        Assert.Single((await store.GetUnschedulableAsync()).Items);
    }

    public sealed class Data;
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
