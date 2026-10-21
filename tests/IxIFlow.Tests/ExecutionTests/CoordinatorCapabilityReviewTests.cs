using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "recovery")]
public sealed class CoordinatorCapabilityReviewTests : IClassFixture<SqliteTestDatabase<CoordinatorCapabilityReviewTests>>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyClaimPassReportsCompletionWithoutAChangeInSlots(bool sqlite)
    {
        IWorkflowStateRepository store = sqlite
            ? new SqliteWorkflowStateRepository(SqliteTestDatabase<CoordinatorCapabilityReviewTests>.ConnectionString)
            : new InMemoryWorkflowStateRepository();
        try
        {
            var services = new ServiceCollection().AddLogging();
            services.AddSingleton(store);
            services.AddIxIFlow();
            await using var provider = services.BuildServiceProvider();
            var completed = 0;
            provider.GetRequiredService<CoordinationState>().ClaimPassCompleted += () => completed++;
            var scanner = provider.GetServices<IHostedService>().OfType<WorkflowRecoveryService>().Single();
            await scanner.RecoverOnceAsync(CancellationToken.None);
            Assert.Equal(1, completed);
            Assert.Equal(HostSettings.DefaultSlots, provider.GetRequiredService<WorkflowHost>().FreeSlots);
        }
        finally { if (store is IAsyncDisposable disposable) await disposable.DisposeAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegisteredHashesAreLocalAndClaimsReuseTheHeartbeatSnapshot(bool sqlite)
    {
        var time = new FakeTimeProvider();
        IWorkflowStateRepository inner = sqlite
            ? new SqliteWorkflowStateRepository(SqliteTestDatabase<CoordinatorCapabilityReviewTests>.ConnectionString, time)
            : new InMemoryWorkflowStateRepository(time);
        try
        {
            var store = DispatchProxy.Create<IWorkflowStateRepository, CountingForwarder>();
            var calls = (CountingForwarder)(object)store;
            calls.Inner = inner;
            var services = new ServiceCollection().AddLogging();
            services.AddSingleton<TimeProvider>(time);
            services.AddSingleton(store);
            services.AddIxIFlow();
            await using var provider = services.BuildServiceProvider();
            var catalog = provider.GetRequiredService<IWorkflowCatalog>();
            var definition = Workflow.Create<Data>("LocalHash").Step<Noop>().Build();
            await catalog.RegisterWorkflowAsync(definition);
            var hash = new WorkflowScopeCatalog(definition).StructureHash;
            calls.Calls.Clear();
            Assert.Equal(hash, await catalog.GetWorkflowStructureHashAsync(definition.Name, 1));
            Assert.Equal(0, calls.Count("GetWorkflowVersionAsync"));

            var countedCatalog = DispatchProxy.Create<IWorkflowCatalog, CountingForwarder>();
            var definitions = (CountingForwarder)(object)countedCatalog;
            definitions.Inner = catalog;
            // A separate provider uses the same catalog, with every member forwarded and counted.
            var hostServices = new ServiceCollection().AddLogging();
            hostServices.AddSingleton<TimeProvider>(time);
            hostServices.AddSingleton(store);
            hostServices.AddIxIFlow();
            hostServices.AddSingleton(countedCatalog);
            await using var host = hostServices.BuildServiceProvider();
            var heartbeat = host.GetServices<IHostedService>().OfType<WorkflowHeartbeatService>().Single();
            await heartbeat.StartAsync(CancellationToken.None);
            try
            {
                var scanner = host.GetServices<IHostedService>().OfType<WorkflowRecoveryService>().Single();
                for (var i = 0; i < 5; i++)
                {
                    await scanner.RecoverOnceAsync(CancellationToken.None);
                    Assert.Equal(WorkflowExecutionStatus.Success, (await host.GetRequiredService<IWorkflowClient>()
                        .RunAsync(definition, new Data())).Status);
                }
                Assert.Equal(1, definitions.Count("GetWorkflowDefinitionsAsync"));
                // Each start still reads the durable version state for admission; scans and snapshots add none.
                Assert.Equal(5, calls.Count("GetWorkflowVersionAsync"));
                // A second host has also cached its capabilities before the shared catalog changes.
                await provider.GetRequiredService<HostSettings>().GetCapabilitiesAsync(catalog);
                var registered = Workflow.Create<Data>("RegisteredAfterHeartbeat").Step<Noop>().Build();
                await catalog.RegisterWorkflowAsync(registered);
                var key = $"workflow:{registered.Name}@{registered.Version}";
                var registeredHash = new WorkflowScopeCatalog(registered).StructureHash;
                Assert.Equal(registeredHash, (await host.GetRequiredService<HostSettings>()
                    .GetCapabilitiesAsync(countedCatalog)).GetValueOrDefault(key));
                Assert.Equal(registeredHash, (await provider.GetRequiredService<HostSettings>()
                    .GetCapabilitiesAsync(catalog)).GetValueOrDefault(key));
                Assert.Equal(2, definitions.Count("GetWorkflowDefinitionsAsync"));
                var added = Workflow.Create<Data>("AddedAfterHeartbeat").Step<Noop>().Build();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                Assert.Equal(WorkflowExecutionStatus.Success, (await host.GetRequiredService<IWorkflowClient>()
                    .RunAsync(added, new Data(), cancellationToken: timeout.Token)).Status);
                Assert.Equal(3, definitions.Count("GetWorkflowDefinitionsAsync"));
                var snapshot = await host.GetRequiredService<HostSettings>().GetCapabilitiesAsync(countedCatalog);
                var revisionReads = definitions.Count("GetCatalogRevisionAsync");
                var deltaReads = definitions.Count("GetWorkflowVersionChangesAsync");
                var before = calls.Count("HeartbeatAsync");
                time.Advance(MemberRegistry.DefaultHeartbeatInterval);
                var until = DateTime.UtcNow.AddSeconds(5);
                while (calls.Count("HeartbeatAsync") == before && DateTime.UtcNow < until) await Task.Delay(10);
                Assert.True(calls.Count("HeartbeatAsync") > before);
                // The unchanged heartbeat reuses definitions and version results; only the scalar is read.
                Assert.Equal(3, definitions.Count("GetWorkflowDefinitionsAsync"));
                Assert.Equal(revisionReads + 1, definitions.Count("GetCatalogRevisionAsync"));
                Assert.Equal(deltaReads, definitions.Count("GetWorkflowVersionChangesAsync"));
                Assert.Equal(0, definitions.Count("GetWorkflowVersionsAsync"));
                Assert.Same(snapshot, await host.GetRequiredService<HostSettings>().GetCapabilitiesAsync(countedCatalog));
            }
            finally { await heartbeat.StopAsync(CancellationToken.None); }
        }
        finally { if (inner is IAsyncDisposable disposable) await disposable.DisposeAsync(); }
    }

    public class CountingForwarder : DispatchProxy
    {
        internal object Inner = null!;
        internal readonly ConcurrentDictionary<string, int> Calls = new();
        internal int Count(string member) => Calls.GetValueOrDefault(member);
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Calls.AddOrUpdate(targetMethod!.Name, 1, (_, count) => count + 1);
            try { return targetMethod.Invoke(Inner, args); }
            catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException!).Throw(); throw; }
        }
    }
    public sealed class Data;
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
