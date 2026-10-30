using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using static IxIFlow.Tests.ExecutionTests.CancellationScopeTests;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "scopes"), Trait("Spec", "W-06")]
public sealed class CancellationScopeChildrenTests
{
    [Theory, InlineData(false), InlineData(true)]
    public async Task BodyChildSettlesBeforeHandlerWhileDetachedChildRetainsItsPolicy(bool detached)
    {
        var probe = new Probe();
        var store = new InMemoryWorkflowStateRepository();
        await using var services = Services(store, probe);
        var client = services.GetRequiredService<IWorkflowClient>();

        var child = new WorkflowBuilder<Data, object>([], "CancellationBodyChild").Try(b => b.WaitFor<Go>("child"))
            .Finally(b => b.Step<Mark>(s => s.Input(x => x.Text).From(c => "child-finally"))).Build();

        await services.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(child);

        var parent = new WorkflowBuilder<Data, object>([], "CancellationParent").CancellationScope(
            b => b.Invoke<Data>(child.Name, 1, s => s.CancelWithParent(!detached)),
            h => h.Step<Mark>(s => s.Input(x => x.Text).From(c => "H"))).Build();

        var idle = await client.RunAsync(parent, new Data());
        var childId = await WaitingChild(store, client, idle.InstanceId, "child");
        await client.CancelAsync(idle.InstanceId, new() { ReasonCode = "test" });
        var done = await client.AwaitAsync(idle.InstanceId, WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(WorkflowExecutionStatus.Cancelled, done.Status);
        Assert.Equal(detached ? ["H"] : new[] { "child-finally", "H" }, probe.Events);

        Assert.Equal(detached ? WorkflowExecutionStatus.Suspended : WorkflowExecutionStatus.Cancelled,
            await client.GetStatusAsync(childId));

        if (detached)
            await client.CancelAsync(childId, new() { ReasonCode = "cleanup-test" });
    }

    [Fact]
    public async Task ChildStartedByHandlerSurvivesParentCancellationAndProviderRestart()
    {
        var store = new InMemoryWorkflowStateRepository();
        var catalog = new WorkflowCatalog();

        var child = new WorkflowBuilder<Data, object>([], "CancellationCleanupChild").WaitFor<Go>("child")
            .Assign(c => c.WorkflowData.Code, c => 7).Build();

        var parent = new WorkflowBuilder<Data, object>([], "CancellationCleanupParent").CancellationScope(b => b.WaitFor<Go>("body"),
            h => h.Invoke<Data>(child.Name, 1, s => s.Output(x => x.Code).To(c => c.WorkflowData.Code))).Build();

        await catalog.RegisterWorkflowAsync(child);
        await catalog.RegisterWorkflowAsync(parent);

        ServiceProvider Open()
        {
            var c = new ServiceCollection();

            c.AddIxIFlow(o =>
            {
                o.VerifyCheckpointParts = true;
                o.PayloadThresholdBytes = 1;
            });

            c.AddSingleton<IWorkflowStateRepository>(store);
            c.AddSingleton<IWorkflowCatalog>(catalog);

            return c.BuildServiceProvider();
        }

        string id, childId;

        await using (var first = Open())
        {
            var client = first.GetRequiredService<IWorkflowClient>();
            id = (await client.RunAsync(parent, new Data())).InstanceId;
            await client.CancelAsync(id, new() { ReasonCode = "test" });
            childId = await WaitingChild(store, client, id, "child");
            Assert.Null(await store.GetCancellationRequestAsync(childId));
        }

        await using var second = Open();
        var recovered = second.GetRequiredService<IWorkflowClient>();
        Assert.Equal(WorkflowExecutionStatus.Success, (await recovered.ResumeAsync(childId, "child", new Go())).Status);
        var done = await recovered.AwaitAsync(id, WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(WorkflowExecutionStatus.Cancelled, done.Status);
        Assert.Equal(7, Assert.IsType<Data>(done.WorkflowData).Code);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task UnresolvedBodyActivityHoldsHandlerUntilResolutionAndTerminationBypassesIt(bool terminate)
    {
        var store = new FaultInjectingRepository();
        var probe = new Probe();

        var definition = new WorkflowBuilder<Data, object>([], "CancellationUnresolved").CancellationScope(
            b => b.Step<Mark>(s => s.Input(x => x.Text).From(c => "body")),
            h => h.Step<Mark>(s => s.Input(x => x.Text).From(c => "H"))).Build();

        string id = Guid.NewGuid().ToString("N");

        await using (var first = Services(store, probe))
        {
            store.FailCommit = instance => probe.Events.Contains("body") &&
                ExecutionCheckpoint.Read(StoredCheckpoint.WholeState(instance)).Continuations[0].PendingActivity == null;

            await Assert.ThrowsAsync<WorkflowEngineException>(() =>
                first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data(), new() { InstanceId = id }));
        }

        store.FailCommit = _ => false;
        await using var second = Services(store, probe);
        await second.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(definition);
        var client = second.GetRequiredService<IWorkflowClient>();
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, (await client.RecoverAsync(id)).Status);
        await client.CancelAsync(id, new() { ReasonCode = "test" });
        Assert.Equal(new[] { "body" }, probe.Events);

        if (terminate)
        {
            await second.GetRequiredService<IWorkflowOperations>().TerminateAsync(id, "stop", actor: "test");
            Assert.Equal(new[] { "body" }, probe.Events);

            return;
        }

        var pending = Assert.Single(await client.GetPendingActivitiesAsync(id));

        var done = await client.ResolveAsync(id, pending.InvocationId,
            ActivityResolution.Completed(new Dictionary<string, object?> { ["Text"] = "body" }));

        Assert.Equal(WorkflowExecutionStatus.Cancelled, done.Status);
        Assert.Equal(new[] { "body", "H" }, probe.Events);
    }

    private static async Task<string> WaitingChild(IWorkflowStateRepository store, IWorkflowClient client, string parent, string key)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        while (true)
        {
            var children = (await store.QueryInstancesAsync(new InstanceQuery(ParentInstanceId: parent))).Items;

            if (children.Count == 1 && (await client.GetOpenWaitsAsync(new(InstanceId: children[0].InstanceId))).Any(w => w.Key == key))
                return children[0].InstanceId;

            await Task.Delay(10, timeout.Token);
        }
    }
}