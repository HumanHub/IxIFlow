using Dapper;
using IxIFlow.Builders;
using IxIFlow.Coordinator;
using System.Text.Json;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "cancellation"), Trait("Spec", "W-06")]
public sealed class CancellationScopeScenarios
{
    [Theory(Timeout = 120_000), MemberData(nameof(StoreProfiles.MultipleHosts), MemberType = typeof(StoreProfiles))]
    public async Task CancellationFromAnotherLinkedHostRunsTheSavedHandler(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile); var catalog = new WorkflowCatalog();
        var definition = new WorkflowBuilder<Data, object>([], "CancellationRemoteHost").CancellationScope(b => b.WaitFor<Go>("body"),
            h => h.Assign(c => c.WorkflowData.Cleanups, c => c.WorkflowData.Cleanups + 1)).Build();
        await catalog.RegisterWorkflowAsync(definition);
        await using var first = await Services(store, store.NewRepository(), catalog);
        await using var second = await Services(store, store.NewRepository(), catalog);
        var idle = await first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        var done = await second.GetRequiredService<IWorkflowClient>().CancelAsync(idle.InstanceId, new() { ReasonCode = "remote" });
        Assert.Equal(WorkflowExecutionStatus.Cancelled, done.Status); Assert.Equal(1, Assert.IsType<Data>(done.WorkflowData).Cleanups);
        Assert.Equal(WorkflowExecutionStatus.Cancelled, await first.GetRequiredService<IWorkflowClient>().GetStatusAsync(idle.InstanceId));
    }

    [Theory(Timeout = 120_000), MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task CancellationHandlerForkDelayAndWaitsRecoverWithTheirOwnStoreDeadlines(string profile)
    {
        var clock = Local(profile) ? new FakeTimeProvider() : null;
        await using var store = await StoreProfiles.CreateAsync(profile, clock);
        await ForkDelayAndWaits(store, clock);
    }

    private static async Task ForkDelayAndWaits(ProfileStore store, FakeTimeProvider? clock)
    {
        var catalog = new WorkflowCatalog();
        var definition = new WorkflowBuilder<Data, object>([], "CancellationHandlerFork").CancellationScope(b => b.WaitFor<Go>("body"),
            h => h.Parallel(p => p.Do(x => x.WaitFor<Go>("one").Assign(c => c.WorkflowData.Cleanups, c => 1))
                .Do(x => x.Delay(TimeSpan.FromMilliseconds(100)).WaitFor<Go>("two").Assign(c => c.WorkflowData.Code, c => 2)))).Build();
        await catalog.RegisterWorkflowAsync(definition);
        var id = Guid.NewGuid().ToString("N"); var repository = store.NewRepository(clock);
        var providers = new List<ServiceProvider>(); var failures = new List<string>();
        async Task<T> Step<T>(string stage, Task<T> work)
        {
            try { return await work.WaitAsync(TimeSpan.FromSeconds(20)); }
            catch (Exception error)
            {
                // Capture while providers and the isolated database still exist. Every probe
                // has its own short bound so a blocked server cannot suppress the evidence.
                var details = await ForkDiagnostics(store, repository, id, providers);
                failures.Add($"Handler fork stalled/failed at {stage}; profile={store.Profile}; coordinators={store.CoordinatorCount}; instance={id}\n{details}\n{error}");
                throw new Xunit.Sdk.XunitException(string.Join("\n\n", failures));
            }
        }
        async Task Close(string stage, ServiceProvider provider) =>
            await Step(stage, DisposeProvider(provider));
        async Task<bool> DisposeProvider(ServiceProvider provider) { await provider.DisposeAsync(); return true; }
        // Explicit bounded disposal preserves a diagnostic failure instead of masking it
        // behind an unbounded await-using teardown.
        var first = await Step("first links", Services(store, repository, catalog, clock, providers.Add));
        try
        {
            var client = first.GetRequiredService<IWorkflowClient>();
            Assert.Equal(WorkflowExecutionStatus.Suspended, (await Step("body start", client.RunAsync(definition, new Data(), new() { InstanceId = id }))).Status);
            Assert.Equal(WorkflowExecutionStatus.Suspended, (await Step("cancel and fork", client.CancelAsync(id, new() { ReasonCode = "test" }))).Status);
            var state = await Read(repository, (await repository.GetWorkflowInstanceAsync(id))!);
            Assert.True(state.Continuations[0].CancellationUnwind); Assert.Single(state.Joins);
            Assert.DoesNotContain(state.Waits, wait => wait.Key == "body");
        }
        finally { await Close("first host disposal", first); }
        if (repository is IAsyncDisposable disposable) await disposable.DisposeAsync();
        repository = store.NewRepository(clock);
        await Step("saved delay deadline", Deadline());
        async Task<bool> Deadline() { await WaitForSavedDeadline(repository, id, clock, execution: false); return true; }
        var second = await Step("successor links", Services(store, repository, catalog, clock, providers.Add));
        try
        {
            var recovered = second.GetRequiredService<IWorkflowClient>();
            await Step("explicit recovery", recovered.RecoverAsync(id));
            Assert.Equal(WorkflowExecutionStatus.Suspended, (await Step("branch one event", recovered.ResumeAsync(id, "one", new Go()))).Status);
            var done = await Step("branch two event and join", recovered.ResumeAsync(id, "two", new Go()));
            Assert.Equal(WorkflowExecutionStatus.Cancelled, done.Status);
            Assert.Equal(1, Assert.IsType<Data>(done.WorkflowData).Cleanups); Assert.Equal(2, Assert.IsType<Data>(done.WorkflowData).Code);
        }
        finally { await Step("successor host disposal", DisposeProvider(second)); }
    }

    private static async Task<string> ForkDiagnostics(ProfileStore store, IWorkflowStateRepository repository, string id, List<ServiceProvider> providers)
    {
        var details = new List<string>();
        async Task Probe(string name, Func<Task<object?>> read)
        {
            try { details.Add(name + "=" + JsonSerializer.Serialize(await read().WaitAsync(TimeSpan.FromSeconds(3)))); }
            catch (Exception error) { details.Add(name + " unavailable: " + error.Message); }
        }
        await Probe("instance", async () =>
        {
            var row = await repository.GetWorkflowInstanceAsync(id);
            return row == null ? null : new { Status = row.Status.ToString(), row.Revision, row.RunnableAtUtc, row.LastHostId, row.LastError };
        });
        await Probe("waits", async () => await repository.GetOpenWaitsAsync(new(InstanceId: id)));
        await Probe("deadlines", async () =>
        {
            var page = await repository.GetPendingEventsAsync(id, 0, 100);
            return new { page.StoreTimeUtc, page.Deadlines, page.Events };
        });
        await Probe("continuations", async () =>
        {
            var state = await Read(repository, (await repository.GetWorkflowInstanceAsync(id))!);
            return new { state.Joins, Threads = state.Continuations.Select(c => new { c.Id, Status = c.Status.ToString(), c.ParentJoinId, c.CancellationUnwind,
                Stack = c.Stack.Select(f => new { f.ScopeId, f.NextStepIndex, f.CancellationState, f.TryState, f.SagaState }) }) };
        });
        if (store.Profile is StoreProfiles.DistributedSqlServer or StoreProfiles.DistributedPostgreSql)
            await Probe("lease", async () =>
            {
                await using var connection = store.NewConnection();
                return (await connection.QueryAsync(new CommandDefinition(store.Profile == StoreProfiles.DistributedSqlServer
                    ? "SELECT HostId, Epoch, ExpiresAtUtc, CrashCount FROM dbo.IxIFlowWorkflowLeases WHERE InstanceId=@Id"
                    : "SELECT host_id, epoch, expires_at_utc, crash_count FROM ixiflow_workflow_leases WHERE instance_id=@Id",
                    new { Id = id }, commandTimeout: 2))).ToArray();
            });
        for (var index = 0; index < providers.Count; index++)
        {
            var provider = providers[index];
            try
            {
                details.Add($"host[{index}] id={provider.GetRequiredService<HostSettings>().Id}");
            }
            catch (ObjectDisposedException) { details.Add($"host[{index}]=disposed"); }
        }
        return string.Join("\n", details);
    }

    public static IEnumerable<object[]> FaultRoutes() => StoreProfiles.ProfileNames.SelectMany(p =>
        new[] { false, true }.Select(yaml => new object[] { p, yaml }));

    [Theory(Timeout = 120_000), MemberData(nameof(FaultRoutes))]
    public async Task CancellationHandlerRestartPreservesOriginalFaultAndExactRoute(string profile, bool yaml)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository(); var catalog = new WorkflowCatalog();
        var fluent = FaultDefinition(); var generated = IxIFlow.Conformance.Generated.Workflow_cancel_fault_workflow.Build();
        Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(generated));
        var definition = yaml ? generated : fluent; await catalog.RegisterWorkflowAsync(definition); string id;
        WorkflowFaultLocation origin; FailureHandlerRoute route;
        await using (var first = await Services(store, repository, catalog))
        {
            var result = await first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
            Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status); id = result.InstanceId;
            var checkpoint = await Read(repository, (await repository.GetWorkflowInstanceAsync(id))!);
            var state = Assert.Single(checkpoint.Continuations[0].Stack, f => f.CancellationState != null).CancellationState!;
            Assert.Equal(CancellationScopePhase.Handler, state.Phase);
            Assert.Equal(CancellationUnwindCause.HandledFaultCancellation, state.Cause);
            route = state.Failure!.Route!; origin = state.Failure.Error.OriginalFaultLocation!;
            Assert.Equal(checkpoint.Continuations[0].Stack[0].ActivationId, route.ActivationId);
            Assert.Equal(0, route.HandlerIndex);
            Assert.Contains((await first.GetRequiredService<IWorkflowOperations>().InspectAsync(id))!.Threads.SelectMany(t => t.Frames),
                frame => frame.Kind == "CancellationHandler" && frame.Phase == "Handler");
        }
        if (repository is IAsyncDisposable disposable) await disposable.DisposeAsync();
        await using var second = await Services(store, store.NewRepository(), catalog);
        var done = await second.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "cleanup", new Go());
        Assert.Equal(WorkflowExecutionStatus.Success, done.Status);
        var data = Assert.IsType<Data>(done.WorkflowData); Assert.Equal(73, data.Code); Assert.Equal(new string('x', 8192), data.Detail);
        var traces = await second.GetRequiredService<IWorkflowOperations>().GetTraceAsync(id);
        Assert.Equal(origin, Assert.IsType<FaultTrace>(Assert.Single(traces, e => e.Entry.EntryType == TraceEntryType.FaultRaised).Entry.Payload).OriginalLocation);
        Assert.Single(traces, e => e.Entry.EntryType == TraceEntryType.CancellationHandlerEntered);
        Assert.Single(traces, e => e.Entry.EntryType == TraceEntryType.CancellationHandlerCompleted);
        Assert.True(traces.Single(e => e.Entry.EntryType == TraceEntryType.CancellationHandlerCompleted).Sequence <
            traces.Single(e => e.Entry.EntryType == TraceEntryType.CatchEntered).Sequence);
    }

    public static IEnumerable<object[]> Causes() => StoreProfiles.ProfileNames.SelectMany(p =>
        new[] { "cancel", "deadline", "terminate" }.Select(c => new object[] { p, c }));

    [Theory(Timeout = 120_000), MemberData(nameof(Causes))]
    public async Task CancellationAndStoreClockDeadlineResumeHandlerButTerminationRunsNothing(string profile, string cause)
    {
        var clock = Local(profile) ? new FakeTimeProvider() : null;
        await using var store = await StoreProfiles.CreateAsync(profile, clock);
        var repository = store.NewRepository(clock); var catalog = new WorkflowCatalog();
        var definition = new WorkflowBuilder<Data, object>([], "CancellationCause").CancellationScope(b => b.WaitFor<Go>("body"),
            h => h.Assign(c => c.WorkflowData.Code, c => 1).WaitFor<Go>("cleanup")
                .Assign(c => c.WorkflowData.Code, c => 2)).Build();
        await catalog.RegisterWorkflowAsync(definition); string id;
        await using (var first = await Services(store, repository, catalog, clock))
        {
            var client = first.GetRequiredService<IWorkflowClient>();
            var idle = await client.RunAsync(definition, new Data(), new() { ExecutionTimeout = cause == "deadline" ? TimeSpan.FromSeconds(10) : null }); id = idle.InstanceId;
            Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
            if (cause == "terminate")
            {
                await first.GetRequiredService<IWorkflowOperations>().TerminateAsync(id, "stop", actor: "test");
                Assert.DoesNotContain(await client.GetTraceAsync(id), e => e.EntryType == TraceEntryType.CancellationHandlerEntered); return;
            }
            if (cause == "deadline")
            {
                await WaitForSavedDeadline(repository, id, clock);
                // The host's timer resumes this instance itself. An explicit recovery can
                // arrive after that run parked at cleanup and be refused as idle recovery.
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                while (!(await client.GetOpenWaitsAsync(new(InstanceId: id))).Any(w => w.Key == "cleanup"))
                    await Task.Delay(10, timeout.Token);
                var timed = (await repository.GetWorkflowInstanceAsync(id))!;
                Assert.Equal(WorkflowStatus.Suspended, timed.Status);
                var checkpoint = await Read(repository, timed);
                Assert.True(checkpoint.TimedOut); Assert.Null(checkpoint.UnhandledError);
            }
            else
            {
                Assert.Equal(WorkflowExecutionStatus.Suspended, (await client.CancelAsync(id, new() { ReasonCode = "test" })).Status);
                await client.CancelAsync(id, new() { ReasonCode = "duplicate" });
            }
            Assert.Equal("cleanup", Assert.Single(await client.GetOpenWaitsAsync(new(InstanceId: id))).Key);
            Assert.Single(await client.GetTraceAsync(id), e => e.EntryType == TraceEntryType.CancellationHandlerEntered);
            var saved = (await repository.GetWorkflowInstanceAsync(id))!;
            var reader = new PayloadReader(async reference => await repository.PayloadStore.GetAsync(reference) ?? throw new InvalidDataException("Missing payload"), new PayloadBudget(100_000_000));
            Assert.Equal(1, System.Text.Json.JsonSerializer.Deserialize<Data>(await reader.ReadAsync(saved.WorkflowDataJson))!.Code);
        }
        if (repository is IAsyncDisposable disposable) await disposable.DisposeAsync();
        await using var second = await Services(store, store.NewRepository(clock), catalog, clock);
        var done = await second.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "cleanup", new Go());
        Assert.Equal(cause == "deadline" ? WorkflowExecutionStatus.TimedOut : WorkflowExecutionStatus.Cancelled, done.Status);
        Assert.Equal(2, Assert.IsType<Data>(done.WorkflowData).Code);
    }

    public static IEnumerable<object[]> Boundaries() => StoreProfiles.ProfileNames.SelectMany(p =>
        new[] { "entry", "completion", "terminal" }.SelectMany(boundary => new[] { false, true }.Select(lost => new object[] { p, boundary, lost })));

    [Theory(Timeout = 120_000), MemberData(nameof(Boundaries))]
    public async Task CancellationPhaseBoundariesRecoverBeforeApplyAndAfterLostAcknowledgement(string profile, string boundary, bool lost)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var repository = store.NewRepository(); var catalog = new WorkflowCatalog();
        var definition = new WorkflowBuilder<Data, object>([], "CancellationCommitBoundary").WaitFor<Go>("start")
            .Try(t => t.CancellationScope(b => b.Throw<BusinessError>(c => new("private") { Code = 73 }),
                h => h.Assign(c => c.WorkflowData.Cleanups, c => c.WorkflowData.Cleanups + 1).WaitFor<Go>("cleanup")))
            .Catch<BusinessError, CodeFault>(c => c.Assign(x => x.WorkflowData.Code, x => x.Fault.Code).WaitFor<Go>("caught")).Build();
        await catalog.RegisterWorkflowAsync(definition);
        var injecting = false; var interrupted = false; var blockAcknowledgement = false;
        bool Interrupt(WorkflowInstance instance)
        {
            if (!injecting || interrupted) return false;
            var candidate = Read(repository, instance).WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            var matches = boundary switch
            {
                "entry" => candidate.Waits.Any(w => w.Key == "cleanup"),
                "completion" => candidate.Waits.Any(w => w.Key == "caught"),
                _ => instance.Status == WorkflowStatus.Completed
            };
            if (!matches) return false;
            interrupted = true; blockAcknowledgement = lost; return true;
        }
        var faults = new FaultRepository(repository)
        { FailCommit = instance => injecting && !lost && (interrupted || Interrupt(instance)) || blockAcknowledgement, LoseAcknowledgement = instance => lost && Interrupt(instance) };
        string id;
        await using (var first = await Services(store, faults, catalog))
        {
            var client = first.GetRequiredService<IWorkflowClient>();
            id = (await client.RunAsync(definition, new Data())).InstanceId;
            if (boundary != "entry") await client.ResumeAsync(id, "start", new Go());
            if (boundary == "terminal") await client.ResumeAsync(id, "cleanup", new Go());
            injecting = true;
            Assert.IsType<WorkflowEngineException>(await Record.ExceptionAsync(() => client.ResumeAsync(id,
                boundary == "entry" ? "start" : boundary == "completion" ? "cleanup" : "caught", new Go())));
            Assert.True(interrupted); injecting = false;
        }
        if (repository is IAsyncDisposable disposable) await disposable.DisposeAsync();
        await using var second = await Services(store, store.NewRepository(), catalog);
        var recovered = second.GetRequiredService<IWorkflowClient>();
        var result = await recovered.RecoverAsync(id);
        if (boundary == "entry") result = await recovered.ResumeAsync(id, "cleanup", new Go());
        if (boundary != "terminal") result = await recovered.ResumeAsync(id, "caught", new Go());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var data = Assert.IsType<Data>(result.WorkflowData); Assert.Equal(73, data.Code); Assert.Equal(1, data.Cleanups);
        var trace = await recovered.GetTraceAsync(id);
        Assert.Single(trace, e => e.EntryType == TraceEntryType.CancellationHandlerEntered);
        Assert.Single(trace, e => e.EntryType == TraceEntryType.CancellationHandlerCompleted);
    }

    public static IEnumerable<object[]> Losers() => StoreProfiles.ProfileNames.SelectMany(p => new[] { false, true }.Select(each => new object[] { p, each }));

    [Theory(Timeout = 120_000), MemberData(nameof(Losers))]
    public async Task CancellationOfActiveParallelLosersWaitsForHandlersAndDoesNotStartQueuedItems(string profile, bool each)
    {
        await using var store = await StoreProfiles.CreateAsync(profile); var catalog = new WorkflowCatalog();
        var repository = store.NewRepository();
        var b = new WorkflowBuilder<Data, object>([], "CancellationLosers");
        var definition = each ? b.ForEach(c => c.WorkflowData.Items, (body, item) => body.CancellationScope(
                x => x.WaitFor<Go>("body"), h => h.WaitFor<Go>("cleanup").Assign(c => c.WorkflowData.Code, c => 999)),
                options => options.Parallel(2).WaitAny()).Build()
            : b.Parallel(p => p.WaitAny().Do(x => x.CancellationScope(y => y.WaitFor<Go>("body"),
                h => h.WaitFor<Go>("cleanup").Assign(c => c.WorkflowData.Code, c => 999))).Do(x => x.WaitFor<Go>("winner"))).Build();
        await catalog.RegisterWorkflowAsync(definition); string id;
        await using (var first = await Services(store, repository, catalog))
        {
            var client = first.GetRequiredService<IWorkflowClient>(); id = (await client.RunAsync(definition, new Data())).InstanceId;
            var waits = await client.GetOpenWaitsAsync(new(InstanceId: id));
            var winner = each ? waits.First(w => w.Key == "body") : waits.Single(w => w.Key == "winner");
            var result = await client.ReplyAsync(winner.Token, new Go());
            Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
            Assert.Equal("cleanup", Assert.Single(await client.GetOpenWaitsAsync(new(InstanceId: id))).Key);
        }
        if (repository is IAsyncDisposable disposable) await disposable.DisposeAsync();
        await using var second = await Services(store, store.NewRepository(), catalog);
        var done = await second.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "cleanup", new Go());
        Assert.Equal(WorkflowExecutionStatus.Success, done.Status); Assert.Equal(0, Assert.IsType<Data>(done.WorkflowData).Code);
        Assert.Single(await second.GetRequiredService<IWorkflowClient>().GetTraceAsync(id), e => e.EntryType == TraceEntryType.CancellationHandlerEntered);
    }

    private static async Task WaitForSavedDeadline(IWorkflowStateRepository repository, string id, FakeTimeProvider? clock, bool execution = true)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var checkpoint = await Read(repository, (await repository.GetWorkflowInstanceAsync(id))!);
            if (execution && checkpoint.TimedOut) return;
            var delay = checkpoint.Waits.SingleOrDefault(wait => wait.TableKind == WaitKind.Delay);
            if (!execution && delay == null)
            {
                Assert.Contains(checkpoint.Waits, wait => wait.Key == "two"); return;
            }
            var timer = execution ? checkpoint.ExecutionTimerId! : delay!.Id;
            var page = await repository.GetPendingEventsAsync(id, 0, 100);
            // A linked host may have consumed the timer between the two reads.
            if (page.Deadlines?.TryGetValue(timer, out var deadline) == true)
            {
                if (clock != null)
                {
                    if (deadline > page.StoreTimeUtc!.Value) clock.Advance(deadline - page.StoreTimeUtc.Value + TimeSpan.FromMilliseconds(1));
                    return;
                }
                if (page.StoreTimeUtc!.Value >= deadline) return;
            }
            await Task.Delay(10, timeout.Token);
        }
    }

    private static WorkflowDefinition FaultDefinition() => new WorkflowBuilder<Data, object>([], "CancellationFaultConformance")
        .Try(t => t.CancellationScope(b => b.Throw<BusinessError>(c => new("private") { Code = 73, Detail = new string('x', 8192) }),
            h => h.WaitFor<Go>("cleanup")))
        .Catch<BusinessError, CodeFault>(c => c.Assign(x => x.WorkflowData.Code, x => x.Fault.Code)
            .Assign(x => x.WorkflowData.Detail, x => x.Fault.Detail)).Build();

    private static bool Local(string profile) => profile is StoreProfiles.LocalMemory or StoreProfiles.LocalSqlite;
    private static async Task<ExecutionCheckpoint> Read(IWorkflowStateRepository repository, WorkflowInstance instance)
    {
        var reader = new PayloadReader(async reference => await repository.PayloadStore.GetAsync(reference) ?? throw new InvalidDataException("Missing payload"), new PayloadBudget(100_000_000));
        return ExecutionCheckpoint.Read(await StoredCheckpoint.WholeStateAsync(instance, reader));
    }
    private static async Task<ServiceProvider> Services(ProfileStore store, IWorkflowStateRepository repository, IWorkflowCatalog catalog, TimeProvider? time = null, Action<ServiceProvider>? created = null)
    {
        var collection = new ServiceCollection().AddLogging(); collection.AddIxIFlow(o => { o.PayloadThresholdBytes = 1; o.Debugging = true; });
        collection.AddSingleton(repository); collection.AddSingleton(catalog); if (time != null) collection.AddSingleton(time);
        store.ConfigureHost(collection); var services = collection.BuildServiceProvider();
        created?.Invoke(services);
        await store.StartHostLinksAsync(services).WaitAsync(TimeSpan.FromSeconds(30)); return services;
    }
    public sealed class Data { public int Code { get; set; } public string Detail { get; set; } = ""; public int Cleanups { get; set; } public int[] Items { get; set; } = [1, 2, 3, 4]; }
    public sealed class Go;
    public sealed class BusinessError(string message) : Exception(message) { public int Code { get; init; } public string Detail { get; init; } = ""; }
    public sealed class CodeFault { public int Code { get; set; } public string Detail { get; set; } = ""; }
}
