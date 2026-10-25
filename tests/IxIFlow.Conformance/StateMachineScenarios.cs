using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Debugging;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "control-flow"), Trait("Spec", "W-02")]
public sealed partial class StateMachineScenarios
{
    public static IEnumerable<object[]> Races => Cases("events", "event-timer", "timers");
    public static IEnumerable<object[]> Replies => Cases("early", "two-early", "armed", "fired");
    public static IEnumerable<object[]> Recoveries => Cases("running", "ready", "armed", "fired", "rearm", "exiting", "transition", "action", "transition-done", "leaving", "final", "final-done")
        .SelectMany(row => new[] { false, true }.Select(prepare => row.Append((object)prepare).ToArray()));
    public static IEnumerable<object[]> Payloads => Cases("immediate", "duration", "rearm");
    public static IEnumerable<object[]> Arbitration => Cases("reply-first", "immediate-first");
    public static IEnumerable<object[]> Effects => Cases("exit", "action", "target");
    public static IEnumerable<object[]> Acceptance => Cases("before", "after");
    public static IEnumerable<object[]> OutputFailures => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { "event", "reply" }.SelectMany(kind => new[] { false, true }
            .Select(recover => new object[] { profile, kind, recover })));
    public static IEnumerable<object[]> Steps => StoreProfiles.ProfileNames.SelectMany(profile => new object[][]
    {
        [profile, DebugStepKind.Into], [profile, DebugStepKind.Over]
    });
    private static IEnumerable<object[]> Cases(params string[] cases) =>
        StoreProfiles.ProfileNames.SelectMany(profile => cases.Select(value => new object[] { profile, value }));

    private static async Task EveryNativePhaseResumesFromTheStoreWithItsSavedPreviousAndChoice(string profile, string phase, bool prepare)
    {
        await using var cluster = await ConformanceCluster.CreateAsync(profile, _ => { });
        var client = cluster.AddHost();
        var definition = new WorkflowBuilder<Data, Incoming>([], "StoredStatePhase" + phase)
            .StateMachine(m => m.Start("a").State("a", s => s.Exit(e => e)
                    .On<Signal>("pulse", t => t.When(c => c.PreviousStep.Number == 1, "done")))
                .State("done", s => s.Final()))
            .Step<Copy>(a => a.Input(x => x.Text).From(c => c.PreviousStep.Text)
                .Output(x => x.Text).To(c => c.WorkflowData.Text)).Build();
        await cluster.Registry.RegisterWorkflowAsync(definition);
        var (instance, checkpoint, scopes) = WorkflowInstances.NewInstance(definition, new Data(), new(), DateTime.UtcNow);
        checkpoint.RootData = new Data();
        using var local = new ServiceCollection().BuildServiceProvider();
        var branch = checkpoint.Continuations[0];
        branch.SetPrevious(new Incoming()); branch.Previous = SerializedValue.From(branch.RuntimePrevious);
        checkpoint.Trace.Configure(instance, checkpoint, ExecutionLimits.Default, null, scopes);
        var transitions = new ContinuationTransitions(local, TimeProvider.System);
        void Advance() => transitions.Advance(scopes, checkpoint, branch, 100, 100);
        Advance();
        if (phase != "running") Advance();
        if (phase is not ("running" or "ready"))
        {
            Advance();
            if (phase != "armed")
            {
                checkpoint.RemoveWaits(_ => true, WaitCloseReason.Answered);
                branch.Status = ContinuationStatus.Active;
                branch.AcceptedWait = new AcceptedWaitState { StepId = "root/0/state/a/on/0/0",
                    Event = SerializedValue.From(new Signal { Number = phase == "rearm" ? 0 : 1 }) };
                GraphTransitions.TriggerFired(checkpoint, branch);
                if (phase != "fired") Advance();
                var extra = phase switch { "transition" => 2, "action" => 3, "transition-done" => 5,
                    "leaving" => 6, "final" => 7, "final-done" => 8, _ => 0 };
                for (var i = 0; i < extra; i++) Advance();
            }
        }
        var expectedPhase = phase switch
        {
            "running" => "Running", "ready" or "rearm" => "Ready", "armed" => "Armed", "fired" => "Fired",
            "exiting" => "Exiting", "transition" => "TransitionChosen", "action" => "Action",
            "transition-done" => "TransitionDone", "leaving" => "Leaving", _ => "FinalEntry"
        };
        Assert.Equal(expectedPhase, GraphFrameInfo.Of(scopes, branch, branch.Stack.Count - 1)!.Phase);
        if (phase == "final-done") Assert.Equal(scopes.Steps(branch.Stack[^1].ScopeId).Count, branch.Stack[^1].NextStepIndex);
        if (prepare) ContinuationTransitions.PrepareCheckpoint(checkpoint, scopes);
        else SaveLivePrevious(branch);
        instance.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
        var seedRepository = cluster.NewRepository();
        var grant = await StoreTestSupport.LeaseAsync(seedRepository, instance.InstanceId);
        var changes = checkpoint.Inbox.WaitChanges(checkpoint);
        Assert.Equal(WorkflowCommitStatus.Applied, (await seedRepository.CommitWorkflowInstanceAsync(
            CheckpointCommit.Create(instance, grant.Epoch, 0, openedWaits: changes.Opened))).Status);
        await StoreTestSupport.ReleaseAsync(seedRepository, instance.InstanceId);
        var result = await client.RecoverAsync(instance.InstanceId);
        if (phase is "running" or "ready" or "armed" or "rearm")
        {
            Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
            if (phase == "armed")
            {
                var repository = cluster.NewRepository();
                var saved = (await repository.GetWorkflowInstanceAsync(instance.InstanceId))!;
                var armed = ExecutionCheckpoint.Read(saved.ExecutionStateJson);
                Assert.Equal("Armed", GraphFrameInfo.Of(scopes, armed.Continuations[0], 1)!.Phase);
                Assert.Single(await repository.GetOpenWaitsAsync(new(InstanceId: instance.InstanceId)));
            }
            result = await client.ResumeAsync(instance.InstanceId, "pulse", new Signal { Number = 1 });
        }
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal("incoming", Assert.IsType<Data>(result.WorkflowData).Text);
    }

    private static bool ThrowGuard() => throw new InvalidOperationException("guard failed");
    private static void SaveLivePrevious(ContinuationState branch)
    {
        // Retain the live serialized value even at Count, where preparation normally discards it.
        if (branch.RuntimePrevious != null) branch.Previous = SerializedValue.From(branch.RuntimePrevious);
        foreach (var frame in branch.Stack)
            if (frame.RuntimeEntryPrevious != null) frame.EntryPrevious = SerializedValue.From(frame.RuntimeEntryPrevious);
    }
    private static FakeTimeProvider? Clock(string profile) =>
        profile is StoreProfiles.LocalMemory or StoreProfiles.LocalSqlite ? new FakeTimeProvider() : null;

    private static async Task Append(IWorkflowStateRepository repository, string instance, string id, string key, Signal payload)
    {
        Assert.Equal(EventAppendStatus.Stored, (await repository.AppendEventAsync(new(instance, id,
            WorkflowTypeIdentity.SavedName(typeof(Signal)), JsonSerializer.Serialize(payload), key))).Status);
    }

    private static async Task<StoredWait> Timer(IWorkflowStateRepository repository, string id)
    {
        var page = await repository.GetPendingEventsAsync(id, 0, 100);
        return (await repository.GetWaitAsync(id, Assert.Single(page.Deadlines!).Key))!;
    }

    private static async Task Due(IWorkflowStateRepository repository, string id, FakeTimeProvider? time)
    {
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var page = await repository.GetPendingEventsAsync(id, 0, 100).WaitAsync(patience.Token);
        // A live owner can already have processed the deadline (and paused or completed).
        if (page.Deadlines!.Count == 0) return;
        var deadline = page.Deadlines!.Values.Max();
        if (time != null)
        {
            if (deadline >= page.StoreTimeUtc!.Value) time.Advance(deadline - page.StoreTimeUtc.Value + TimeSpan.FromMilliseconds(1));
        }
        else while (page.StoreTimeUtc!.Value < deadline)
        {
            await Task.Delay(10, patience.Token);
            page = await repository.GetPendingEventsAsync(id, 0, 100).WaitAsync(patience.Token);
        }
    }

    private static StateMachineHost Services(ProfileStore store, IWorkflowStateRepository repository,
        TimeProvider? time = null, Probe? probe = null, IWorkflowCatalog? registry = null,
        bool compiler = false, long threshold = 0)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow(o => { o.Debugging = true; o.PayloadThresholdBytes = threshold; o.VerifyCheckpointParts = true; });
        services.AddSingleton(repository);
        services.AddSingleton(probe ?? new Probe());
        if (registry != null) services.AddSingleton(registry);
        if (time != null) services.AddSingleton(time);
        if (compiler) services.AddBreakpointConditions();
        store.ConfigureHost(services);
        var provider = services.BuildServiceProvider();
        try { store.StartHostLinksAsync(provider).GetAwaiter().GetResult(); return new(provider); }
        catch { provider.DisposeAsync().AsTask().GetAwaiter().GetResult(); throw; }
    }

    private sealed class StateMachineHost(ServiceProvider provider) : IServiceProvider, IAsyncDisposable
    {
        public object? GetService(Type type) => provider.GetService(type);
        public async ValueTask DisposeAsync()
        {
            await provider.GetRequiredService<WorkflowHost>().StopAsync(CancellationToken.None);
            await provider.DisposeAsync();
        }
    }

    public sealed class Data
    {
        public Signal? Missing { get; set; }
        public int Number { get; set; }
        public string Text { get; set; } = "";
        public List<int> Items { get; set; } = [1, 2];
    }
    public sealed class Signal
    {
        public int Number { get; set; }
        public string Text { get; set; } = "";
    }
    public sealed class Probe
    {
        public TaskCompletionSource BodyEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BodyCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseBody { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Dictionary<string, string> Tokens { get; } = [];
        public List<string> Marks { get; } = [];
        public Dictionary<string, string> Values { get; } = [];
    }
    public sealed class Ask(Probe probe) : IRequestActivity<Signal>
    {
        public string Name { get; set; } = "";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { probe.Tokens[Name] = context.Reply!.Token; return Task.CompletedTask; }
    }
    public sealed class Mark(Probe probe) : IAsyncActivity
    {
        public string Name { get; set; } = "";
        public string Value { get; set; } = "";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { probe.Marks.Add(Name); probe.Values[Name] = Value; return Task.CompletedTask; }
    }
    public sealed class Incoming : IAsyncActivity
    {
        public string Text { get; set; } = "incoming";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public sealed class Copy : IAsyncActivity
    {
        public string Text { get; set; } = "";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public sealed class Fail : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("entry failed"));
    }
}
