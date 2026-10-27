using IxIFlow.Core;
using IxIFlow.Builders;
using IxIFlow.Debugging;
using IxIFlow.Extensions;
using IxIFlow.Recipes;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Net;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "scopes"), Trait("Spec", "F-26"), Trait("Spec", "G-06")]
public sealed class ScopeDebuggerScenarios
{
    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task DapScopesVariablesUseRedactedSavedValuesOnEveryStore(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        await using var host = ScopeDebuggerFixture.Services(store.NewRepository(), new WorkflowCatalog(), new(), store.ConfigureHost);
        await store.StartHostLinksAsync(host);
        var client = host.GetRequiredService<IWorkflowClient>(); var ops = host.GetRequiredService<IWorkflowOperations>();
        var idle = await client.RunAsync(ScopeDebuggerFixture.Definition(), new ScopeDebuggerFixture.Data());
        await ops.SetBreakpointsAsync(idle.InstanceId, [new("body", "read")], "setup");
        Assert.Equal(WorkflowExecutionStatus.Paused, (await client.ResumeAsync(idle.InstanceId, "start", new ScopeDebuggerFixture.Signal())).Status);
        var inspection = (await ops.InspectAsync(idle.InstanceId))!;
        var scope = Assert.Single(inspection.Scopes);
        var options = new DebuggingOptions(); options.Tcp(IPAddress.Loopback, 0, InlineDebugProtocolClient.Token, "scope-inspector");
        options.Authorize = (_, _) => ValueTask.FromResult(true);
        var session = new DebugAdapterSession(ops, options);
        await using var dap = new InlineDebugProtocolClient((input, output) => session.RunAsync(input, output), session);
        await dap.Ok("initialize"); await dap.Ok("attach", new { instanceId = idle.InstanceId, sessionToken = InlineDebugProtocolClient.Token });
        await dap.Ok("configurationDone");
        var threadId = (await dap.Ok("threads")).GetProperty("body").GetProperty("threads").EnumerateArray().Single().GetProperty("id").GetInt32();
        var frameId = (await dap.Ok("stackTrace", new { threadId })).GetProperty("body").GetProperty("stackFrames")[0].GetProperty("id").GetInt32();
        var scopes = (await dap.Ok("scopes", new { frameId })).GetProperty("body").GetProperty("scopes");
        var reference = scopes.EnumerateArray().Single(s => s.GetProperty("name").GetString() == "Scopes").GetProperty("variablesReference").GetInt32();
        var activation = (await dap.Ok("variables", new { variablesReference = reference })).GetProperty("body").GetProperty("variables").EnumerateArray().Single();
        Assert.Equal(scope.ActivationId, activation.GetProperty("name").GetString());
        var fields = (await dap.Ok("variables", new { variablesReference = activation.GetProperty("variablesReference").GetInt32() })).GetProperty("body").GetProperty("variables");
        Assert.Contains(fields.EnumerateArray(), f => f.GetProperty("name").GetString() == "Threading" && f.GetProperty("value").GetString() == "\"ThreadAffine\"");
        var outputReference = fields.EnumerateArray().Single(f => f.GetProperty("name").GetString() == "Outputs").GetProperty("variablesReference").GetInt32();
        var outputs = (await dap.Ok("variables", new { variablesReference = outputReference })).GetProperty("body").GetProperty("variables");
        Assert.Contains(outputs.EnumerateArray(), f => f.GetProperty("name").GetString() == "Opened" && f.GetProperty("value").GetString() == "\"outer\"");
        Assert.DoesNotContain("scope-secret", outputs.GetRawText()); Assert.DoesNotContain("Disposed", outputs.GetRawText());
        Assert.Equal(inspection.Revision, (await ops.InspectAsync(idle.InstanceId))!.Revision);
        await dap.Ok("disconnect");
        await ops.SetBreakpointsAsync(idle.InstanceId, [], "clear");
        Assert.Equal(WorkflowExecutionStatus.Success, (await ops.ContinueAsync(idle.InstanceId, "finish")).Status);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task AffineScopeSteppingUsesTheSameFramesAndReopenRulesOnEveryStore(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        await ScopeDebuggerFixture.IntoOverOut(store.NewRepository(), new WorkflowCatalog(), new(), store.ConfigureHost,
            host => store.StartHostLinksAsync(host));
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ScopedApprovalRecipeReopensAfterWaitAndItsIndependentChildInheritsNoSession(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var probe = new ScopedApproval.Probe(); var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow(); services.AddSingleton(store.NewRepository()); services.AddSingleton(probe);
        store.ConfigureHost(services);
        await using var host = services.BuildServiceProvider();
        await store.StartHostLinksAsync(host);
        await host.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(ScopedApproval.Child());
        var client = host.GetRequiredService<IWorkflowClient>();
        var idle = await client.RunAsync(ScopedApproval.Definition(), new ScopedApproval.Data());
        Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
        Assert.Equal(1, probe.Opens); Assert.Equal(1, probe.Reads); Assert.Equal(1, probe.Disposals);
        var done = await client.ResumeAsync(idle.InstanceId, "approval", new ScopedApproval.Decision());
        if (done.Status != WorkflowExecutionStatus.Success)
            done = await client.AwaitAsync(idle.InstanceId, WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(done.Status == WorkflowExecutionStatus.Success, done.ErrorMessage);
        Assert.False(Assert.IsType<ScopedApproval.Data>(done.WorkflowData).ChildSawScope);
        Assert.Equal(2, probe.Opens); Assert.Equal(2, probe.Reads); Assert.Equal(2, probe.Disposals);
        Assert.Equal(new[] { ScopeCloseReason.Suspended, ScopeCloseReason.Completed }, probe.Closes);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task SelectedInheritedBranchReopensAHeldParentsAffineValueAtCapOne(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var probe = new ScopeDebuggerFixture.Probe();
        await using var host = ScopeDebuggerFixture.Services(store.NewRepository(), new WorkflowCatalog(), probe, store.ConfigureHost);
        await store.StartHostLinksAsync(host);
        var client = host.GetRequiredService<IWorkflowClient>(); var ops = host.GetRequiredService<IWorkflowOperations>();
        var definition = new WorkflowBuilder<ScopeDebuggerFixture.Data, object>([], "HeldParentScope")
            .WaitFor<ScopeDebuggerFixture.Signal>("start")
            .Scope<ScopeDebuggerFixture.Container>(s => s.Input(a => a.Label).From(c => "inherited"), b => b.Parallel(p => p
                .Do(x => x.Step<ScopeDebuggerFixture.Read>().WithId("selected"))
                .Do(x => x.WaitFor<ScopeDebuggerFixture.Signal>("held")))).Build();
        var idle = await client.RunAsync(definition, new ScopeDebuggerFixture.Data());
        await ops.SetBreakpointsAsync(idle.InstanceId, [new("selected", "selected")], "setup");
        Assert.Equal(WorkflowExecutionStatus.Paused, (await client.ResumeAsync(idle.InstanceId, "start", new ScopeDebuggerFixture.Signal())).Status);
        var inspection = (await ops.InspectAsync(idle.InstanceId))!;
        var selected = Assert.Single(inspection.Threads, t => t.AuthorStepId == "selected");
        Assert.Single(selected.ScopesInForce);
        Assert.Equal("Joining", inspection.Threads.Single(t => t.ContinuationId == selected.ParentContinuationId).Status);
        await ops.SetBreakpointsAsync(idle.InstanceId, [], "clear");
        Assert.Equal(WorkflowExecutionStatus.Paused, (await ops.StepAsync(idle.InstanceId, selected.ContinuationId, DebugStepKind.Over, "selected-step")).Status);
        Assert.Equal("inherited", Assert.Single(probe.Labels)); Assert.Equal(2, probe.Threads.Distinct().Count());
        Assert.Equal(WorkflowExecutionStatus.Suspended, (await ops.ContinueAsync(idle.InstanceId, "continue")).Status);
        Assert.Equal(WorkflowExecutionStatus.Success, (await client.ResumeAsync(idle.InstanceId, "held", new ScopeDebuggerFixture.Signal())).Status);
        Assert.Equal(2, probe.Threads.Count);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task PauseDrainsTheAsyncAffinePumpAndContinueUsesANewResource(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var probe = new ScopeDebuggerFixture.Probe(); var repository = store.NewRepository();
        var observed = DispatchProxy.Create<IWorkflowStateRepository, PauseObservation>();
        var observer = (PauseObservation)(object)observed; observer.Store = repository;
        await using var host = ScopeDebuggerFixture.Services(observed, new WorkflowCatalog(), probe, store.ConfigureHost);
        await store.StartHostLinksAsync(host);
        var client = host.GetRequiredService<IWorkflowClient>(); var ops = host.GetRequiredService<IWorkflowOperations>();
        var definition = new WorkflowBuilder<ScopeDebuggerFixture.Data, object>([], "AsyncScopePause")
            .Scope<ScopeDebuggerFixture.Container>(null, b => b.Step<ScopeDebuggerFixture.Block>().Step<ScopeDebuggerFixture.Read>()).Build();
        var id = Guid.NewGuid().ToString("N");
        var run = client.RunAsync(definition, new ScopeDebuggerFixture.Data(), new() { InstanceId = id });
        try
        {
            await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(15)); await ops.PauseAsync(id, "pause");
            Assert.Contains((await repository.GetDebugStatesAsync([id]))[id].Commands, c => c.Kind == DebugCommandKind.Pause && c.AcknowledgedRevision == null);
            await observer.Observed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(run.IsCompleted); Assert.Empty(probe.Closes);
        }
        finally { probe.Resume.TrySetResult(); }
        Assert.Equal(WorkflowExecutionStatus.Paused, (await run.WaitAsync(TimeSpan.FromSeconds(15))).Status);
        Assert.Equal(1, probe.Reads); Assert.Equal(ScopeCloseReason.Suspended, Assert.Single(probe.Closes));
        Assert.Equal(WorkflowExecutionStatus.Success, (await ops.ContinueAsync(id, "continue")).Status);
        Assert.Equal(2, probe.Reads); Assert.Equal(2, probe.Threads.Distinct().Count());
        Assert.Equal(ScopeCloseReason.Completed, probe.Closes.Last());
    }

    public class PauseObservation : DispatchProxy
    {
        public IWorkflowStateRepository Store = null!;
        private int _pendingReads;
        public TaskCompletionSource Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IWorkflowStateRepository.GetControlRequestsAsync)) return Read((IReadOnlyCollection<string>)args![0]!);
            try { return method.Invoke(Store, args); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
        private async Task<WorkflowControlRequests> Read(IReadOnlyCollection<string> ids)
        {
            var result = await Store.GetControlRequestsAsync(ids);
            var (_, debug) = result;
            // A second owner poll proves the previous result reached its control subscriber.
            if (debug.Values.Any(s => s.Commands.Any(c => c.Kind == DebugCommandKind.Pause && c.AcknowledgedRevision == null)) &&
                Interlocked.Increment(ref _pendingReads) >= 2) Observed.TrySetResult();
            return result;
        }
    }
}
