using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Infrastructure;

public static class DebugCommandWakeRound2Scenario
{
    public static readonly string[] Cases = ["cancel-continue", "cancel-step", "cancel-request",
        "payload-continue", "payload-step", "payload-request", "edit-continue", "edit-request",
        "pause-failure", "stale-retry", "already-applied", "committed-result-failure", "acknowledged-interruption"];
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(12);

    public static async Task VerifyAsync(IWorkflowStateRepository store, IWorkflowStateRepository otherStore, string scenario,
        Action<IServiceCollection>? configure = null, Func<IServiceProvider, Task>? startLinks = null,
        Func<IServiceProvider, Task>? stopLinks = null)
    {
        if (scenario.StartsWith("cancel-"))
        {
            await PublishedCancellationAsync(store, scenario, configure, startLinks, stopLinks);
            return;
        }
        ScopeProbe.Submitting.Value = true;
        var wrapped = DispatchProxy.Create<IWorkflowStateRepository, Probe>();
        var probe = (Probe)wrapped;
        probe.Store = store;
        using var catalog = new WorkflowCatalog();
        var definition = new WorkflowBuilder<Data, object>([], "DebugWakeRound2")
            .ForEach(c => c.WorkflowData.Items, (body, item) => body.Result(c => c.Item(item) + 1),
                setup => setup.Into(c => c.WorkflowData.Results)).WaitFor<Go>("end").Build();
        await catalog.RegisterWorkflowAsync(definition);
        await using var requester = Services(wrapped, "requester");
        await using var other = Services(otherStore, "other");
        try
        {
            if (startLinks != null) { await startLinks(requester); await startLinks(other); }
            var ops = requester.GetRequiredService<IWorkflowOperations>();
            var client = requester.GetRequiredService<IWorkflowClient>();
            var id = Guid.NewGuid().ToString("N");
            await ops.LaunchAsync(id, definition.Name, definition.Version, JsonSerializer.SerializeToElement(new Data()),
                stopOnEntry: scenario != "pause-failure", actor: "launch");
            await Until(async () => (await store.GetWorkflowInstanceAsync(id))!.Status ==
                (scenario == "pause-failure" ? WorkflowStatus.Suspended : WorkflowStatus.Paused));
            var initial = (await store.GetWorkflowInstanceAsync(id))!;
            var generation = WorkflowDebugRules.Checkpoint(initial).PauseGeneration;
            var continuation = Assert.Single((await client.GetSnapshotAsync(id))!.Pointers).PointerId;
            using var cancellation = new CancellationTokenSource();

            if (scenario.StartsWith("payload-"))
            {
                probe.OnAccepted = (_, _) =>
                {
                    probe.FailPayloadReads = 1;
                    return Task.CompletedTask;
                };
                Exception? error = null;
                try { await Command(); } catch (Exception caught) { error = caught; }
                Assert.NotNull(error);
                Assert.True(probe.PayloadFailures > 0, "The failure must occur in a payload read after acceptance");
                await Until(async () => (await store.GetDebugStatesAsync([id]))[id].Commands.All(c => c.AcknowledgedRevision != null) &&
                    (await store.GetWorkflowInstanceAsync(id))!.Status != WorkflowStatus.Running);
            }
            else if (scenario.StartsWith("edit-"))
            {
                probe.FailEdit = true;
                await Assert.ThrowsAnyAsync<Exception>(() => ops.EditDataAsync(id, "edit", initial.Revision,
                    new(generation), [new([nameof(Data.Items)], JsonSerializer.SerializeToElement(new[] { 99 }))], "editor"));
                var pending = Assert.Single((await store.GetDebugStatesAsync([id]))[id].Commands, c => c.Kind == DebugCommandKind.EditData);
                Assert.Null(pending.AcknowledgedRevision);
                probe.OnAccepted = (request, _) => { if (request.Kind == DebugCommandKind.Continue) probe.FailEdit = false; return Task.CompletedTask; };
                await Command();
                await Until(async () => (await store.GetWorkflowInstanceAsync(id))!.Status == WorkflowStatus.Suspended);
                var trace = await store.GetTraceAsync(id);
                Assert.Single(trace, e => e.Entry.EntryType == TraceEntryType.DataEdited);
                Assert.Equal(100, Assert.Single((await SavedData()).Results).Value);
            }
            else if (scenario == "pause-failure")
            {
                probe.OnAccepted = (_, _) => { probe.FailInstanceReads = 1; return Task.CompletedTask; };
                await Assert.ThrowsAnyAsync<Exception>(() => ops.PauseAsync(id, "operator"));
                await Until(async () => (await store.GetWorkflowInstanceAsync(id))!.Status == WorkflowStatus.Paused);
                Assert.Equal(generation + 1, WorkflowDebugRules.Checkpoint((await store.GetWorkflowInstanceAsync(id))!).PauseGeneration);
                Assert.Single(await store.GetTraceAsync(id), e => e.Entry.EntryType == TraceEntryType.Paused);
            }
            else if (scenario == "stale-retry")
            {
                // Pause once more before entering the wait, then make a second Continue suspend.
                var bp = Assert.Single(BreakpointResolver.Resolve(definition, [new("next", "root/1")]));
                Assert.Equal(DebugCommandStatus.Accepted, (await store.RequestDebugCommandAsync(id,
                    new("breakpoint", DebugCommandKind.SetBreakpoints, generation, "setup", [bp]) { ExpectedIncarnation = initial.Incarnation })).Status);
                Assert.NotNull(await otherStore.TryAcquireExecutionLeaseAsync(id, "held", TimeSpan.FromMinutes(1),
                    await StoreTestSupport.ClaimAsync(other), ExecutionClaimKind.Operator));
                probe.BlockAfterRefusal = true;
                var refused = await ops.ContinueAsync(id, "first");
                Assert.Equal(WorkflowExecutionStatus.Running, refused.Status);
                await probe.StaleReadStarted.Task.WaitAsync(Patience);
                Assert.True(probe.Claims >= 2, "The local recovery wake itself must encounter the held lease");
                Assert.True((await otherStore.ReleaseExecutionLeaseAsync(id, "held")).Released);
                var first = await other.GetRequiredService<IWorkflowClient>().RecoverAsync(id);
                Assert.Equal(WorkflowExecutionStatus.Paused, first.Status);
                Assert.Equal(generation + 1, WorkflowDebugRules.Checkpoint((await store.GetWorkflowInstanceAsync(id))!).PauseGeneration);
                probe.OnAccepted = async (request, _) =>
                {
                    if (request.Actor != "second") return;
                    probe.SecondAccepted.TrySetResult();
                    await probe.ReleaseSecond.Task.WaitAsync(Patience);
                };
                var next = ops.ContinueAsync(id, "second");
                await probe.SecondAccepted.Task.WaitAsync(Patience);
                var claims = probe.Claims;
                probe.ReleaseStale.TrySetResult();
                // Scope disposal proves the old attempt has finished, including its lease and
                // result construction. No timing delay decides which command wins this race.
                await probe.StaleScopeDisposed.Task.WaitAsync(Patience);
                Assert.Equal(0, probe.StaleReadTimeouts);
                Assert.Equal(claims, probe.Claims);
                Assert.Equal(WorkflowStatus.Paused, (await store.GetWorkflowInstanceAsync(id))!.Status);
                probe.ReleaseSecond.TrySetResult();
                var result = await next.WaitAsync(Patience);
                Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
                Assert.Null(result.ErrorKind);
            }
            else if (scenario == "committed-result-failure")
            {
                probe.FailResultAfterCommit = true;
                await Assert.ThrowsAnyAsync<Exception>(() => ops.ContinueAsync(id, "operator"));
                Assert.Equal(WorkflowStatus.Suspended, (await store.GetWorkflowInstanceAsync(id))!.Status);
                var claims = probe.Claims;
                await Task.Delay(1200);
                Assert.Equal(claims, probe.Claims);
                Assert.Equal(0, probe.PostFailureInboxReads);
                Assert.True(probe.PayloadFailures > 0);
            }
            else if (scenario == "already-applied")
            {
                probe.OnAccepted = async (_, _) =>
                {
                    var applied = await other.GetRequiredService<IWorkflowClient>().RecoverAsync(id);
                    Assert.Equal(WorkflowExecutionStatus.Suspended, applied.Status);
                };
                var result = await ops.ContinueAsync(id, "operator");
                Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
                Assert.Null(result.ErrorKind);
            }
            else if (scenario == "acknowledged-interruption")
            {
                probe.FailAfterResume = true;
                await Assert.ThrowsAnyAsync<Exception>(() => ops.ContinueAsync(id, "operator"));
                var command = Assert.Single((await store.GetDebugStatesAsync([id]))[id].Commands);
                Assert.NotNull(command.AcknowledgedRevision);
                probe.FailAfterResume = false;
                await Until(async () => (await store.GetWorkflowInstanceAsync(id))!.Status == WorkflowStatus.Suspended);
            }
            else throw new ArgumentOutOfRangeException(nameof(scenario));

            var commands = (await store.GetDebugStatesAsync([id]))[id].Commands;
            Assert.All(commands, command => Assert.NotNull(command.AcknowledgedRevision));
            var savedTrace = await store.GetTraceAsync(id);
            foreach (var command in commands.Where(c => c.Kind is DebugCommandKind.Continue or DebugCommandKind.Step))
                Assert.Single(savedTrace, e => e.Entry.EntryType == TraceEntryType.Continued &&
                    e.Entry.Payload is DebugCommandTrace applied && applied.CommandId == command.Id);
            if (scenario != "pause-failure")
                Assert.Equal(scenario.StartsWith("edit-") ? 100 : 42, Assert.Single((await SavedData()).Results).Value);

            async Task Command()
            {
                if (scenario.EndsWith("request")) await ops.RequestContinueAsync(id, "operator", cancellation.Token);
                else if (scenario.EndsWith("step")) await ops.StepAsync(id, continuation, DebugStepKind.Over, "operator", cancellation.Token);
                else await ops.ContinueAsync(id, "operator", cancellation.Token);
            }
            async Task<Data> SavedData()
            {
                var saved = (await store.GetWorkflowInstanceAsync(id))!;
                var reader = new PayloadReader(async reference => (await store.PayloadStore.GetAsync(reference))!.Value, new(8 * 1024 * 1024));
                return JsonSerializer.Deserialize<Data>(await reader.ReadAsync(saved.WorkflowDataJson))!;
            }
        }
        finally
        {
            probe.FailEdit = false; probe.FailAfterResume = false;
            probe.ReleaseStale.TrySetResult(); probe.ReleaseSecond.TrySetResult();
            foreach (var provider in new[] { requester, other })
            {
                if (stopLinks != null) await stopLinks(provider);
                await provider.GetRequiredService<WorkflowHost>().StopAsync(CancellationToken.None);
            }
        }

        ServiceProvider Services(IWorkflowStateRepository repository, string name)
        {
            var services = new ServiceCollection().AddLogging();
            services.AddIxIFlow(options => { options.Debugging = true; options.HostName = name; options.PayloadThresholdBytes = 64; });
            services.AddSingleton(repository);
            if (ReferenceEquals(repository, wrapped))
            {
                services.AddScoped(_ => new ScopeProbe(probe));
                services.AddScoped<IWorkflowStateRepository>(provider =>
                {
                    ScopeProbe.Current.Value = provider.GetRequiredService<ScopeProbe>();
                    return repository;
                });
            }
            services.AddSingleton<IWorkflowCatalog>(catalog);
            configure?.Invoke(services);
            // Simulate separate processes; the default gate is shared by providers in one process.
            services.AddSingleton(new InProcessInstanceGate());
            // No scanner or hosted recovery service is started.
            return services.BuildServiceProvider();
        }
    }

