using System.Collections.Concurrent;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "scopes"), Trait("Spec", "W-06")]
public sealed class CancellationScopeRestartScenarios
{
    public static IEnumerable<object[]> ChildRestarts() => StoreProfiles.All.Cast<object[]>().Select(row => new object[] { row[0], false })
        .Concat(StoreProfiles.MultipleHosts.Cast<object[]>().Select(row => new object[] { row[0], true }));

    [Theory(Timeout = 120_000), MemberData(nameof(ChildRestarts))]
    public async Task CancellationHandlerChildSurvivesRestartOrTakeoverAndItsStoreClockDelay(string profile, bool takeover)
    {
        var clock = Clock(profile);
        await using var store = await StoreProfiles.CreateAsync(profile, clock);
        var catalog = new WorkflowCatalog();
        var effects = new Effects();
        var repository = store.NewRepository(clock);

        var child = new WorkflowBuilder<Data, object>([], "CancellationRestartChild").WaitFor<Go>("child")
            .Assign(c => c.WorkflowData.Code, c => 7).Build();

        var parent = new WorkflowBuilder<Data, object>([], "CancellationRestartParent").CancellationScope(b => b.WaitFor<Go>("body"),
            h => h.Invoke<Data>(child.Name, 1, s => s.Output(x => x.Code).To(c => c.WorkflowData.Code))
                .Delay(TimeSpan.FromSeconds(2)).Step<Mark>(s => s.Input(x => x.Text).From(c => "finished"))).Build();

        await catalog.RegisterWorkflowAsync(child);
        await catalog.RegisterWorkflowAsync(parent);
        string id, childId;

        await using (var first = await Services(store, repository, catalog, effects, clock))
        {
            var client = first.GetRequiredService<IWorkflowClient>();
            id = (await client.RunAsync(parent, new Data())).InstanceId;
            await client.CancelAsync(id, new() { ReasonCode = "test" });
            childId = await WaitingChild(repository, client, id);
            Assert.Null(await repository.GetCancellationRequestAsync(childId));
            var state = await Read(repository, id);

            Assert.Equal(CancellationScopePhase.Handler,
                Assert.Single(state.Continuations[0].Stack, f => f.CancellationState != null).CancellationState!.Phase);
        }

        if (repository is IAsyncDisposable disposable)
            await disposable.DisposeAsync();

        var next = store.NewRepository(clock);
        var blocked = false;

        var faults = new FaultRepository(next)
        {
            FailCommit = _ => blocked,
            LoseAcknowledgement = instance =>
            {
                if (!takeover || blocked || instance.InstanceId != id)
                    return false;

                if (!Read(next, instance).WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult().Waits
                        .Any(w => w.TableKind == WaitKind.Delay))
                    return false;

                blocked = true;

                return true;
            }
        };

        var former = SwitchScenarios.LeaseProbe.Wrap(faults, takeover);

        await using (var second = await Services(store, former, catalog, effects, clock))
        {
            var client = second.GetRequiredService<IWorkflowClient>();
            Assert.Equal(WorkflowExecutionStatus.Success, (await client.ResumeAsync(childId, "child", new Go())).Status);

            if (takeover)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

                while (!Volatile.Read(ref blocked))
                    await Task.Delay(25, timeout.Token);
            }
            else
                await DueDelay(next, id, clock);
        }

        if (takeover)
        {
            if (clock != null)
                clock.Advance(TimeSpan.FromSeconds(31));
            else
                await store.ExpireExecutionLeaseAsync(id);
        }

        if (next is IAsyncDisposable nextDisposable)
            await nextDisposable.DisposeAsync();

        var successor = SwitchScenarios.LeaseProbe.Wrap(store.NewRepository(clock), false);
        await using var third = await Services(store, successor, catalog, effects, clock);
        var recovered = third.GetRequiredService<IWorkflowClient>();
        await DueDelay(successor, id, clock);
        await recovered.RecoverAsync(id);

        if (takeover)
            Assert.True(((SwitchScenarios.LeaseProbe)successor).Grant!.Epoch > ((SwitchScenarios.LeaseProbe)former).Grant!.Epoch);

