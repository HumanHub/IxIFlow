using System.Text.Json;
using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "scopes"), Trait("Spec", "F-26")]
public sealed class ScopeTraceTests
{
    [Theory, InlineData(false), InlineData(true)]
    public async Task TwoBodyActivitiesCostSevenCommitsAndLifecycleTracesContainOnlyIdentity(bool affine)
    {
        var probe = new ScopeActivityTests.Probe(); var store = new CountingStore();
        await using var services = Services(probe, store);
        var definition = ScopeActivityTests.Start("ScopeCommitBudget").Scope<ScopeActivityTests.TestScope>(
            s => s.Threading(affine ? ScopeThreading.ThreadAffine : ScopeThreading.AnyThread), b => b.Step<ScopeActivityTests.Read>().Step<ScopeActivityTests.Read>()).Build();
        var done = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new ScopeActivityTests.Data());
        Assert.True(done.Status == WorkflowExecutionStatus.Success, done.ErrorMessage);
        Assert.Equal(7, store.Commits);
        var traces = await services.GetRequiredService<IWorkflowOperations>().GetTraceAsync(done.InstanceId);
        var lifecycle = traces.Select(item => item.Entry).Where(entry => entry.Payload is ScopeTrace).ToArray();
        Assert.Equal(new[] { TraceEntryType.ScopeOpened, TraceEntryType.ScopeClosed }, lifecycle.Select(entry => entry.EntryType));
        Assert.All(lifecycle, entry => Assert.Equal(WorkflowStepType.Scope, Assert.IsType<ScopeTrace>(entry.Payload).StepKind));
        Assert.Single(lifecycle.Select(entry => Assert.IsType<ScopeTrace>(entry.Payload).InvocationId).Distinct());
        var replay = TraceReplay.Load(definition, traces);
        replay.Continue();
        Assert.Empty(replay.Current.Threads);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task ScopeInsideEachNodeVisitPaysOneIntentWhileEnclosingScopePaysOne(bool inside)
    {
        var probe = new ScopeActivityTests.Probe(); var store = new CountingStore();
        await using var services = Services(probe, store);
        var builder = new WorkflowBuilder<Counter, object>([], "ScopeGraphBudget");
        var definition = inside ? builder.Flowchart(f => f.Start("again").Node("again", b => b.Scope<ScopeActivityTests.TestScope>(null,
                x => x.Step<Increment>(s => s.Input(a => a.Count).From(c => c.WorkflowData.Count).Output(a => a.Count).To(c => c.WorkflowData.Count))), e => e.When(c => c.WorkflowData.Count < 3, "again"))).Build()
            : builder.Scope<ScopeActivityTests.TestScope>(null, b => b.Flowchart(f => f.Start("again").Node("again", x => x.Step<Increment>(s => s.Input(a => a.Count)
                .From(c => c.WorkflowData.Count).Output(a => a.Count).To(c => c.WorkflowData.Count)), e => e.When(c => c.WorkflowData.Count < 3, "again")))).Build();
        var done = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Counter());
        Assert.True(done.Status == WorkflowExecutionStatus.Success, done.ErrorMessage);
        Assert.Equal(inside ? 11 : 9, store.Commits);
        Assert.Equal(inside ? 3 : 1, probe.Invocations.Distinct().Count());
    }

    [Fact]
    public async Task InspectionRedactsScopePropertyClassificationAndNeverProjectsTheLiveValue()
    {
        var probe = new ScopeActivityTests.Probe(); var store = new CountingStore();
        await using var services = Services(probe, store);
        var definition = ScopeActivityTests.Start("ScopeRedaction").Scope<SensitiveContainer>(s => s.Input(a => a.Secret).From(c => "private-setting"),
            b => b.WaitFor<ScopeActivityTests.Signal>("hold")).Build();
        var idle = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new ScopeActivityTests.Data());
        var operations = services.GetRequiredService<IWorkflowOperations>();
        var inspection = (await operations.InspectAsync(idle.InstanceId))!;
        Assert.Single(inspection.Scopes);
        var json = JsonSerializer.Serialize(inspection);
        Assert.DoesNotContain("private-setting", json); Assert.DoesNotContain("live-resource-only", json);
        Assert.DoesNotContain("private-setting", JsonSerializer.Serialize(await operations.GetTraceAsync(idle.InstanceId)));
    }

    private static ServiceProvider Services(ScopeActivityTests.Probe probe, CountingStore store)
    {
        var services = new ServiceCollection(); services.AddSingleton(probe); services.AddIxIFlow(options => options.VerifyCheckpointParts = true);
        services.AddSingleton<IWorkflowStateRepository>(store); return services.BuildServiceProvider();
    }
    private sealed class CountingStore : FaultInjectingRepository
    {
        public int Commits;
        public override async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        { var result = await base.CommitWorkflowInstanceAsync(commit); if (result.Status == WorkflowCommitStatus.Applied) Interlocked.Increment(ref Commits); return result; }
    }
    public sealed class Counter { public int Count { get; set; } }
    public sealed class Increment : IAsyncActivity
    { public int Count { get; set; } public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken) { Count++; return Task.CompletedTask; } }
    public sealed class SensitiveContainer(ScopeActivityTests.Probe probe) : IScopeActivity<ScopeActivityTests.Resource>
    {
        [Sensitive, System.Text.Json.Serialization.JsonPropertyName("account")] public string Secret { get; set; } = "";
        public Task<ScopeActivityTests.Resource> OpenAsync(ScopeOpening opening, IScopeContext context, CancellationToken cancellationToken) => Task.FromResult(new ScopeActivityTests.Resource("live-resource-only", probe));
        public Task CloseAsync(ScopeActivityTests.Resource value, ScopeClosing closing, IScopeContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