    private static async Task PublishedCancellationAsync(IWorkflowStateRepository store, string scenario,
        Action<IServiceCollection>? configure, Func<IServiceProvider, Task>? startLinks, Func<IServiceProvider, Task>? stopLinks)
    {
        var version = await PublishedBundleScenario.PublishAsync(store);
        var wrapped = DispatchProxy.Create<IWorkflowStateRepository, Probe>();
        var probe = (Probe)wrapped;
        probe.Store = store;
        var cache = Path.Combine(Path.GetTempPath(), "DebugWakeRound2", Guid.NewGuid().ToString("N"));
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow(options => options.Debugging = true).UseWorkflowBundles(cache);
        services.AddSingleton(wrapped);
        configure?.Invoke(services);
        var provider = services.BuildServiceProvider();
        try
        {
            if (startLinks != null) await startLinks(provider);
            var ops = provider.GetRequiredService<IWorkflowOperations>();
            var id = Guid.NewGuid().ToString("N");
            await ops.LaunchAsync(id, version.Name, version.Version,
                JsonSerializer.SerializeToElement(new { Value = "41" }), true, "launch");
            await Until(async () => (await store.GetWorkflowInstanceAsync(id))!.Status == WorkflowStatus.Paused);
            Assert.NotNull((await store.GetWorkflowInstanceAsync(id))!.BundleDigest);
            var continuation = Assert.Single((await provider.GetRequiredService<IWorkflowClient>().GetSnapshotAsync(id))!.Pointers).PointerId;
            using var cancellation = new CancellationTokenSource();
            probe.OnAccepted = (_, _) => { cancellation.Cancel(); return Task.CompletedTask; };
            // This cancels the real bundle reacquisition in result hydration after the command
            // write. No injected cancellation exception or explicit recovery supplies the wake.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                if (scenario.EndsWith("request")) await ops.RequestContinueAsync(id, "operator", cancellation.Token);
                else if (scenario.EndsWith("step")) await ops.StepAsync(id, continuation, DebugStepKind.Over, "operator", cancellation.Token);
                else await ops.ContinueAsync(id, "operator", cancellation.Token);
            });
            await Until(async () => (await store.GetDebugStatesAsync([id]))[id].Commands.Single().AcknowledgedRevision != null &&
                (await store.GetWorkflowInstanceAsync(id))!.Status is WorkflowStatus.Suspended or WorkflowStatus.Paused);
            var saved = (await store.GetWorkflowInstanceAsync(id))!;
            if (!scenario.EndsWith("step")) Assert.Equal(WorkflowStatus.Suspended, saved.Status);
            var command = Assert.Single((await store.GetDebugStatesAsync([id]))[id].Commands);
            Assert.NotNull(command.AcknowledgedRevision);
            Assert.Single(await store.GetTraceAsync(id), e => e.Entry.EntryType == TraceEntryType.Continued &&
                e.Entry.Payload is DebugCommandTrace applied && applied.CommandId == command.Id);
            Assert.Equal(saved.Status == WorkflowStatus.Paused ? 2 : 1, WorkflowDebugRules.Checkpoint(saved).PauseGeneration);
        }
        finally
        {
            if (stopLinks != null) await stopLinks(provider);
            await provider.GetRequiredService<WorkflowHost>().StopAsync(CancellationToken.None);
            await provider.DisposeAsync();
            if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
        }
    }

    private static async Task Until(Func<Task<bool>> check)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!await check())
        {
            Assert.True(DateTime.UtcNow < deadline, "Accepted debug command was stranded without a scanner");
            await Task.Delay(20);
        }
    }

    public class Probe : DispatchProxy
    {
        public IWorkflowStateRepository Store = null!;
        public Func<DebugCommandRequest, DebugCommandResult, Task>? OnAccepted;
        public int FailPayloadReads, PayloadFailures, FailInstanceReads, Claims;
        public int PostFailureInboxReads;
        public int StaleReadTimeouts;
        public bool FailEdit, FailResultAfterCommit, FailAfterResume, BlockAfterRefusal;
        private bool _resumeCommitted;
        private int _blockRetryRead;
        public TaskCompletionSource StaleReadStarted = Signal(), ReleaseStale = Signal(), StaleScopeDisposed = Signal(),
            SecondAccepted = Signal(), ReleaseSecond = Signal();
        private IPayloadStore? _payloads;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "get_PayloadStore")
            {
                if (_payloads != null) return _payloads;
                _payloads = DispatchProxy.Create<IPayloadStore, PayloadProbe>();
                ((PayloadProbe)_payloads).Owner = this;
                return _payloads;
            }
            if (method.Name == nameof(IWorkflowStateRepository.RequestDebugCommandAsync)) return Accept((string)args![0]!, (DebugCommandRequest)args[1]!);
            if (method.Name == nameof(IWorkflowStateRepository.CommitWorkflowInstanceAsync)) return Commit((CheckpointCommit)args![0]!);
            if (method.Name == nameof(IWorkflowStateRepository.TryAcquireExecutionLeaseAsync)) return Acquire(method, args!);
            if (method.Name == nameof(IWorkflowStateRepository.GetWorkflowInstanceAsync)) return Read((string)args![0]!);
            if (method.Name == nameof(IWorkflowStateRepository.GetPendingEventsAsync) && PayloadFailures > 0)
                Interlocked.Increment(ref PostFailureInboxReads);
            return Forward(method, Store, args);
        }
        private async Task<DebugCommandResult> Accept(string id, DebugCommandRequest request)
        {
            var result = await Store.RequestDebugCommandAsync(id, request);
            if (result.Status is DebugCommandStatus.Accepted or DebugCommandStatus.AlreadyAccepted && OnAccepted != null)
                await OnAccepted(request, result);
            return result;
        }
        private async Task<WorkflowInstance?> Read(string id)
        {
            if (Interlocked.Exchange(ref FailInstanceReads, 0) > 0) throw new IOException("Injected state read failure after Pause acceptance");
            var block = ScopeProbe.Current.Value?.Submission != true &&
                Interlocked.Exchange(ref _blockRetryRead, 0) > 0;
            if (block)
            {
                Assert.NotNull(ScopeProbe.Current.Value);
                ScopeProbe.Current.Value.Stale = true;
                StaleReadStarted.TrySetResult();
                try { await ReleaseStale.Task.WaitAsync(Patience); }
                catch (TimeoutException) { Interlocked.Increment(ref StaleReadTimeouts); throw; }
            }
            var instance = await Store.GetWorkflowInstanceAsync(id);
            return instance;
        }
        private async Task<ExecutionLeaseGrant?> Acquire(MethodInfo method, object?[] args)
        {
            Interlocked.Increment(ref Claims);
            var result = await (Task<ExecutionLeaseGrant?>)Forward(method, Store, args)!;
            if (result == null && BlockAfterRefusal && Claims >= 2) { BlockAfterRefusal = false; _blockRetryRead = 1; }
            return result;
        }
        private async Task<WorkflowCommitResult> Commit(CheckpointCommit commit)
        {
            if (FailEdit && commit.ReadTraceEntries().Any(e => e.EntryType == TraceEntryType.DataEdited)) throw new IOException("Interrupted edit commit");
            if (FailAfterResume && _resumeCommitted) throw new IOException("Interrupted after resume acknowledgement");
            var result = await Store.CommitWorkflowInstanceAsync(commit);
            if (result.Status == WorkflowCommitStatus.Applied && commit.ReadTraceEntries().Any(e => e.EntryType == TraceEntryType.Continued)) _resumeCommitted = true;
            if (FailResultAfterCommit && commit.Instance.Status == WorkflowStatus.Suspended && result.Status == WorkflowCommitStatus.Applied)
            { FailResultAfterCommit = false; FailPayloadReads = 1; }
            return result;
        }
    }
    private sealed class ScopeProbe(Probe owner) : IDisposable
    {
        public static readonly AsyncLocal<ScopeProbe?> Current = new();
        public static readonly AsyncLocal<bool> Submitting = new();
        public bool Submission { get; } = Submitting.Value;
        public bool Stale;
        public void Dispose()
        {
            if (Stale) owner.StaleScopeDisposed.TrySetResult();
        }
    }
    public class PayloadProbe : DispatchProxy
    {
        public Probe Owner = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IPayloadStore.GetAsync) && Interlocked.Exchange(ref Owner.FailPayloadReads, 0) > 0)
            {
                Interlocked.Increment(ref Owner.PayloadFailures);
                throw new IOException("Injected payload-read failure after acceptance");
            }
            return Forward(method, Owner.Store.PayloadStore, args);
        }
    }
    private static object? Forward(MethodInfo method, object target, object?[]? args)
    {
        try { return method.Invoke(target, args); }
        catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException!).Throw(); throw; }
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public sealed class Data
    {
        public List<int> Items { get; set; } = [41];
        public List<ItemResult<int>> Results { get; set; } = [];
        public string Padding { get; set; } = new('x', 1024);
    }
    public sealed class Go { }
}