        var done = await Finished(recovered, id);
        Assert.Equal(WorkflowExecutionStatus.Cancelled, done.Status);
        Assert.Equal(7, Assert.IsType<Data>(done.WorkflowData).Code);
        Assert.Equal(new[] { "finished" }, effects.Events);
        Assert.Single(await recovered.GetTraceAsync(id), e => e.EntryType == TraceEntryType.CancellationHandlerEntered);
    }

    public static IEnumerable<object[]> Unresolved() => StoreProfiles.All.Cast<object[]>().SelectMany(row =>
            new[] { false, true }.Select(terminate => new object[] { row[0], terminate, false }))
        .Concat(StoreProfiles.MultipleHosts.Cast<object[]>().SelectMany(row =>
            new[] { false, true }.Select(terminate => new object[] { row[0], terminate, true })));

    [Theory(Timeout = 120_000), MemberData(nameof(Unresolved))]
    public async Task CancellationUnresolvedActivityHoldsCleanupAcrossRestartUntilResolutionOrTermination(string profile, bool terminate,
        bool takeover)
    {
        var clock = Clock(profile);
        await using var store = await StoreProfiles.CreateAsync(profile, clock);
        var repository = store.NewRepository(clock);
        var effects = new Effects();
        var catalog = new WorkflowCatalog();

        var definition = new WorkflowBuilder<Data, object>([], "CancellationUnresolvedRestart").CancellationScope(
            b => b.Step<Mark>(s => s.Input(x => x.Text).From(c => "body")),
            h => h.Step<Mark>(s => s.Input(x => x.Text).From(c => "H"))).Build();

        await catalog.RegisterWorkflowAsync(definition);

        var faults = new FaultRepository(repository)
        {
            FailCommit = instance => effects.Events.Contains("body") &&
                Read(repository, instance).WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult().Continuations[0].PendingActivity ==
                null
        };

        var id = Guid.NewGuid().ToString("N");
        var former = SwitchScenarios.LeaseProbe.Wrap(faults, takeover);

        await using (var first = await Services(store, former, catalog, effects, clock))
            await Assert.ThrowsAsync<WorkflowEngineException>(() => first.GetRequiredService<IWorkflowClient>()
                .RunAsync(definition, new Data(), new() { InstanceId = id }));

        if (repository is IAsyncDisposable disposable)
            await disposable.DisposeAsync();

        if (takeover)
        {
            if (clock != null)
                clock.Advance(TimeSpan.FromSeconds(31));
            else
                await store.ExpireExecutionLeaseAsync(id);
        }

        var nextRepository = store.NewRepository(clock);
        var next = SwitchScenarios.LeaseProbe.Wrap(nextRepository, false);

        await using (var second = await Services(store, next, catalog, effects, clock))
        {
            var client = second.GetRequiredService<IWorkflowClient>();
            Assert.Equal(WorkflowExecutionStatus.NeedsResolution, (await client.RecoverAsync(id)).Status);

            if (takeover)
                Assert.True(((SwitchScenarios.LeaseProbe)next).Grant!.Epoch > ((SwitchScenarios.LeaseProbe)former).Grant!.Epoch);

            Assert.Equal(WorkflowExecutionStatus.NeedsResolution, (await client.CancelAsync(id, new() { ReasonCode = "test" })).Status);
            Assert.Equal(new[] { "body" }, effects.Events);
            Assert.DoesNotContain(await client.GetTraceAsync(id), e => e.EntryType == TraceEntryType.CancellationHandlerEntered);
        }

        if (nextRepository is IAsyncDisposable nextDisposable)
            await nextDisposable.DisposeAsync();

        await using var third = await Services(store, store.NewRepository(clock), catalog, effects, clock);
        var resumed = third.GetRequiredService<IWorkflowClient>();

        if (terminate)
        {
            await third.GetRequiredService<IWorkflowOperations>().TerminateAsync(id, "stop", actor: "test");
            Assert.Equal(WorkflowExecutionStatus.Terminated, await resumed.GetStatusAsync(id));
            Assert.Equal(new[] { "body" }, effects.Events);
        }
        else
        {
            var pending = Assert.Single(await resumed.GetPendingActivitiesAsync(id));

            var done = await resumed.ResolveAsync(id, pending.InvocationId,
                ActivityResolution.Completed(new Dictionary<string, object?> { ["Text"] = "body" }));

            Assert.Equal(WorkflowExecutionStatus.Cancelled, done.Status);
            Assert.Equal(new[] { "body", "H" }, effects.Events);
        }
    }

    public static IEnumerable<object[]> HeldJoins() => StoreProfiles.All.Cast<object[]>().SelectMany(row =>
        new[] { DebugStepKind.Into, DebugStepKind.Over }.Select(kind => new object[] { row[0], kind }));

    [Theory(Timeout = 120_000), MemberData(nameof(HeldJoins))]
    public async Task CancellationHeldJoinStaysBlockedAcrossRestartUntilContinue(string profile, DebugStepKind kind)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository();
        var effects = new Effects();
        var catalog = new WorkflowCatalog();

        var definition = new WorkflowBuilder<Data, object>([], "CancellationHeldJoinRestart").WaitFor<Go>("start")
            .Try(t => t.Parallel(p => p.Do(b => b.CancellationScope(
                    x => x.Throw<BusinessError>(c => new("original") { Code = 73 }).WithId("raise"),
                    h => h.Step<Mark>(s => s.Input(x => x.Text).From(c => "H"))))
                .Do(b => b.WaitFor<Go>("held"))))
            .Catch<BusinessError, CodeFault>(c => c.Assign(x => x.WorkflowData.Code, x => x.Fault.Code)).Build();

        await catalog.RegisterWorkflowAsync(definition);
        string id;

        await using (var first = await Services(store, repository, catalog, effects))
        {
            var client = first.GetRequiredService<IWorkflowClient>();
            var ops = first.GetRequiredService<IWorkflowOperations>();
            id = (await client.RunAsync(definition, new Data())).InstanceId;
            await ops.SetBreakpointsAsync(id, [new("raise", "raise")], "set");
            Assert.Equal(WorkflowExecutionStatus.Paused, (await client.ResumeAsync(id, "start", new Go())).Status);
            var before = (await ops.InspectAsync(id))!;
            var selected = before.DebugState.Stop!.BranchId;
            var held = JsonSerializer.Serialize(before.Threads.Where(t => t.ContinuationId != selected));
            Assert.Equal(WorkflowExecutionStatus.Paused, (await ops.StepAsync(id, selected, kind, "step")).Status);
            var blocked = (await ops.InspectAsync(id))!;
            Assert.Equal("Blocked", blocked.DebugState.Stop!.Reason);
            Assert.Equal(held, JsonSerializer.Serialize(blocked.Threads.Where(t => t.ContinuationId != selected)));
            Assert.Empty(effects.Events);
            Assert.DoesNotContain(await client.GetTraceAsync(id), e => e.EntryType == TraceEntryType.CancellationHandlerEntered);
        }

        if (repository is IAsyncDisposable disposable)
            await disposable.DisposeAsync();

        await using var second = await Services(store, store.NewRepository(), catalog, effects);
        var recovered = second.GetRequiredService<IWorkflowClient>();
        var operations = second.GetRequiredService<IWorkflowOperations>();
        Assert.Equal(WorkflowExecutionStatus.Paused, (await recovered.RecoverAsync(id)).Status);
        Assert.Equal("Blocked", (await operations.InspectAsync(id))!.DebugState.Stop!.Reason);
        await operations.SetBreakpointsAsync(id, [], "clear");
        var done = await operations.ContinueAsync(id, "continue");
        Assert.Equal(WorkflowExecutionStatus.Success, done.Status);
        Assert.Equal(73, Assert.IsType<Data>(done.WorkflowData).Code);
        Assert.Equal(new[] { "H" }, effects.Events);
    }

    public static IEnumerable<object[]> FailureRestarts() => StoreProfiles.All.Cast<object[]>()
        .Select(row => new object[] { row[0], false })
        .Concat(StoreProfiles.MultipleHosts.Cast<object[]>().Select(row => new object[] { row[0], true }));

    [Theory(Timeout = 120_000), MemberData(nameof(FailureRestarts))]
    public async Task CancellationHandlerFailureRetainsOriginalFaultAndOuterCleanupAcrossRestartOrTakeover(string profile, bool takeover)
    {
        var clock = Clock(profile);
        await using var store = await StoreProfiles.CreateAsync(profile, clock);
        var repository = store.NewRepository(clock);
        var effects = new Effects();
        var catalog = new WorkflowCatalog();

        var definition = new WorkflowBuilder<Data, object>([], "CancellationHandlerFailureRestart")
            .Try(t => t.CancellationScope(b => b.CancellationScope(x => x.Throw<BusinessError>(c => new("original") { Code = 73 }),
                    h => h.WaitFor<Go>("inner").Throw<InvalidOperationException>(c => new("cleanup"))),
                h => h.Step<Mark>(s => s.Input(x => x.Text).From(c => "outer")).Delay(TimeSpan.FromSeconds(2)).WaitFor<Go>("outer")))
            .Catch<BusinessError, CodeFault>(c => c.Step<Mark>(s => s.Input(x => x.Text).From(c => "C")))
            .Finally(f => f.Step<Mark>(s => s.Input(x => x.Text).From(c => "F"))).Build();

        await catalog.RegisterWorkflowAsync(definition);
        string id;

        await using (var first = await Services(store, repository, catalog, effects, clock))
        {
            var idle = await first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
            Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
            id = idle.InstanceId;
        }

        if (repository is IAsyncDisposable disposable)
            await disposable.DisposeAsync();

        var next = store.NewRepository(clock);
        var blocked = false;

        var faults = new FaultRepository(next)
        {
            FailCommit = _ => blocked,
            LoseAcknowledgement = instance =>
            {
                if (!takeover || blocked)
                    return false;

                var saved = Read(next, instance).WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();

                if (saved.UnhandledError == null || !saved.Waits.Any(w => w.TableKind == WaitKind.Delay))
                    return false;

                blocked = true;

                return true;
            }
        };

        var former = SwitchScenarios.LeaseProbe.Wrap(faults, takeover);

        await using (var second = await Services(store, former, catalog, effects, clock))
        {
            var task = second.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "inner", new Go());

            if (takeover)
                await Assert.ThrowsAsync<WorkflowEngineException>(() => task);
            else
                Assert.Equal(WorkflowExecutionStatus.Suspended, (await task).Status);

            Assert.Equal(new[] { "outer" }, effects.Events);
            var saved = await Read(next, id);
            Assert.Equal("cleanup", saved.UnhandledError!.Message);
            Assert.Equal("original", saved.UnhandledError.OriginatingFailure!.Message);

            Assert.Equal("73",
                saved.UnhandledError.OriginatingFailure.Error
                    .Properties[FaultProjection.PropertyKey(typeof(BusinessError).GetProperty(nameof(BusinessError.Code))!)].Json);
        }

        if (takeover)
        {
            Assert.True(blocked);
            Assert.NotNull(((SwitchScenarios.LeaseProbe)former).Grant);

            if (clock != null)
                clock.Advance(TimeSpan.FromSeconds(31));
            else
                await store.ExpireExecutionLeaseAsync(id);
        }

        if (next is IAsyncDisposable nextDisposable)
            await nextDisposable.DisposeAsync();

        var successor = SwitchScenarios.LeaseProbe.Wrap(store.NewRepository(clock), false);
        await using var third = await Services(store, successor, catalog, effects, clock);
        var recovered = third.GetRequiredService<IWorkflowClient>();
        await DueDelay(successor, id, clock);
        Assert.Equal(WorkflowExecutionStatus.Suspended, (await recovered.RecoverAsync(id)).Status);

        if (takeover)
            Assert.True(((SwitchScenarios.LeaseProbe)successor).Grant!.Epoch > ((SwitchScenarios.LeaseProbe)former).Grant!.Epoch);

        var done = await recovered.ResumeAsync(id, "outer", new Go());
        Assert.Equal(WorkflowExecutionStatus.Faulted, done.Status);
        Assert.Equal(new[] { "outer", "F" }, effects.Events);
        var trace = await recovered.GetTraceAsync(id);
        Assert.Equal(2, trace.Count(e => e.EntryType == TraceEntryType.CancellationHandlerEntered));
        Assert.Single(trace, e => e.EntryType == TraceEntryType.CancellationHandlerCompleted);
    }

    private static FakeTimeProvider? Clock(string profile) => profile is StoreProfiles.LocalMemory or StoreProfiles.LocalSqlite
        ? new FakeTimeProvider(DateTimeOffset.UtcNow)
        : null;

    private static async Task<WorkflowExecutionResult> Finished(IWorkflowClient client, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        return await client.AwaitAsync(id, WorkflowAwaitCondition.Finished, timeout.Token);
    }

    private static async Task<string> WaitingChild(IWorkflowStateRepository repository, IWorkflowClient client, string parent)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        while (true)
        {
            var children = (await repository.QueryInstancesAsync(new InstanceQuery(ParentInstanceId: parent))).Items;

            if (children.Count == 1 && (await client.GetOpenWaitsAsync(new(InstanceId: children[0].InstanceId))).Any(w => w.Key == "child"))
                return children[0].InstanceId;

            await Task.Delay(25, timeout.Token);
        }
    }

    private static async Task DueDelay(IWorkflowStateRepository repository, string id, FakeTimeProvider? clock)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        while (true)
        {
            var saved = await Read(repository, id);
            var delay = saved.Waits.SingleOrDefault(w => w.TableKind == WaitKind.Delay);

            if (delay == null)
            {
                if (saved.Waits.Any(w => w.Key == "outer") || saved.Continuations[0].Status == ContinuationStatus.Cancelled)
                    return;
            }
            else
            {
                var page = await repository.GetPendingEventsAsync(id, 0, 100);

                if (page.Deadlines?.TryGetValue(delay.Id, out var deadline) == true)
                {
                    if (clock != null)
                    {
                        if (deadline >= page.StoreTimeUtc!.Value)
                            clock.Advance(deadline - page.StoreTimeUtc.Value + TimeSpan.FromMilliseconds(1));

                        return;
                    }

                    if (page.StoreTimeUtc!.Value >= deadline)
                        return;
                }
            }

            await Task.Delay(25, timeout.Token);
        }
    }

    private static async Task<ExecutionCheckpoint> Read(IWorkflowStateRepository repository, string id) =>
        await Read(repository, (await repository.GetWorkflowInstanceAsync(id))!);

    private static async Task<ExecutionCheckpoint> Read(IWorkflowStateRepository repository, WorkflowInstance instance)
    {
        var reader = new PayloadReader(
            async reference => await repository.PayloadStore.GetAsync(reference) ?? throw new InvalidDataException("Missing payload"),
            new PayloadBudget(100_000_000));

        return ExecutionCheckpoint.Read(await StoredCheckpoint.WholeStateAsync(instance, reader));
    }

    private static async Task<ServiceProvider> Services(ProfileStore store, IWorkflowStateRepository repository, IWorkflowCatalog catalog,
        Effects effects, TimeProvider? clock = null)
    {
        var services = new ServiceCollection().AddLogging();

        services.AddIxIFlow(o =>
        {
            o.Debugging = true;
            o.VerifyCheckpointParts = true;
            o.PayloadThresholdBytes = 1;
        });

        services.AddSingleton(repository);
        services.AddSingleton(catalog);
        services.AddSingleton(effects);

        if (clock != null)
            services.AddSingleton(clock);

        store.ConfigureHost(services);
        var provider = services.BuildServiceProvider();
        await store.StartHostLinksAsync(provider).WaitAsync(TimeSpan.FromSeconds(30));

        return provider;
    }

    public sealed class Data
    {
        public int Code { get; set; }
    }

    public sealed class Go;

    public sealed class BusinessError(string message) : Exception(message)
    {
        public int Code { get; init; }
    }

    public sealed class CodeFault
    {
        public int Code { get; set; }
    }

    public sealed class Effects
    {
        public ConcurrentQueue<string> Events { get; } = new();
    }

    public sealed class Mark(Effects effects) : IAsyncActivity
    {
        public string Text { get; set; } = "";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            effects.Events.Enqueue(Text);

            return Task.CompletedTask;
        }
    }
}