using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static IxIFlow.Tests.Infrastructure.CancellationMixedCleanupScenario;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "scopes"), Trait("Spec", "W-06")]
public sealed class CancellationScopeMixedCleanupScenarios
{
    public static IEnumerable<object[]> NestedRetryCases() =>
        from row in Profiles()
        from kind in new[] { "handler", "finally" }
        from running in new[] { false, true }
        from failure in new[] { "none", "handled", "unhandled" }
        select new object[] { row.Profile, kind, running, failure, row.Takeover };

    [Theory(Timeout = 120_000), MemberData(nameof(NestedRetryCases))]
    public async Task NestedRetryCleanupFinishesBeforeAbandonmentAcrossRestartOrTakeover(string profile, string kind, bool running, string failure, bool takeover)
    {
        var clock = profile is StoreProfiles.LocalMemory or StoreProfiles.LocalSqlite ? new FakeTimeProvider(DateTimeOffset.UtcNow) : null;
        await using var store = await StoreProfiles.CreateAsync(profile, clock);
        var effects = new Effects(); var catalog = new WorkflowCatalog();
        await ExerciseNestedRetryCleanupAsync(kind, running, failure, takeover, () => store.NewRepository(clock),
            repository => Services(store, repository, catalog, effects, clock),
            id => { if (clock != null) { clock.Advance(TimeSpan.FromSeconds(31)); return Task.CompletedTask; } return store.ExpireExecutionLeaseAsync(id); }, effects, catalog);
    }

    public static IEnumerable<object[]> RetryCases() =>
        from row in Profiles()
        from finallyOutside in new[] { false, true }
        from work in new[] { "wait", "activity" }
        select new object[] { row.Profile, finallyOutside, work, row.Takeover };

    [Theory(Timeout = 120_000), MemberData(nameof(RetryCases))]
    public async Task ActiveRetryHandlerCannotEscapeCancellationAcrossRestartOrTakeover(string profile, bool finallyOutside, string work, bool takeover)
    {
        var clock = profile is StoreProfiles.LocalMemory or StoreProfiles.LocalSqlite ? new FakeTimeProvider(DateTimeOffset.UtcNow) : null;
        await using var store = await StoreProfiles.CreateAsync(profile, clock);
        var effects = new Effects(); var catalog = new WorkflowCatalog();
        await ExerciseRetryAsync(finallyOutside, work, takeover, () => store.NewRepository(clock),
            repository => Services(store, repository, catalog, effects, clock),
            id => { if (clock != null) { clock.Advance(TimeSpan.FromSeconds(31)); return Task.CompletedTask; } return store.ExpireExecutionLeaseAsync(id); }, effects, catalog).WaitAsync(TimeSpan.FromMinutes(2));
    }

    private static IEnumerable<(string Profile, bool Takeover)> Profiles() =>
        StoreProfiles.ProfileNames.Select(p => (p, false)).Concat(StoreProfiles.MultipleHosts.Cast<object[]>().Select(p => ((string)p[0], true)));

    public static IEnumerable<object[]> RecoveryCases() =>
        from row in Profiles()
        from finallyOutside in new[] { false, true }
        from failure in new[] { false, true }
        from boundary in RecoveryBoundaries
        from lost in new[] { false, true }
        where !failure || boundary is not ("boundary/resolution" or "boundary/start")
        where !row.Takeover || !lost || boundary != "terminal"
        select new object[] { row.Profile, finallyOutside, failure, boundary, lost, row.Takeover };

    [Theory(Timeout = 120_000), MemberData(nameof(RecoveryCases))]
    public async Task EveryMixedCleanupCommitRecoversAcrossRestartOrTakeover(string profile, bool finallyOutside, bool failure, string boundary, bool lost, bool takeover)
    {
        var clock = profile is StoreProfiles.LocalMemory or StoreProfiles.LocalSqlite ? new FakeTimeProvider(DateTimeOffset.UtcNow) : null;
        await using var store = await StoreProfiles.CreateAsync(profile, clock);
        var effects = new Effects(); var catalog = new WorkflowCatalog();
        await ExerciseRecoveryAsync(finallyOutside, failure, boundary, lost, takeover, () => store.NewRepository(clock),
            repository => Services(store, repository, catalog, effects, clock),
            id => { if (clock != null) { clock.Advance(TimeSpan.FromSeconds(31)); return Task.CompletedTask; } return store.ExpireExecutionLeaseAsync(id); }, effects, catalog).WaitAsync(TimeSpan.FromMinutes(2));
    }

