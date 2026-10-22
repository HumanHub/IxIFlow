using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Infrastructure;

public static class DebugCommandWakeRound4Scenario
{
    public static readonly string[] Cases = [
        "demand-success", "demand-failure", "demand-step", "demand-pending",
        "edit-lease-terminated", "edit-lease-pending", "direct-edit-refusal",
        "edit-receipt", "edit-committed-under-lease", "edit-settled-under-lease",
        "edit-revision-refusal", "edit-generation-refusal", "edit-structure-refusal",
        "edit-cancellation-refusal", "edit-patch-refusal", "edit-accept-ownership", "edit-commit-interrupted",
        "edit-size-refusal", "edit-continuation-refusal", "edit-frame-refusal", "edit-accepted-result", "edit-refused-committed",
        "termination", "structure", "definition", "operator-stop", "unstructured",
        "changed-under-lease", "settled-under-lease", "quarantine", "quarantine-pending",
        "assignment-ownership", "read-exception", "slot-cancelled", "gate-cancelled", "scope-exception",
        "pause-lease-terminated", "pause-changed", "pause-command-order", "pause-generation", "pause-cancellation", "pause-structure"];
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(12);

    public static async Task VerifyAsync(IWorkflowStateRepository store, IWorkflowStateRepository otherStore, string scenario,
        Action<IServiceCollection>? configure = null, Func<IServiceProvider, Task>? startLinks = null,
        Func<IServiceProvider, Task>? stopLinks = null)
    {
        var wrapped = DispatchProxy.Create<IWorkflowStateRepository, Probe>();
        var probe = (Probe)wrapped; probe.Store = store;
        var remote = DispatchProxy.Create<IWorkflowStateRepository, Probe>();
        ((Probe)remote).Store = otherStore;
        using var catalog = new WorkflowCatalog();
        var builder = new WorkflowBuilder<Data, object>([], "DebugWakeRound4")
            .Step<Compute>(setup => setup.Input(a => a.Fail).From(c => scenario == "demand-failure"));
        var pausing = scenario.StartsWith("pause-");
        var definition = scenario == "demand-step" || pausing ? builder.WaitFor<Go>("end").Build() : builder.Build();
        if (scenario.StartsWith("demand-"))
            definition.Demands.Add(new("gpu", "cuda", DateTime.UtcNow.AddDays(1)));
        await catalog.RegisterWorkflowAsync(definition);
        var localCatalog = DispatchProxy.Create<IWorkflowCatalog, CatalogProbe>();
        var catalogProbe = (CatalogProbe)localCatalog; catalogProbe.Catalog = catalog;
        await using var requester = Services(wrapped, localCatalog, "requester");
        await using var other = Services(remote, catalog, "other");
        try
        {
            if (startLinks != null) { await startLinks(requester); await startLinks(other); }
            var id = Guid.NewGuid().ToString("N");
            var remoteOps = other.GetRequiredService<IWorkflowOperations>();
            await remoteOps.LaunchAsync(id, definition.Name, definition.Version,
                JsonSerializer.SerializeToElement(new Data()), !pausing, "launch");
            var setupDeadline = DateTime.UtcNow + Patience;
            while ((await store.GetWorkflowInstanceAsync(id))!.Status != (pausing ? WorkflowStatus.Suspended : WorkflowStatus.Paused))
            {
                Assert.True(DateTime.UtcNow < setupDeadline, "Launch did not reach its setup checkpoint");
                await Task.Delay(10);
            }
            var initial = (await store.GetWorkflowInstanceAsync(id))!;
            Assert.Equal(pausing ? WorkflowStatus.Suspended : WorkflowStatus.Paused, initial.Status);
            var generation = WorkflowDebugRules.Checkpoint(initial).PauseGeneration;
            var continuation = Assert.Single((await other.GetRequiredService<IWorkflowClient>().GetSnapshotAsync(id))!.Pointers).PointerId;
            // Warm placement independently of the definition lookup intercepted below.
            await StoreTestSupport.ClaimAsync(requester);
            var edit = new DataEditRequest(initial.Revision, new(generation),
                [new([nameof(Data.Value)], JsonSerializer.SerializeToElement(99))]);
            if (scenario == "edit-patch-refusal")
                edit = edit with { Patches = [new(["MissingProperty"], JsonSerializer.SerializeToElement(99))] };
            if (scenario == "edit-size-refusal")
                edit = edit with { Patches = [new([nameof(Data.Padding)], JsonSerializer.SerializeToElement(new string('x', 10_000)))] };
            if (scenario == "edit-continuation-refusal") edit = edit with { Target = edit.Target with { ContinuationId = "gone" } };
            if (scenario == "edit-frame-refusal") edit = edit with { Target = edit.Target with { FrameId = "gone" } };
            var editing = scenario.Contains("edit-");
            if (editing)
                Assert.Equal(DebugCommandStatus.Accepted, (await store.RequestDebugCommandAsync(id,
                    new("edit", DebugCommandKind.EditData, generation, "editor", Edit: edit)
                    { ExpectedIncarnation = initial.Incarnation })).Status);
            if (scenario == "pause-command-order")
                Assert.Equal(DebugCommandStatus.Accepted, (await store.RequestDebugCommandAsync(id,
                    new("breakpoints", DebugCommandKind.SetBreakpoints, generation, "setup", Breakpoints: [])
                    { ExpectedIncarnation = initial.Incarnation })).Status);
            var kind = pausing ? DebugCommandKind.Pause : scenario == "demand-step" ? DebugCommandKind.Step : DebugCommandKind.Continue;
            var publicCommand = pausing || scenario.StartsWith("demand-") || scenario == "edit-lease-terminated";
            DebugCommandReceipt? receipt = null;
            if (publicCommand)
                probe.OnAccepted = command => receipt = new(id, initial.Incarnation, command.Id, command.Order, command.Kind);
            else
            {
                var accepted = await store.RequestDebugCommandAsync(id,
                    new("resume", kind, generation, "operator") { ExpectedIncarnation = initial.Incarnation });
                Assert.Equal(DebugCommandStatus.Accepted, accepted.Status);
                receipt = new(id, initial.Incarnation, accepted.Command!.Id, accepted.Command.Order, kind);
            }
            WorkflowExecutionResult? settled = null;
            using var cancellation = new CancellationTokenSource();
            var hits = 0;
            if (pausing)
            {
                if (scenario == "pause-lease-terminated") probe.BeforeAcquire = async () => { hits++; await Terminate(); };
                else probe.AfterAcquire = async () =>
                {
                    if (scenario == "pause-changed") { hits++; await Release(); await Terminate(); }
                    else if (scenario is "pause-command-order" or "pause-generation")
                    {
                        if (scenario == "pause-generation") probe.AfterRead = current =>
                        {
                            var state = JsonNode.Parse(current.ExecutionStateJson)!;
                            state["Debug"]!["PauseGeneration"] = generation + 1;
                            current.ExecutionStateJson = state.ToJsonString();
                        };
                        probe.AfterDebugRead = async () => { hits++; await Release(); settled = await RemoteRecover(); };
                    }
                    else if (scenario == "pause-cancellation")
                        probe.BeforeFirstControls = async () => { hits++; await Release(); await Terminate(); };
                    else catalogProbe.BeforeAcquireDefinition = async () =>
                    {
                        hits++; await Release(); settled = await RemoteRecover();
                        return new WorkflowDefinitionLease(DifferentDefinition(definition.Name));
                    };
                };
            }
            else if (scenario.StartsWith("demand-"))
                probe.BeforeDemand = async () =>
                {
                    hits++;
                    Assert.Null((await store.GetDebugStatesAsync([id]))[id].Commands.Single(c => c.Id == receipt!.CommandId).AcknowledgedRevision);
                    if (scenario != "demand-pending") settled = await RemoteRecover();
                };
            else if (scenario is "edit-lease-terminated" or "edit-lease-pending" or "direct-edit-refusal")
            {
                if (scenario == "edit-lease-terminated")
                    probe.BeforeAcquire = async () =>
                    {
                        hits++;
                        await Remote(() => remoteOps.TerminateAsync(id, "round4"));
                        Assert.Equal(WorkflowStatus.Terminated, (await store.GetWorkflowInstanceAsync(id))!.Status);
                        Assert.Null((await store.GetDebugStatesAsync([id]))[id].Commands.Single(c => c.Id == "edit").EditResult);
                    };
                else
                {
                    Assert.NotNull(await otherStore.TryAcquireExecutionLeaseAsync(id, "held", TimeSpan.FromMinutes(1),
                        await StoreTestSupport.ClaimAsync(other), ExecutionClaimKind.Operator));
                    probe.BeforeAcquire = () => { hits++; return Task.CompletedTask; };
                }
            }
            else if (editing)
            {
                if (scenario == "edit-receipt")
                    probe.BeforeSummary = async () => { hits++; await RemoteRecover(); settled = await RemoteRecover(); };
                else if (scenario == "edit-committed-under-lease")
                    probe.BeforeAcquire = async () => { hits++; settled = await RemoteRecover(); };
                else if (scenario == "edit-refused-committed")
                    probe.BeforeAcquire = async () =>
                    {
                        hits++; settled = await RemoteRecover();
                        Assert.NotNull(await otherStore.TryAcquireExecutionLeaseAsync(id, "held", TimeSpan.FromMinutes(1),
                            await StoreTestSupport.ClaimAsync(other), ExecutionClaimKind.Operator));
                    };
                else if (scenario == "edit-settled-under-lease")
                    probe.AfterAcquire = async () => { hits++; await Release(); await RemoteRecover(); settled = await RemoteRecover(); };
                else if (scenario == "edit-accept-ownership")
                    probe.BeforeEditAccept = async () => { hits++; await Release(); await Terminate(); };
                else if (scenario == "edit-accepted-result")
                    probe.BeforeEditAccept = async () => { hits++; await Release(); settled = await RemoteRecover(); };
                else if (scenario == "edit-commit-interrupted")
                    probe.BeforeCommit = async _ =>
                    { hits++; await Release(); await Terminate(); throw new CheckpointInterruptedException(new IOException("Edit commit interrupted")); };
                else
                {
                    // Settle only after the refused attempt has released its real lease.
                    // The guard must replace each inner exception with that saved termination.
                    probe.AfterRelease = async () => { hits++; await Terminate(); };
                    if (scenario == "edit-cancellation-refusal")
                        probe.CancellationOnThirdRead = true;
                    else if (scenario == "edit-structure-refusal")
                        probe.AfterFullRead = (read, _) =>
                        {
                            if (read == 4) catalogProbe.BeforeAcquireDefinition = () => Task.FromResult(
                                new WorkflowDefinitionLease(DifferentDefinition(definition.Name)));
                        };
                    else if (scenario is not ("edit-patch-refusal" or "edit-size-refusal" or "edit-continuation-refusal" or "edit-frame-refusal"))
                        probe.AfterFullRead = (read, current) =>
                        {
                            if (read != 4) return;
                            if (scenario == "edit-revision-refusal") current.Revision++;
                            else
                            {
                                var state = JsonNode.Parse(current.ExecutionStateJson)!;
                                state["Debug"]!["PauseGeneration"] = generation + 1;
                                current.ExecutionStateJson = state.ToJsonString();
                            }
                        };
                }
            }
            else if (scenario == "termination")
            {
                Assert.True((await otherStore.RequestTerminationAsync(id, new()
                { Mode = WorkflowControlMode.Terminate, ReasonCode = "round4" }, initial.Incarnation)).Accepted);
                hits++;
            }
            else if (scenario is "structure" or "definition")
                catalogProbe.BeforeDefinition = async () =>
                {
                    if (scenario == "structure") { hits++; settled = await RemoteRecover(); }
                    else probe.BeforeRecoveryControls = () =>
                    {
                        probe.AfterDebugRead = async () => { hits++; settled = await RemoteRecover(); };
                        return Task.CompletedTask;
                    };
                    return scenario == "definition" ? null : DifferentDefinition(definition.Name);
                };
            else if (scenario is "operator-stop" or "unstructured")
            {
                probe.AfterRead = instance =>
                {
                    if (scenario == "unstructured") instance.ExecutionStateJson = "{}";
                    else
                    {
                        var state = JsonNode.Parse(instance.ExecutionStateJson)!;
                        state["OperatorStop"] = JsonSerializer.SerializeToNode(new OperatorStopState
                        { Kind = OperatorStopKind.Quarantine, Reason = "stale stop", StoppedAtUtc = DateTime.UtcNow });
                        instance.ExecutionStateJson = state.ToJsonString();
                    }
                };
                probe.BeforeRecoveryControls = () =>
                {
                    probe.AfterDebugRead = async () => { hits++; settled = await RemoteRecover(); };
                    return Task.CompletedTask;
                };
            }
            else if (scenario is "changed-under-lease" or "settled-under-lease")
            {
                probe.AfterAcquire = async () =>
                {
                    hits++;
                    if (scenario == "changed-under-lease")
                    {
                        // A sees a changed revision, then an older pending-command response.
                        // B commits after that response was captured but before it reaches A.
                        probe.AfterSummary = current => current.Revision++;
                        probe.AfterDebugRead = async () => { await Release(); settled = await RemoteRecover(); };
                    }
                    else { await Release(); settled = await RemoteRecover(); }
                };
            }
            else if (scenario.StartsWith("quarantine") || scenario == "assignment-ownership")
            {
                probe.Quarantine = scenario.StartsWith("quarantine");
                probe.BeforeCommit = async commit =>
                {
                    if (scenario.StartsWith("quarantine"))
                        Assert.Contains("OperatorStop", commit.ReadInstance(await store.GetWorkflowInstanceAsync(id)).ExecutionStateJson);
                    hits++;
                    if (scenario == "quarantine-pending") return;
                    await Release();
                    settled = await RemoteRecover();
                };
            }
            else if (scenario == "read-exception")
                probe.BeforeRecoveryControls = async () =>
                { hits++; settled = await RemoteRecover(); throw new IOException("Recovery control read failed"); };
            else if (scenario == "slot-cancelled")
            {
                settled = await RemoteRecover(); cancellation.Cancel(); hits++;
            }
            else if (scenario == "gate-cancelled")
                probe.BeforeFirstControls = async () => { hits++; settled = await RemoteRecover(); cancellation.Cancel(); };
            else if (scenario == "scope-exception") probe.FailScope = true;
            else throw new ArgumentOutOfRangeException(nameof(scenario));

            WorkflowExecutionResult? result = null;
            Exception? attemptFailure = null;
            if (scenario == "direct-edit-refusal")
            {
                var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => requester.GetRequiredService<IWorkflowOperations>()
                    .EditDataAsync(id, "edit", edit.ExpectedRevision, edit.Target, edit.Patches, "editor"));
                Assert.Contains("owned by another host", error.Message);
            }
            else
            {
                // This is the background recovery entry, enclosing the actual slot and scoped
                // recovery body in the same guard used by the public Continue/Step operations.
                var host = requester.GetRequiredService<WorkflowHost>();
                Task<WorkflowExecutionResult> Recover() => publicCommand
                    ? kind == DebugCommandKind.Pause
                        ? requester.GetRequiredService<IWorkflowOperations>().PauseAsync(id, "operator")
                        : kind == DebugCommandKind.Step
                        ? requester.GetRequiredService<IWorkflowOperations>().StepAsync(id, continuation, DebugStepKind.Over, "operator")
                        : requester.GetRequiredService<IWorkflowOperations>().ContinueAsync(id, "operator")
                    : host.WithDebugCommandResultAsync(() => receipt, async () =>
                    {
                        try { return await host.RunAsync(id, (slot, _, token) => slot.RecoverDebugCommandAsync(receipt!, token), cancellation.Token); }
                        catch (Exception error) { attemptFailure = error; throw; }
                    });
                if (scenario == "edit-lease-pending")
                {
                    var error = await Assert.ThrowsAsync<WorkflowEngineException>(Recover);
                    Assert.Contains("owned by another host", error.Message);
                    Assert.Null((await store.GetDebugStatesAsync([id]))[id].Commands.Single(c => c.Id == "resume").AcknowledgedRevision);
                    await otherStore.ReleaseExecutionLeaseAsync(id, "held");
                }
                else result = await Recover();
                if (scenario == "demand-pending") Assert.Equal(WorkflowExecutionStatus.Scheduled, result.Status);
                else if (scenario == "edit-lease-pending") { }
                else if (scenario == "quarantine-pending")
                {
                    Assert.Equal(WorkflowExecutionStatus.NeedsResolution, result.Status);
                    Assert.Contains("quarantined", result.ErrorMessage);
                    Assert.Null((await store.GetDebugStatesAsync([id]))[id].Commands.Single(c => c.Id == "resume").AcknowledgedRevision);
                }
                else if (scenario is "termination" or "edit-lease-terminated" or "edit-accept-ownership" or "pause-lease-terminated" or "pause-changed" or "pause-cancellation" || scenario.StartsWith("edit-") && scenario.EndsWith("refusal") || scenario == "edit-commit-interrupted")
                {
                    Assert.Equal(WorkflowExecutionStatus.Terminated, result.Status);
                    Assert.IsType<Data>(result.WorkflowData); // The synthetic termination result has no data.
                }
                else
                {
                    if (scenario == "scope-exception") settled = result;
                    Assert.NotNull(settled);
                    Assert.Equal(settled.Status, result.Status);
                    Assert.Equal(settled.ErrorKind, result.ErrorKind);
                    Assert.Equal(settled.ErrorMessage, result.ErrorMessage);
                    if (scenario == "demand-failure") Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
                    else
                    {
                        var earlierEditOnly = scenario is "edit-committed-under-lease" or "edit-accepted-result" or "edit-refused-committed";
                        Assert.Equal(scenario == "demand-step" || earlierEditOnly || pausing ? WorkflowExecutionStatus.Paused : WorkflowExecutionStatus.Success, result.Status);
                        Assert.Equal(earlierEditOnly ? 99 : 42, Assert.IsType<Data>(result.WorkflowData).Value);
                    }
                }
            }
            Assert.Equal(scenario == "scope-exception" ? 0 : 1, hits);
            if (scenario == "scope-exception") Assert.Equal(1, probe.ScopeFailures);
            var expectedRefusal = scenario switch
            {
                "edit-revision-refusal" => "expected paused revision",
                "edit-generation-refusal" => "pause generation changed",
                "edit-structure-refusal" => "Workflow structure differs",
                "edit-cancellation-refusal" => "Cancellation is pending",
                "edit-patch-refusal" => "data contract refused",
                "edit-continuation-refusal" or "edit-frame-refusal" => "data contract refused",
                "edit-size-refusal" => "edited checkpoint exceeds the size limit",
                "edit-commit-interrupted" => "edit was not acknowledged",
                _ => null
            };
            if (expectedRefusal != null)
                Assert.True(attemptFailure is WorkflowEngineException && attemptFailure.Message.Contains(expectedRefusal), attemptFailure?.ToString());
            if (scenario is "structure" or "definition" or "operator-stop" or "unstructured") Assert.Equal(0, probe.Claims);
            if (scenario.StartsWith("demand-")) Assert.Equal(0, probe.Claims);
            if (scenario is "edit-lease-terminated" or "edit-lease-pending" or "direct-edit-refusal") Assert.Equal(1, probe.Refusals);
            if (scenario == "demand-pending")
            {
                Assert.Null((await store.GetDebugStatesAsync([id]))[id].Commands.Single(c => c.Id == receipt!.CommandId).AcknowledgedRevision);
                Assert.Equal(WorkflowExecutionStatus.Scheduled, (await requester.GetRequiredService<IWorkflowClient>().RecoverAsync(id)).Status);
            }
            if (scenario is "direct-edit-refusal" or "edit-refused-committed") await otherStore.ReleaseExecutionLeaseAsync(id, "held");
            foreach (var command in (await store.GetDebugStatesAsync([id]))[id].Commands.Where(c =>
                c.AcknowledgedRevision != null && c.Kind is DebugCommandKind.Continue or DebugCommandKind.Step))
                Assert.Single(await store.GetTraceAsync(id), entry => entry.Entry.EntryType == TraceEntryType.Continued &&
                    entry.Entry.Payload is DebugCommandTrace trace && trace.CommandId == command.Id);

            async Task Release() => Assert.True((await store.ReleaseExecutionLeaseAsync(id, probe.OwnerToken!)).Released);
            Task<WorkflowExecutionResult> RemoteRecover() => Remote(() => other.GetRequiredService<IWorkflowClient>().RecoverAsync(id));
            Task<InstanceSummary> Terminate() => Remote(() => remoteOps.TerminateAsync(id, "round4-edit"));
        }
        finally
        {
            probe.BeforeDemand = null; probe.BeforeAcquire = null; probe.AfterAcquire = null; probe.BeforeCommit = null;
            probe.BeforeFirstControls = null; probe.BeforeRecoveryControls = null; probe.FailScope = false;
            foreach (var provider in new[] { requester, other })
            {
                if (stopLinks != null) await stopLinks(provider);
                await provider.GetRequiredService<WorkflowHost>().StopAsync(CancellationToken.None);
            }
        }

