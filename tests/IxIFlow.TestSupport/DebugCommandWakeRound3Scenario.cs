using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Infrastructure;

public static class DebugCommandWakeRound3Scenario
{
    public static readonly string[] Cases = [
        "edit-alone", "edit-conditions-no-compiler", "stale-before-edit", "reconciled-running",
        "suspended-between-reads", "completed-between-reads", "pause-before-continue", "pause-before-request",
        "pause-before-step", "edit-before-step", "breakpoint-failure", "acknowledged-step",
        "acknowledged-edit", "acknowledged-pause", "acknowledged-breakpoints",
        "disabled-continue", "disabled-step", "disabled-edit", "conditions-continue", "conditions-step"];
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan SetupPatience = Patience + TimeSpan.FromSeconds(5);

    public static async Task VerifyAsync(IWorkflowStateRepository store, IWorkflowStateRepository otherStore, string scenario,
        Action<IServiceCollection>? configure = null, Func<IServiceProvider, Task>? startLinks = null,
        Func<IServiceProvider, Task>? stopLinks = null)
    {
        var wrapped = DispatchProxy.Create<IWorkflowStateRepository, Probe>();
        var probe = (Probe)wrapped; probe.Store = store;
        var remote = DispatchProxy.Create<IWorkflowStateRepository, Probe>();
        var remoteProbe = (Probe)remote; remoteProbe.Store = otherStore;
        using var catalog = new WorkflowCatalog();
        var builder = new WorkflowBuilder<Data, object>([], "DebugWakeRound3")
            .ForEach(c => c.WorkflowData.Items, (body, item) => body.Result(c => c.Item(item) + 1),
                setup => setup.Into(c => c.WorkflowData.Results));
        var definition = scenario == "completed-between-reads" ? builder.Build() : builder.WaitFor<Go>("end").Build();
        await catalog.RegisterWorkflowAsync(definition);
        await using var requester = Services(wrapped, "requester", true);
        await using var other = Services(remote, "other", true);
        await using var disabled = Services(wrapped, "disabled", false);
        Task<WorkflowExecutionResult>? remoteRun = null;
        try
        {
            if (startLinks != null)
                foreach (var provider in new[] { requester, other, disabled }) await startLinks(provider);
            var ops = requester.GetRequiredService<IWorkflowOperations>();
            var id = Guid.NewGuid().ToString("N");
            var earlierPause = scenario.StartsWith("pause-before-") || scenario == "acknowledged-pause";
            await ops.LaunchAsync(id, definition.Name, definition.Version, JsonSerializer.SerializeToElement(new Data()),
                stopOnEntry: !earlierPause, actor: "launch");
            await Until(async () => (await store.GetWorkflowInstanceAsync(id))!.Status ==
                (earlierPause ? WorkflowStatus.Suspended : WorkflowStatus.Paused));
            var initial = (await store.GetWorkflowInstanceAsync(id))!;
            var generation = WorkflowDebugRules.Checkpoint(initial).PauseGeneration;
            var continuation = Assert.Single((await requester.GetRequiredService<IWorkflowClient>().GetSnapshotAsync(id))!.Pointers).PointerId;
            var claims = probe.Claims;

            if (scenario is "edit-alone" or "edit-conditions-no-compiler")
            {
                if (scenario == "edit-conditions-no-compiler")
                {
                    // Configuration can arrive from a compiler-capable host. Editing never evaluates it.
                    initial = await SetConditionalBreakpointsAsync();
                    Assert.Null(requester.GetService<IBreakpointConditionCompiler>());
                    Assert.Single(WorkflowDebugRules.Checkpoint(initial).Breakpoints, b => b.Condition != null);
                }
                probe.FailEdits = true;
                await Assert.ThrowsAnyAsync<Exception>(Edit);
                var accepted = Assert.Single((await store.GetDebugStatesAsync([id]))[id].Commands, c => c.Kind == DebugCommandKind.EditData);
                Assert.Null(accepted.AcknowledgedRevision);
                probe.FailEdits = false;
                // Only the edit's own wake can acknowledge it. Polling is strictly read-only.
                await Until(async () => (await store.GetDebugStatesAsync([id]))[id].Commands.Single(c => c.Id == accepted.Id).EditResult != null);
                var saved = (await store.GetWorkflowInstanceAsync(id))!;
                Assert.Equal(WorkflowStatus.Paused, saved.Status);
                Assert.Equal(generation, WorkflowDebugRules.Checkpoint(saved).PauseGeneration);
                Assert.Equal(new[] { 99 }, (await SavedData()).Items);
                Assert.Empty((await SavedData()).Results);
                Assert.Single(await store.GetTraceAsync(id), e => e.Entry.EntryType == TraceEntryType.DataEdited);
                Assert.DoesNotContain((await store.GetDebugStatesAsync([id]))[id].Commands,
                    c => c.Kind is DebugCommandKind.Continue or DebugCommandKind.Step);
            }
            else if (scenario == "stale-before-edit")
            {
                await Accept(DebugCommandKind.SetBreakpoints, "pause-again", breakpoints:
                    BreakpointResolver.Resolve(definition, [new("next", "root/1")]));
                probe.OnAccepted = (_, _) => { probe.DebugReads = 0; probe.Armed = true; return Task.CompletedTask; };
                probe.BeforeDebugRead = async read =>
                {
                    if (read != 2) return; // Initial receipt reconciliation already returned pending.
                    Assert.Equal(WorkflowExecutionStatus.Paused, (await RemoteRecover()).Status);
                    var paused = (await store.GetWorkflowInstanceAsync(id))!;
                    Assert.Equal(generation + 1, WorkflowDebugRules.Checkpoint(paused).PauseGeneration);
                    var edit = await store.RequestDebugCommandAsync(id, new("later-edit", DebugCommandKind.EditData,
                        generation + 1, "later", Edit: EditRequest(paused)) { ExpectedIncarnation = paused.Incarnation });
                    Assert.Equal(DebugCommandStatus.Accepted, edit.Status);
                };
                var result = await ops.ContinueAsync(id, "stale");
                Assert.Equal(WorkflowExecutionStatus.Paused, result.Status);
                Assert.True(probe.DebugReads >= 2, "The race must pass initial reconciliation before exposing the later edit");
                var later = (await store.GetDebugStatesAsync([id]))[id].Commands.Single(c => c.Id == "later-edit");
                Assert.Null(later.AcknowledgedRevision);
                Assert.Equal(new[] { 41 }, (await SavedData()).Items);
                Assert.DoesNotContain(await store.GetTraceAsync(id), e => e.Entry.EntryType == TraceEntryType.DataEdited);
                Assert.Equal(claims, probe.Claims);
                // The later receipt remains independently recoverable.
                probe.Armed = false;
                await requester.GetRequiredService<WorkflowHost>().WakeDebugCommandIfNeededAsync(Receipt(later));
                await Until(async () => (await store.GetDebugStatesAsync([id]))[id].Commands.Single(c => c.Id == later.Id).EditResult != null);
            }
            else if (scenario is "reconciled-running" or "suspended-between-reads" or "completed-between-reads")
            {
                probe.OnAccepted = (_, _) => { probe.DebugReads = 0; probe.Armed = true; return Task.CompletedTask; };
                probe.BeforeDebugRead = async read =>
                {
                    if (read != (scenario == "reconciled-running" ? 1 : 2)) return;
                    if (scenario == "reconciled-running")
                    {
                        remoteProbe.HoldAfterResume = true;
                        remoteRun = RemoteRecover();
                        await remoteProbe.ResumeCommitted.Task.WaitAsync(Patience);
                        Assert.Equal(WorkflowStatus.Running, (await store.GetWorkflowInstanceAsync(id))!.Status);
                    }
                    else
                    {
                        var applied = await RemoteRecover();
                        Assert.Equal(scenario == "completed-between-reads" ? WorkflowExecutionStatus.Success : WorkflowExecutionStatus.Suspended, applied.Status);
                    }
                };
                var result = await ops.ContinueAsync(id, "operator");
                Assert.True(probe.DebugReads >= (scenario == "reconciled-running" ? 1 : 2));
                Assert.Equal(scenario == "reconciled-running" ? WorkflowExecutionStatus.Running :
                    scenario == "completed-between-reads" ? WorkflowExecutionStatus.Success : WorkflowExecutionStatus.Suspended, result.Status);
                if (scenario != "reconciled-running")
                {
                    Assert.Null(result.ErrorKind);
                    Assert.Equal(42, Assert.Single(Assert.IsType<Data>(result.WorkflowData).Results).Value);
                    Assert.Equal(claims, probe.Claims);
                }
                remoteProbe.ReleaseResume.TrySetResult();
                if (remoteRun != null) await remoteRun.WaitAsync(Patience);
            }
            else if (scenario.StartsWith("pause-before-"))
            {
                // Only the later receipt owns a wake; it must carry the earlier Pause through
                // its separate lease and then obtain another assignment for the resume.
                var pause = await Accept(DebugCommandKind.Pause, "earlier-pause");
                Assert.NotNull(await otherStore.TryAcquireExecutionLeaseAsync(id, "held", TimeSpan.FromMinutes(1),
                    await StoreTestSupport.ClaimAsync(other), ExecutionClaimKind.Operator));
                Assert.Null(pause.AcknowledgedRevision);
                if (scenario.EndsWith("request")) await ops.RequestContinueAsync(id, "later");
                else if (scenario.EndsWith("step")) await ops.StepAsync(id, continuation, DebugStepKind.Over, "later");
                else await ops.ContinueAsync(id, "later");
                var resume = (await store.GetDebugStatesAsync([id]))[id].Commands.Single(c => c.Kind is DebugCommandKind.Continue or DebugCommandKind.Step);
                Assert.Null(resume.AcknowledgedRevision);
                Assert.True((await otherStore.ReleaseExecutionLeaseAsync(id, "held")).Released);
                await Until(async () => (await store.GetDebugStatesAsync([id]))[id].Commands.All(c => c.AcknowledgedRevision != null) &&
                    (await store.GetWorkflowInstanceAsync(id))!.Status != WorkflowStatus.Running);
                var commands = (await store.GetDebugStatesAsync([id]))[id].Commands;
                Assert.True(commands.Single(c => c.Id == pause.Id).AcknowledgedRevision < commands.Single(c => c.Id == resume.Id).AcknowledgedRevision);
                Assert.Single(await store.GetTraceAsync(id), e => e.Entry.EntryType == TraceEntryType.Paused &&
                    e.Entry.Payload is DebugCommandTrace trace && trace.CommandId == pause.Id);
                Assert.Equal(42, Assert.Single((await SavedData()).Results).Value);
            }
            else if (scenario == "edit-before-step")
            {
                probe.FailEdits = true;
                await Assert.ThrowsAnyAsync<Exception>(Edit);
                probe.OnAccepted = (request, _) => { if (request.Kind == DebugCommandKind.Step) probe.FailEdits = false; return Task.CompletedTask; };
                await ops.StepAsync(id, continuation, DebugStepKind.Over, "step");
                await Until(async () => (await store.GetDebugStatesAsync([id]))[id].Commands.All(c => c.AcknowledgedRevision != null) &&
                    (await store.GetWorkflowInstanceAsync(id))!.Status != WorkflowStatus.Running);
                var commands = (await store.GetDebugStatesAsync([id]))[id].Commands;
                Assert.True(commands.Single(c => c.Kind == DebugCommandKind.EditData).AcknowledgedRevision <
                    commands.Single(c => c.Kind == DebugCommandKind.Step).AcknowledgedRevision);
                Assert.Equal(100, Assert.Single((await SavedData()).Results).Value);
                Assert.Single(await store.GetTraceAsync(id), e => e.Entry.EntryType == TraceEntryType.DataEdited);
                var saved = (await store.GetWorkflowInstanceAsync(id))!;
                Assert.Equal(WorkflowStatus.Paused, saved.Status);
                Assert.Equal(generation + 1, WorkflowDebugRules.Checkpoint(saved).PauseGeneration);
            }
            else if (scenario == "breakpoint-failure")
            {
                // Fail after the store accepted the configuration, during disposal of its request scope.
                probe.FailScopeAfterAcceptance = true;
                await Assert.ThrowsAnyAsync<Exception>(() => ops.SetBreakpointsAsync(id, [new("next", "root/1")], "setter"));
                await Until(async () => (await store.GetDebugStatesAsync([id]))[id].Commands.Single().AcknowledgedRevision != null);
                Assert.Single(WorkflowDebugRules.Checkpoint((await store.GetWorkflowInstanceAsync(id))!).Breakpoints);
                Assert.Single(await store.GetTraceAsync(id), e => e.Entry.EntryType == TraceEntryType.BreakpointsChanged);
                Assert.Equal(WorkflowStatus.Paused, (await store.GetWorkflowInstanceAsync(id))!.Status);
            }
            else if (scenario.StartsWith("acknowledged-"))
            {
                probe.FailAfterAcknowledgement = true;
                if (scenario.EndsWith("edit")) await Assert.ThrowsAnyAsync<Exception>(Edit);
                else if (scenario.EndsWith("pause")) await Assert.ThrowsAnyAsync<Exception>(() => ops.PauseAsync(id, "pause"));
                else if (scenario.EndsWith("breakpoints"))
                {
                    // SetBreakpoints is request-only; interrupt its recovery after the acknowledgement.
                    await ops.SetBreakpointsAsync(id, [new("next", "root/1")], "setter");
                    await probe.AcknowledgementInterrupted.Task.WaitAsync(Patience);
                }
                else await Assert.ThrowsAnyAsync<Exception>(() => ops.StepAsync(id, continuation, DebugStepKind.Over, "step"));
                probe.BlockCommitsAfterResume = false;
                await Until(async () => (await store.GetDebugStatesAsync([id]))[id].Commands.All(c => c.AcknowledgedRevision != null) &&
                    (await store.GetWorkflowInstanceAsync(id))!.Status != WorkflowStatus.Running);
                Assert.Equal(1, probe.AcknowledgementFailures);
                // A successful durable acknowledgement must never be replayed after an exception.
                await requester.GetRequiredService<WorkflowHost>().StopAsync(CancellationToken.None);
                Assert.Equal(1, probe.AcknowledgementFailures);
                var saved = (await store.GetWorkflowInstanceAsync(id))!;
                Assert.Equal(WorkflowStatus.Paused, saved.Status);
                if (scenario.EndsWith("step"))
                {
                    Assert.Equal(42, Assert.Single((await SavedData()).Results).Value);
                    Assert.Equal(generation + 1, WorkflowDebugRules.Checkpoint(saved).PauseGeneration);
                }
                else
                {
                    Assert.Equal(claims + 1, probe.Claims);
                    if (scenario.EndsWith("edit")) Assert.Equal(new[] { 99 }, (await SavedData()).Items);
                    if (scenario.EndsWith("pause")) Assert.Equal(generation + 1, WorkflowDebugRules.Checkpoint(saved).PauseGeneration);
                    if (scenario.EndsWith("breakpoints")) Assert.Single(WorkflowDebugRules.Checkpoint(saved).Breakpoints);
                }
                var trace = await store.GetTraceAsync(id);
                var kind = scenario.EndsWith("edit") ? TraceEntryType.DataEdited : scenario.EndsWith("pause") ? TraceEntryType.Paused :
                    scenario.EndsWith("breakpoints") ? TraceEntryType.BreakpointsChanged : TraceEntryType.Continued;
                Assert.Single(trace, e => e.Entry.EntryType == kind);
            }
            else if (scenario.StartsWith("disabled-") || scenario.StartsWith("conditions-"))
            {
                if (scenario.StartsWith("conditions-"))
                {
                    initial = await SetConditionalBreakpointsAsync();
                    claims = probe.Claims;
                }
                var kind = scenario.EndsWith("edit") ? DebugCommandKind.EditData :
                    scenario.EndsWith("step") ? DebugCommandKind.Step : DebugCommandKind.Continue;
                var command = await Accept(kind, "held");
                var host = (scenario.StartsWith("disabled-") ? disabled : requester).GetRequiredService<WorkflowHost>();
                await host.WakeDebugCommandIfNeededAsync(Receipt(command));
                Assert.Equal(claims, probe.Claims);
                Assert.Null((await store.GetDebugStatesAsync([id]))[id].Commands.Single(c => c.Id == command.Id).AcknowledgedRevision);
                Assert.Equal(initial.Revision, (await store.GetWorkflowInstanceAsync(id))!.Revision);
                Assert.Equal(WorkflowStatus.Paused, (await store.GetWorkflowInstanceAsync(id))!.Status);
            }
            else throw new ArgumentOutOfRangeException(nameof(scenario));

            foreach (var command in (await store.GetDebugStatesAsync([id]))[id].Commands.Where(c =>
                c.AcknowledgedRevision != null && c.Kind is DebugCommandKind.Continue or DebugCommandKind.Step))
                Assert.Single(await store.GetTraceAsync(id), e => e.Entry.EntryType == TraceEntryType.Continued &&
                    e.Entry.Payload is DebugCommandTrace applied && applied.CommandId == command.Id);

            Task<DataEditResult> Edit() => ops.EditDataAsync(id, "edit", initial.Revision,
                new(generation), EditRequest(initial).Patches, "editor");
            DataEditRequest EditRequest(WorkflowInstance paused) => new(paused.Revision,
                new(WorkflowDebugRules.Checkpoint(paused).PauseGeneration), [new([nameof(Data.Items)], JsonSerializer.SerializeToElement(new[] { 99 }))]);
            async Task<DebugCommand> Accept(DebugCommandKind kind, string commandId, IReadOnlyList<Breakpoint>? breakpoints = null)
            {
                var accepted = await store.RequestDebugCommandAsync(id, new(commandId, kind, generation, "setup", breakpoints,
                    kind == DebugCommandKind.Step ? continuation : null, kind == DebugCommandKind.Step ? DebugStepKind.Over : null,
                    kind == DebugCommandKind.EditData ? EditRequest(initial) : null) { ExpectedIncarnation = initial.Incarnation });
                Assert.Equal(DebugCommandStatus.Accepted, accepted.Status);
                return accepted.Command!;
            }
            DebugCommandReceipt Receipt(DebugCommand command) => new(id, initial.Incarnation, command.Id, command.Order, command.Kind);
            async Task<WorkflowInstance> SetConditionalBreakpointsAsync()
            {
                var breakpoints = BreakpointResolver.Resolve(definition, [new("conditional", "root/1", Condition: "true")]);
                var command = await Accept(DebugCommandKind.SetBreakpoints, "conditions", breakpoints);
                var receipt = Receipt(command);
                // Use the requester's gate so the later edit cannot race setup's lease release.
                await requester.GetRequiredService<WorkflowHost>().WakeDebugCommandIfNeededAsync(receipt);
                WorkflowInstance? configured = null;
                // The original instance was already paused. Wait for this configuration's
                // acknowledgement and matching saved checkpoint, using store reads only.
                await Until(async () =>
                {
                    var accepted = (await store.GetDebugStatesAsync([id]))[id].Commands.Single(c =>
                        c.Id == command.Id && c.Order == command.Order);
                    if (accepted.AcknowledgedRevision is not { } revision) return false;
                    var saved = await store.GetWorkflowInstanceAsync(id);
                    if (saved == null || saved.Incarnation != receipt.Incarnation || saved.Revision < revision ||
                        saved.Status != WorkflowStatus.Paused ||
                        !WorkflowDebugRules.Checkpoint(saved).Breakpoints.SequenceEqual(breakpoints)) return false;
                    configured = saved;
                    return true;
                }, SetupPatience);
                return configured!;
            }
            Task<WorkflowExecutionResult> RemoteRecover()
            {
                // These providers represent separate processes. Do not carry the requester's
                // inline run context into the other host.
                using (ExecutionContext.SuppressFlow())
                    return Task.Run(() => other.GetRequiredService<IWorkflowClient>().RecoverAsync(id)).WaitAsync(Patience);
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
            probe.FailEdits = false; probe.BlockCommitsAfterResume = false; probe.Armed = false;
            remoteProbe.ReleaseResume.TrySetResult();
            if (remoteRun != null) await remoteRun.WaitAsync(Patience);
            foreach (var provider in new[] { requester, other, disabled })
            {
                if (stopLinks != null) await stopLinks(provider);
                await provider.GetRequiredService<WorkflowHost>().StopAsync(CancellationToken.None);
            }
        }

        ServiceProvider Services(IWorkflowStateRepository repository, string name, bool debugging)
        {
            var services = new ServiceCollection().AddLogging();
            services.AddIxIFlow(options => { options.Debugging = debugging; options.HostName = name; options.PayloadThresholdBytes = 64; });
            services.AddSingleton<IWorkflowCatalog>(catalog);
            services.AddScoped(_ => new ScopeProbe());
            services.AddScoped<IWorkflowStateRepository>(provider =>
            {
                ScopeProbe.Current.Value = provider.GetRequiredService<ScopeProbe>();
                return repository;
            });
            configure?.Invoke(services);
            services.AddSingleton(new InProcessInstanceGate());
            // No scanners, heartbeat or hosted recovery services run.
            return services.BuildServiceProvider();
        }
    }

    private static async Task Until(Func<Task<bool>> check, TimeSpan? patience = null)
    {
        var deadline = DateTime.UtcNow + (patience ?? Patience);
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
        public Func<int, Task>? BeforeDebugRead;
        public int DebugReads, Claims, AcknowledgementFailures;
        public bool Armed, FailEdits, FailAfterAcknowledgement, FailScopeAfterAcceptance, HoldAfterResume, BlockCommitsAfterResume;
        public TaskCompletionSource ResumeCommitted = Signal(), ReleaseResume = Signal(), AcknowledgementInterrupted = Signal();
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IWorkflowStateRepository.RequestDebugCommandAsync)) return Accept((string)args![0]!, (DebugCommandRequest)args[1]!);
            if (method.Name == nameof(IWorkflowStateRepository.GetDebugStatesAsync)) return DebugRead((IReadOnlyCollection<string>)args![0]!);
            if (method.Name == nameof(IWorkflowStateRepository.CommitWorkflowInstanceAsync)) return Commit((CheckpointCommit)args![0]!);
            if (method.Name == nameof(IWorkflowStateRepository.TryAcquireExecutionLeaseAsync)) Interlocked.Increment(ref Claims);
            return Forward(method, Store, args);
        }
        private async Task<DebugCommandResult> Accept(string id, DebugCommandRequest request)
        {
            var result = await Store.RequestDebugCommandAsync(id, request);
            if (result.Status is DebugCommandStatus.Accepted or DebugCommandStatus.AlreadyAccepted)
            {
                if (FailScopeAfterAcceptance) { FailScopeAfterAcceptance = false; ScopeProbe.Current.Value!.Fail = true; }
                if (OnAccepted != null) await OnAccepted(request, result);
            }
            return result;
        }
        private async Task<IReadOnlyDictionary<string, WorkflowDebugState>> DebugRead(IReadOnlyCollection<string> ids)
        {
            if (Armed && BeforeDebugRead != null) await BeforeDebugRead(Interlocked.Increment(ref DebugReads));
            return await Store.GetDebugStatesAsync(ids);
        }
        private async Task<WorkflowCommitResult> Commit(CheckpointCommit commit)
        {
            var trace = commit.ReadTraceEntries();
            if (FailEdits && trace.Any(e => e.EntryType == TraceEntryType.DataEdited)) throw new IOException("Interrupted edit commit");
            if (BlockCommitsAfterResume) throw new IOException("Interrupted execution after resume acknowledgement");
            var result = await Store.CommitWorkflowInstanceAsync(commit);
            if (result.Status != WorkflowCommitStatus.Applied) return result;
            if (HoldAfterResume && trace.Any(e => e.EntryType == TraceEntryType.Continued))
            {
                ResumeCommitted.TrySetResult();
                await ReleaseResume.Task.WaitAsync(Patience);
            }
            if (FailAfterAcknowledgement && trace.Any(e => e.EntryType is TraceEntryType.Continued or
                TraceEntryType.DataEdited or TraceEntryType.Paused or TraceEntryType.BreakpointsChanged))
            {
                FailAfterAcknowledgement = false;
                if (trace.Any(e => e.EntryType == TraceEntryType.Continued)) BlockCommitsAfterResume = true;
                else ScopeProbe.Current.Value!.Fail = true;
                Interlocked.Increment(ref AcknowledgementFailures);
                AcknowledgementInterrupted.TrySetResult();
                throw new IOException("Interrupted after durable command acknowledgement");
            }
            return result;
        }
    }
    private sealed class ScopeProbe : IDisposable
    {
        public static readonly AsyncLocal<ScopeProbe?> Current = new();
        public bool Fail;
        public void Dispose()
        {
            if (Fail) throw new IOException("Interrupted request scope after breakpoint acceptance");
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
    public sealed class Go;
}
