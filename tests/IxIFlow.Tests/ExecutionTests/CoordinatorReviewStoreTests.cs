using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Generators;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "host")]
public sealed class CoordinatorReviewStoreTests : IClassFixture<SqliteTestDatabase<CoordinatorReviewStoreTests>>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailAdmissionAfterValueExpiryUsesStoreTime(bool sqlite)
    {
        var storeTime = new FakeTimeProvider(new DateTimeOffset(2035, 1, 1, 0, 0, 0, TimeSpan.Zero));
        IWorkflowStateRepository store = sqlite
            ? new SqliteWorkflowStateRepository(SqliteTestDatabase<CoordinatorReviewStoreTests>.ConnectionString, storeTime)
            : new InMemoryWorkflowStateRepository(storeTime);
        try
        {
            var definition = Workflow.Create<Data>("ExpiredAdmission").Demand("region").Step<Noop>().Build();
            var hash = new WorkflowScopeCatalog(definition).StructureHash;
            var caps = new Dictionary<string, string> { ["region"] = "us", ["workflow:ExpiredAdmission@1"] = hash };
            await store.RegisterMemberAsync(new(Guid.NewGuid(), MemberKind.Host, "us", null, 1, "1", TimeSpan.FromMinutes(5), caps));
            var services = new ServiceCollection().AddLogging();
            services.AddSingleton(store);
            // The host's clock says the value is still active. Only store time can expire it.
            services.AddSingleton<TimeProvider>(new FakeTimeProvider(storeTime.GetUtcNow().AddYears(-1)));
            services.AddIxIFlow(options => options.Capabilities["region"] = "us");
            await using var provider = services.BuildServiceProvider();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var result = await provider.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data(),
                new() { WhenUnschedulable = WhenUnschedulable.Fail,
                    Demands = [new("region", "eu", storeTime.GetUtcNow().UtcDateTime.AddSeconds(-1))] }, timeout.Token);
            Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
            var saved = Assert.Single((await store.GetWorkflowDemandsAsync(result.InstanceId)).Where(demand => demand.Key == "region"));
            Assert.Null(saved.ExpiresAtUtc);
            Assert.NotNull(saved.ValueExpiresAtUtc);
        }
        finally { if (store is IAsyncDisposable disposable) await disposable.DisposeAsync(); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task MergedPresenceOutlivesValueRestriction(bool sqlite, bool reverse)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2035, 1, 1, 0, 0, 0, TimeSpan.Zero));
        IWorkflowStateRepository store = sqlite
            ? new SqliteWorkflowStateRepository(SqliteTestDatabase<CoordinatorReviewStoreTests>.ConnectionString, time)
            : new InMemoryWorkflowStateRepository(time);
        try
        {
            var expiry = time.GetUtcNow().UtcDateTime.AddMinutes(1);
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
            time.Advance(TimeSpan.FromMinutes(1));
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
        finally { if (store is IAsyncDisposable disposable) await disposable.DisposeAsync(); }
    }

    [Theory]
    [InlineData(false, false, "finally")]
    [InlineData(true, false, "finally")]
    [InlineData(false, false, "catch")]
    [InlineData(true, false, "catch")]
    [InlineData(false, false, "both")]
    [InlineData(true, false, "both")]
    [InlineData(false, true, "finally")]
    [InlineData(true, true, "finally")]
    [InlineData(false, true, "catch")]
    [InlineData(true, true, "catch")]
    [InlineData(false, true, "both")]
    [InlineData(true, true, "both")]
    public async Task TryContinuationsSaveDemandsAndRefuseMismatchingClaims(bool sqlite, bool yaml, string continuation)
    {
        WorkflowDefinition definition;
        if (yaml)
        {
            var options = new WorkflowSourceOptions();
            options.Types["Data"] = "global::IxIFlow.Tests.ExecutionTests.CoordinatorReviewStoreTests.Data";
            options.Types["Noop"] = "global::IxIFlow.Tests.ExecutionTests.CoordinatorReviewStoreTests.Noop";
            var source = "workflow: {name: TryPlacement, dataType: Data}\ndemands: [{key: region, value: eu}]\ndefinitions:\n"
                + "  - {kind: activity, activity: Noop}\n  - kind: try\n    steps: [{kind: activity, activity: Noop}]\n"
                + (continuation != "finally" ? "    catches: [{steps: [{kind: activity, activity: Noop}]}]\n" : "")
                + (continuation != "catch" ? "    finally: [{kind: activity, activity: Noop}]\n" : "")
                + "  - {kind: activity, activity: Noop}\n";
            definition = IxIFlow.Authoring.WorkflowCompilation.Build(WorkflowSourceTranslator.Translate(source, "placement.yaml", options));
        }
        else
        {
            var attempt = Workflow.Create<Data>("TryPlacement").Demand("region", "eu").Step<Noop>()
                .Try(body => body.Step<Noop>());
            definition = continuation switch
            {
                "finally" => attempt.Finally(body => body.Step<Noop>()).Step<Noop>().Build(),
                "catch" => attempt.Catch(body => body.Step<Noop>()).Step<Noop>().Build(),
                _ => attempt.Catch(body => body.Step<Noop>()).Finally(body => body.Step<Noop>()).Step<Noop>().Build()
            };
        }
        IWorkflowStateRepository store = sqlite
            ? new SqliteWorkflowStateRepository(SqliteTestDatabase<CoordinatorReviewStoreTests>.ConnectionString)
            : new InMemoryWorkflowStateRepository();
        try
        {
            var (instance, checkpoint, scopes) = WorkflowInstances.NewInstance(definition, new Data(), new(), DateTime.UtcNow);
            ExecutionCheckpointWriter.Prepare(instance, checkpoint, new Data(), scopes, DateTime.UtcNow);
            await store.SeedAsync(instance, TimeSpan.Zero);
            Assert.Contains(new WorkflowDemand("region", "eu"), await store.GetWorkflowDemandsAsync(instance.InstanceId));
            var capabilities = new Dictionary<string, string> { ["workflow:TryPlacement@1"] = scopes.StructureHash, ["region"] = "us" };
            var wrong = new ClaimQuery(Guid.NewGuid(), capabilities);
            Assert.DoesNotContain(await store.GetRunnableInstancesAsync(wrong), row => row.InstanceId == instance.InstanceId);
            Assert.Null(await store.TryAcquireExecutionLeaseAsync(instance.InstanceId, "wrong", TimeSpan.FromMinutes(1), wrong));
            capabilities["region"] = "eu";
            Assert.NotNull(await store.TryAcquireExecutionLeaseAsync(instance.InstanceId, "right", TimeSpan.FromMinutes(1), new(Guid.NewGuid(), capabilities)));
        }
        finally { if (store is IAsyncDisposable disposable) await disposable.DisposeAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminationClaimRequiresPendingCommandAndPreservesLeaseFencing(bool sqlite)
    {
        IWorkflowStateRepository store = sqlite
            ? new SqliteWorkflowStateRepository(SqliteTestDatabase<CoordinatorReviewStoreTests>.ConnectionString)
            : new InMemoryWorkflowStateRepository();
        try
        {
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
        finally { if (store is IAsyncDisposable disposable) await disposable.DisposeAsync(); }
    }

    public sealed class Data;
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