    public static IEnumerable<object[]> Cases() =>
        from row in StoreProfiles.All.Cast<object[]>().Select(p => (Profile: (string)p[0], Takeover: false))
            .Concat(StoreProfiles.MultipleHosts.Cast<object[]>().Select(p => (Profile: (string)p[0], Takeover: true)))
        from finallyOutside in new[] { false, true }
        from compensation in new[] { false, true }
        from unresolved in new[] { false, true }
        select new object[] { row.Profile, finallyOutside, compensation, unresolved, row.Takeover };

    [Theory(Timeout = 120_000), MemberData(nameof(Cases))]
    public async Task CancellationPreservesMixedCleanupAcrossRestartOrTakeover(string profile, bool finallyOutside,
        bool compensation, bool unresolved, bool takeover)
    {
        var clock = profile is StoreProfiles.LocalMemory or StoreProfiles.LocalSqlite ? new FakeTimeProvider(DateTimeOffset.UtcNow) : null;
        await using var store = await StoreProfiles.CreateAsync(profile, clock);
        var repository = store.NewRepository(clock); var catalog = new WorkflowCatalog(); var effects = new Effects();
        var definition = Definition(finallyOutside, compensation, unresolved); await catalog.RegisterWorkflowAsync(definition);
        var faults = new FaultRepository(repository)
        {
            FailCommit = instance => unresolved && effects.Events.Contains("boundary") &&
                Read(repository, instance).WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult().Continuations[0].PendingActivity == null
        };
        var id = Guid.NewGuid().ToString("N");
        await using (var first = await Services(store, faults, catalog, effects, clock))
        {
            var task = first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data(), new() { InstanceId = id });
            if (unresolved) await Assert.ThrowsAsync<WorkflowEngineException>(() => task);
            else Assert.Equal(WorkflowExecutionStatus.Suspended, (await task).Status);
        }
        if (repository is IAsyncDisposable disposable) await disposable.DisposeAsync();

        var nextRepository = store.NewRepository(clock);
        var former = SwitchScenarios.LeaseProbe.Wrap(nextRepository, takeover);
        await using (var second = await Services(store, former, catalog, effects, clock))
        {
            var client = second.GetRequiredService<IWorkflowClient>();
            if (unresolved) Assert.Equal(WorkflowExecutionStatus.NeedsResolution, (await client.RecoverAsync(id)).Status);
            Assert.Equal(unresolved ? WorkflowExecutionStatus.NeedsResolution : WorkflowExecutionStatus.Suspended,
                (await client.CancelAsync(id, new() { ReasonCode = "test" })).Status);
            AssertOwner(await Read(nextRepository, id), finallyOutside, compensation && unresolved);
            Assert.Equal(unresolved ? new[] { "boundary" } : Array.Empty<string>(), effects.Events);
        }
        if (takeover)
        {
            Assert.NotNull(((SwitchScenarios.LeaseProbe)former).Grant);
            if (clock != null) clock.Advance(TimeSpan.FromSeconds(31)); else await store.ExpireExecutionLeaseAsync(id);
        }
        if (nextRepository is IAsyncDisposable nextDisposable) await nextDisposable.DisposeAsync();

        var successor = SwitchScenarios.LeaseProbe.Wrap(store.NewRepository(clock), false);
        await using var third = await Services(store, successor, catalog, effects, clock);
        AssertOwner(await Read(successor, id), finallyOutside, compensation && unresolved);
        var resumed = third.GetRequiredService<IWorkflowClient>();
        var done = unresolved
            ? await resumed.ResolveAsync(id, Assert.Single(await resumed.GetPendingActivitiesAsync(id)).InvocationId,
                ActivityResolution.Completed(new Dictionary<string, object?>()))
            : await resumed.ResumeAsync(id, "cleanup", new Go());
        Assert.Equal(WorkflowExecutionStatus.Cancelled, done.Status);
        if (takeover) Assert.True(((SwitchScenarios.LeaseProbe)successor).Grant!.Epoch > ((SwitchScenarios.LeaseProbe)former).Grant!.Epoch);
        AssertEffects(effects, compensation);
        var trace = await resumed.GetTraceAsync(id);
        Assert.Single(trace, e => e.EntryType == TraceEntryType.CancellationHandlerEntered);
        Assert.Single(trace, e => e.EntryType == TraceEntryType.CancellationHandlerCompleted);
    }

    private static async Task<ServiceProvider> Services(ProfileStore store, IWorkflowStateRepository repository,
        IWorkflowCatalog catalog, Effects effects, TimeProvider? clock)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow(o => { o.Debugging = true; o.VerifyCheckpointParts = true; o.PayloadThresholdBytes = 1; });
        services.AddSingleton(repository); services.AddSingleton(catalog); services.AddSingleton(effects);
        if (clock != null) services.AddSingleton(clock);
        store.ConfigureHost(services); var provider = services.BuildServiceProvider();
        await store.StartHostLinksAsync(provider).WaitAsync(TimeSpan.FromSeconds(30)); return provider;
    }
}