        ServiceProvider Services(IWorkflowStateRepository repository, IWorkflowCatalog definitions, string name)
        {
            var services = new ServiceCollection().AddLogging();
            services.AddIxIFlow(options =>
            {
                options.Debugging = true; options.HostName = name;
                if (name == "other") options.Capabilities["gpu"] = "cuda";
                if (name == "requester" && scenario == "edit-size-refusal")
                { options.MaxCheckpointBytes = 4096; options.PayloadThresholdBytes = 0; }
            });
            services.AddSingleton(definitions);
            services.AddScoped(_ => new ScopeProbe(probe));
            services.AddScoped<IWorkflowStateRepository>(provider =>
            {
                if (name == "requester" && scenario == "scope-exception")
                {
                    var scoped = DispatchProxy.Create<IWorkflowStateRepository, ScopeRepository>();
                    var scopeProbe = (ScopeRepository)scoped;
                    scopeProbe.Store = repository; scopeProbe.Scope = provider.GetRequiredService<ScopeProbe>(); scopeProbe.Owner = probe;
                    return scoped;
                }
                return repository;
            });
            configure?.Invoke(services);
            services.AddSingleton(new InProcessInstanceGate());
            return services.BuildServiceProvider();
        }
    }

    private static Task<T> Remote<T>(Func<Task<T>> action)
    {
        using (ExecutionContext.SuppressFlow()) return Task.Run(action).WaitAsync(Patience);
    }
    private static WorkflowDefinition DifferentDefinition(string name) =>
        new WorkflowBuilder<Data, object>([], name).Step<Compute>().WaitFor<Go>("different").Build();

    public class Probe : DispatchProxy
    {
        public IWorkflowStateRepository Store = null!;
        public Action<DebugCommand>? OnAccepted;
        public Func<Task>? BeforeDemand, BeforeAcquire, AfterAcquire, BeforeFirstControls, BeforeRecoveryControls, AfterDebugRead;
        public Func<Task>? BeforeSummary, AfterRelease, BeforeEditAccept;
        public Func<CheckpointCommit, Task>? BeforeCommit;
        public Action<WorkflowInstance>? AfterRead, AfterSummary;
        public Action<int, WorkflowInstance>? AfterFullRead;
        public int Claims, Refusals, ScopeFailures;
        public bool Quarantine, FailScope, CancellationOnThirdRead;
        public string? OwnerToken;
        private int _controls, _reads;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IWorkflowStateRepository.GetPendingEventsAsync) && BeforeDemand != null) return Demand(method, args!);
            if (method.Name == nameof(IWorkflowStateRepository.RequestDebugCommandAsync)) return Accept((string)args![0]!, (DebugCommandRequest)args[1]!);
            if (method.Name == nameof(IWorkflowStateRepository.TryAcquireExecutionLeaseAsync)) return Acquire(method, args!);
            if (method.Name == nameof(IWorkflowStateRepository.GetWorkflowInstanceAsync)) return Read((string)args![0]!, false);
            if (method.Name == nameof(IWorkflowStateRepository.GetWorkflowInstanceSummaryAsync)) return Read((string)args![0]!, true);
            if (method.Name == nameof(IWorkflowStateRepository.GetCancellationRequestAsync)) return Controls((string)args![0]!);
            if (method.Name == nameof(IWorkflowStateRepository.GetDebugStatesAsync) && AfterDebugRead != null) return DebugRead((IReadOnlyCollection<string>)args![0]!);
            if (method.Name == nameof(IWorkflowStateRepository.CommitWorkflowInstanceAsync)) return Commit((CheckpointCommit)args![0]!);
            if (method.Name == nameof(IWorkflowStateRepository.ReleaseExecutionLeaseAsync)) return Release(method, args!);
            return Forward(method, Store, args);
        }
        private async Task<DebugCommandResult> Accept(string id, DebugCommandRequest request)
        {
            if (request.Kind == DebugCommandKind.EditData && Interlocked.Exchange(ref BeforeEditAccept, null) is { } before) await before();
            var accepted = await Store.RequestDebugCommandAsync(id, request);
            if (accepted.Status is DebugCommandStatus.Accepted or DebugCommandStatus.AlreadyAccepted) OnAccepted?.Invoke(accepted.Command!);
            return accepted;
        }
        private async Task<InboxPage> Demand(MethodInfo method, object?[] args)
        {
            var before = Interlocked.Exchange(ref BeforeDemand, null);
            if (before != null) await before();
            return await (Task<InboxPage>)Forward(method, Store, args)!;
        }
        private async Task<WorkflowInstance?> Read(string id, bool summary)
        {
            if (summary && Interlocked.Exchange(ref BeforeSummary, null) is { } before) await before();
            var saved = summary ? await Store.GetWorkflowInstanceSummaryAsync(id) : await Store.GetWorkflowInstanceAsync(id);
            var after = summary ? Interlocked.Exchange(ref AfterSummary, null) : Interlocked.Exchange(ref AfterRead, null);
            if (saved != null) after?.Invoke(saved);
            if (!summary && saved != null) AfterFullRead?.Invoke(Interlocked.Increment(ref _reads), saved);
            return saved;
        }
        private async Task<CancellationReason?> Controls(string id)
        {
            var read = Interlocked.Increment(ref _controls);
            var before = read == 1 ? Interlocked.Exchange(ref BeforeFirstControls, null)
                : Interlocked.Exchange(ref BeforeRecoveryControls, null);
            if (before != null) await before();
            if (read == 3 && CancellationOnThirdRead) return new() { Mode = WorkflowControlMode.Cancel, ReasonCode = "pending" };
            return await Store.GetCancellationRequestAsync(id);
        }
        private async Task<IReadOnlyDictionary<string, WorkflowDebugState>> DebugRead(IReadOnlyCollection<string> ids)
        {
            var state = await Store.GetDebugStatesAsync(ids);
            var after = Interlocked.Exchange(ref AfterDebugRead, null);
            if (after != null) await after();
            return state;
        }
        private async Task<ExecutionLeaseGrant?> Acquire(MethodInfo method, object?[] args)
        {
            Claims++;
            OwnerToken = (string)args[1]!;
            var before = Interlocked.Exchange(ref BeforeAcquire, null);
            if (before != null) await before();
            var grant = await (Task<ExecutionLeaseGrant?>)Forward(method, Store, args)!;
            if (grant == null) Refusals++;
            var after = Interlocked.Exchange(ref AfterAcquire, null);
            if (after != null) await after();
            return Quarantine && grant != null ? grant with { CrashCount = int.MaxValue } : grant;
        }
        private async Task<WorkflowCommitResult> Commit(CheckpointCommit commit)
        {
            var before = Interlocked.Exchange(ref BeforeCommit, null);
            if (before != null) await before(commit);
            var result = await Store.CommitWorkflowInstanceAsync(commit);
            return result;
        }
        private async Task<ExecutionLeaseReleaseResult> Release(MethodInfo method, object?[] args)
        {
            var released = await (Task<ExecutionLeaseReleaseResult>)Forward(method, Store, args)!;
            if (released.Released && Interlocked.Exchange(ref AfterRelease, null) is { } after) await after();
            return released;
        }
    }
    public sealed class ScopeProbe(Probe owner) : IDisposable
    {
        public bool Fail;
        public void Dispose()
        {
            if (Fail) { owner.ScopeFailures++; throw new IOException("Recovery scope disposal failed after commit"); }
        }
    }
    public class ScopeRepository : DispatchProxy
    {
        public IWorkflowStateRepository Store = null!;
        public ScopeProbe Scope = null!;
        public Probe Owner = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method!.Name == nameof(IWorkflowStateRepository.CommitWorkflowInstanceAsync)
                ? Commit((CheckpointCommit)args![0]!) : Forward(method, Store, args);
        private async Task<WorkflowCommitResult> Commit(CheckpointCommit commit)
        {
            var result = await Store.CommitWorkflowInstanceAsync(commit);
            if (Owner.FailScope && result.Status == WorkflowCommitStatus.Applied && commit.Instance.Status == WorkflowStatus.Completed)
            { Owner.FailScope = false; Scope.Fail = true; }
            return result;
        }
    }
    public class CatalogProbe : DispatchProxy
    {
        public IWorkflowCatalog Catalog = null!;
        public Func<Task<WorkflowDefinition?>>? BeforeDefinition;
        public Func<Task<WorkflowDefinitionLease>>? BeforeAcquireDefinition;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IWorkflowCatalog.GetWorkflowDefinitionAsync) &&
                Interlocked.Exchange(ref BeforeDefinition, null) is { } before) return before();
            if (method.Name == nameof(IWorkflowDefinitionSource.AcquireWorkflowDefinitionAsync) &&
                Interlocked.Exchange(ref BeforeAcquireDefinition, null) is { } acquire) return acquire();
            return Forward(method, Catalog, args);
        }
    }
    private static object? Forward(MethodInfo method, object target, object?[]? args)
    {
        try { return method.Invoke(target, args); }
        catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException!).Throw(); throw; }
    }
    public sealed class Data { public int Value { get; set; } public int Zero { get; set; } public string Padding { get; set; } = ""; }
    public sealed class Go;
    public sealed class Compute : IAsyncActivity
    {
        public bool Fail { get; set; }
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken)
        {
            var data = (Data)context.WorkflowData;
            data.Value = Fail ? 100 / data.Zero : 42;
            return Task.CompletedTask;
        }
    }
}
