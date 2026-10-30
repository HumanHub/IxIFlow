using System.Text.Json;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "control-flow"), Trait("Spec", "W-03")]
public sealed partial class SwitchSteppingScenarios
{
    public static IEnumerable<object[]> Routes => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { "case", "default", "no-match", "empty-case", "empty-default", "empty-switch", "default-only" }.SelectMany(route =>
            new[] { DebugStepKind.Into, DebugStepKind.Over, DebugStepKind.Out }.Select(kind => new object[] { profile, route, kind })));

    private static async Task LiveAndReplayStepsAgreeAcrossEverySwitchRoute(string profile, string route, DebugStepKind kind)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);

        var definition = new WorkflowBuilder<SwitchScenarios.Data, object>([], "SwitchReplayRoutes").WaitFor<SwitchScenarios.Go>("start")
            .Switch(ctx => ctx.WorkflowData.Route, c =>
            {
                if (route is not ("empty-switch" or "default-only"))
                    c.Case(1, b =>
                    {
                        if (route != "empty-case")
                            b.Step<SwitchScenarios.Seed>().WithId("inside");
                    });

                if (route is not ("no-match" or "empty-switch"))
                    c.Default(b =>
                    {
                        if (route != "empty-default")
                            b.Step<SwitchScenarios.Seed>().WithId("fallback");
                    });
            }).WithId("switch").Step<SwitchScenarios.Seed>().WithId("after").Build();

        var routeValue = route is "case" or "empty-case" ? 1 : 2;
        var chosen = route is "case" ? "inside" : route is "default" or "default-only" ? "fallback" : "after";
        var start = kind == DebugStepKind.Out && chosen != "after" ? chosen : "switch";
        var expected = kind == DebugStepKind.Into ? chosen : "after";
        var repository = store.NewRepository();
        var registry = new WorkflowCatalog();
        await registry.RegisterWorkflowAsync(definition);

        ServiceProvider Services()
        {
            var services = new ServiceCollection().AddLogging().AddIxIFlow(o => o.Debugging = true).Services.AddSingleton(repository)
                .AddSingleton<IWorkflowCatalog>(registry);

            store.ConfigureHost(services);
            var provider = services.BuildServiceProvider();
            store.StartHostLinksAsync(provider).GetAwaiter().GetResult();

            return provider;
        }

        string id;

        await using (var first = Services())
        {
            var client = first.GetRequiredService<IWorkflowClient>();
            var ops = first.GetRequiredService<IWorkflowOperations>();
            var waiting = await client.RunAsync(definition, new SwitchScenarios.Data { Route = routeValue });
            id = waiting.InstanceId;
            await ops.SetBreakpointsAsync(id, [new("start", start)], "test");
            Assert.Equal(WorkflowExecutionStatus.Paused, (await client.ResumeAsync(id, "start", new SwitchScenarios.Go())).Status);
        }

        await using var second = Services();
        var operations = second.GetRequiredService<IWorkflowOperations>();
        var before = (await operations.InspectAsync(id))!;
        var thread = Assert.Single(before.Threads);
        Assert.Equal(start, thread.AuthorStepId);
        var traceBefore = await operations.GetTraceAsync(id);
        var sequence = traceBefore[^1].Sequence;
        await operations.SetBreakpointsAsync(id, [], "test");
        InstanceInspection? after = null;
        long afterSequence = 0;
        var stepped = await operations.StepAsync(id, thread.ContinuationId, kind, "live-step");

        // Out from the root scope completes; Out from a selected body stops after Switch.
        if (kind == DebugStepKind.Out && start == "switch")
            Assert.Equal(WorkflowExecutionStatus.Success, stepped.Status);
        else
        {
            Assert.Equal(WorkflowExecutionStatus.Paused, stepped.Status);
            after = (await operations.InspectAsync(id))!;
            afterSequence = (await operations.GetTraceAsync(id))[^1].Sequence;
            Assert.Equal(expected, Assert.Single(after.Threads).AuthorStepId);
            Assert.Equal(WorkflowExecutionStatus.Success, (await operations.ContinueAsync(id, "finish")).Status);
        }

        var trace = await operations.GetTraceAsync(id);
        var replay = TraceReplay.Load(definition, trace);
        VerifyRecordedInspection(replay, sequence, before);

        if (after != null)
            VerifyRecordedInspection(replay, afterSequence, after);

        VerifyOccurrences(replay, trace);
        var target = definition.StepTable.Single(s => s.AuthorId == expected).CatalogId;
        replay.Seek(trace[^1].Sequence);
        Assert.Null(replay.CurrentStepId);
        Assert.Equal(WorkflowStatus.Completed, replay.Current.Status);

        // Stepping forward from the saved live pause must encounter exactly the selected route.
        replay.Seek(sequence);
        var stops = new List<string>();

        while (replay.StepForward())
            if (replay.CurrentStepId is { } step && (stops.Count == 0 || stops[^1] != step))
                stops.Add(step);

        Assert.Contains(target, stops);

        Assert.DoesNotContain(
            definition.StepTable.SingleOrDefault(s => s.AuthorId == (chosen == "inside" ? "fallback" : "inside"))?.CatalogId ??
            "unselected", stops);
    }

    public static IEnumerable<object[]> NestedStarts => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { "sequence", "node", "state" }.SelectMany(shape => new[] { false, true }.SelectMany(fallback =>
            new[] { DebugStepKind.Into, DebugStepKind.Over, DebugStepKind.Out }.Select(kind =>
                new object[] { profile, shape, fallback, kind }))));

    private static async Task NestedSwitchStepsPreserveScopeFramesAndOccurrencesAcrossRecovery(string profile, string shape, bool fallback,
        DebugStepKind kind)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var builder = new WorkflowBuilder<SwitchScenarios.Data, object>([], "NestedSwitchStep").WaitFor<SwitchScenarios.Go>("start");

        IWorkflowBuilder<SwitchScenarios.Data, SwitchScenarios.Seed>
            Work(IWorkflowBuilder<SwitchScenarios.Data, SwitchScenarios.Go> body) => body
            .Switch(ctx => ctx.WorkflowData.Route, c => c
                .Case(1, arm => arm.Sequence(b =>
                        b.Step<SwitchScenarios.Seed>().WithId("inside-case").Step<SwitchScenarios.Seed>().WithId("inner-tail-case"))
                    .Step<SwitchScenarios.Seed>().WithId("arm-tail-case"))
                .Default(arm => arm.Sequence(b =>
                        b.Step<SwitchScenarios.Seed>().WithId("inside-default").Step<SwitchScenarios.Seed>().WithId("inner-tail-default"))
                    .Step<SwitchScenarios.Seed>().WithId("arm-tail-default")))
            .WithId("switch").Step<SwitchScenarios.Seed>().WithId("after-switch");

        var definition = shape switch
        {
            "node" => builder.Flowchart(g => g.Start("a").Node("a", b => Work(b))).Step<SwitchScenarios.Seed>().WithId("after-graph")
                .Build(),
            "state" => builder.StateMachine(m => m.Start("a").State("a", s => s.Entry(b => Work(b)).Otherwise("done"))
                .State("done", s => s.Final())).Step<SwitchScenarios.Seed>().WithId("after-graph").Build(),
            _ => Work(builder).Build()
        };

        var repository = store.NewRepository();
        var registry = new WorkflowCatalog();
        await registry.RegisterWorkflowAsync(definition);

        ServiceProvider Services()
        {
            var services = new ServiceCollection().AddLogging().AddIxIFlow(o => o.Debugging = true).Services
                .AddSingleton(repository).AddSingleton<IWorkflowCatalog>(registry);

            store.ConfigureHost(services);
            var provider = services.BuildServiceProvider();
            store.StartHostLinksAsync(provider).GetAwaiter().GetResult();

            return provider;
        }

        var suffix = fallback ? "default" : "case";
        string id;
        InstanceInspection before;
        long beforeSequence;

        await using (var first = Services())
        {
            var client = first.GetRequiredService<IWorkflowClient>();
            var ops = first.GetRequiredService<IWorkflowOperations>();
            id = (await client.RunAsync(definition, new SwitchScenarios.Data { Route = fallback ? 2 : 1 })).InstanceId;
            await ops.SetBreakpointsAsync(id, [new("nested-start", "inside-" + suffix)], "test");
            Assert.Equal(WorkflowExecutionStatus.Paused, (await client.ResumeAsync(id, "start", new SwitchScenarios.Go())).Status);
            before = (await ops.InspectAsync(id))!;
            beforeSequence = (await ops.GetTraceAsync(id))[^1].Sequence;
        }

        await using var second = Services();
        var operations = second.GetRequiredService<IWorkflowOperations>();
        var thread = Assert.Single(before.Threads);
        var arm = Assert.Single(thread.Frames, frame => frame.Kind == "SwitchCase");
        Assert.Equal("Sequence", thread.Frames[^1].Kind);

        if (shape != "sequence")
            Assert.Contains(thread.Frames, frame => frame.Kind == (shape == "node" ? "Node" : "State"));

        await operations.SetBreakpointsAsync(id, [], "test");
        Assert.Equal(WorkflowExecutionStatus.Paused, (await operations.StepAsync(id, thread.ContinuationId, kind, "step")).Status);
        var after = (await operations.InspectAsync(id))!;
        var afterSequence = (await operations.GetTraceAsync(id))[^1].Sequence;
        var stopped = Assert.Single(after.Threads);
        Assert.Equal(kind == DebugStepKind.Out ? "after-switch" : "inner-tail-" + suffix, stopped.AuthorStepId);

        if (kind == DebugStepKind.Out)
            Assert.DoesNotContain(stopped.Frames, frame => frame.ActivationId == arm.ActivationId);
        else
            Assert.Contains(stopped.Frames, frame => frame.ActivationId == arm.ActivationId && frame.ScopeId == arm.ScopeId);

        Assert.Equal(WorkflowExecutionStatus.Success, (await operations.ContinueAsync(id, "finish")).Status);
        var trace = await operations.GetTraceAsync(id);
        var replay = TraceReplay.Load(definition, trace);
        VerifyRecordedInspection(replay, beforeSequence, before);
        VerifyRecordedInspection(replay, afterSequence, after);
        VerifyOccurrences(replay, trace);

        var entered = trace.Where(e =>
                e.Sequence > beforeSequence && e.Sequence <= afterSequence && e.Entry.EntryType == TraceEntryType.ActivityStarted)
            .Select(e => definition.StepTable.Single(s => s.CatalogId == e.Entry.StepId).AuthorId).ToArray();

        Assert.Equal(
            kind == DebugStepKind.Out
                ? new[] { "inside-" + suffix, "inner-tail-" + suffix, "arm-tail-" + suffix }
                : new[] { "inside-" + suffix }, entered);

        Assert.DoesNotContain(trace,
            e => e.Entry.StepId == definition.StepTable.Single(s => s.AuthorId == "inside-" + (fallback ? "case" : "default")).CatalogId);
    }

    private static void VerifyRecordedInspection(TraceReplay replay, long sequence, InstanceInspection live)
    {
        replay.Seek(sequence);
        Assert.True(replay.Current.IsCheckpoint);
        Assert.True(replay.Current.IsComplete);
        Assert.Equal(live.Revision, replay.Current.Revision);
        Assert.Equal(JsonSerializer.Serialize(live.Threads), JsonSerializer.Serialize(replay.Current.Threads));

        // Includes scope IDs, activation IDs, item/loop indices, graph visits and arm occurrences.
        replay.AttachCheckpoint(live);
    }

    private static void VerifyOccurrences(TraceReplay replay, IReadOnlyList<StoredTraceEntry> trace)
    {
        foreach (var stored in trace.Where(e => e.Entry.EntryType is TraceEntryType.StepEntered or TraceEntryType.ActivityStarted))
        {
            replay.Seek(stored.Sequence);
            Assert.Equal(stored.Entry.StepId, replay.CurrentStepId);
            var thread = Assert.Single(replay.Current.Threads, t => t.ContinuationId == stored.Entry.ContinuationId);
            Assert.Equal(stored.Entry.StepId, thread.StepId);
            Assert.Equal(stored.Entry.FrameActivationId, thread.FrameId);
            Assert.Equal(thread.FrameId, thread.Frames[^1].ActivationId);
        }

        foreach (var selected in trace.Where(e => e.Entry.EntryType == TraceEntryType.SwitchSelected))
        {
            replay.Seek(selected.Sequence);
            Assert.Null(replay.CurrentStepId);
        }
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ParallelForEachOpensBothSelectedCaseWaitsBeforeEitherCompletes(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var services = new ServiceCollection().AddLogging().AddIxIFlow(o => o.Debugging = true).Services;
        services.AddSingleton(store.NewRepository());
        store.ConfigureHost(services);
        await using var provider = services.BuildServiceProvider();
        await store.StartHostLinksAsync(provider);

        var definition = Workflow.Create<SwitchScenarios.Data>("ParallelSwitchItems").Step<SwitchScenarios.Seed>()
            .ForEach(itemsExpression: ctx => ctx.WorkflowData.Items, body: (b, item) => b.Switch(ctx => ctx.Item(item), c => c
                .Case(1, arm => arm.WaitFor<SwitchScenarios.Go>("one"))
                .Default(arm => arm.WaitFor<SwitchScenarios.Go>("two"))), setup: s => s.Parallel(2)).Build();

        var client = provider.GetRequiredService<IWorkflowClient>();
        var waiting = await client.RunAsync(definition, new SwitchScenarios.Data { Items = [1, 2] });
        Assert.Equal(WorkflowExecutionStatus.Suspended, waiting.Status);

        Assert.Equal(new[] { "one", "two" },
            (await client.GetOpenWaitsAsync(new WaitQuery(InstanceId: waiting.InstanceId))).Select(w => w.Key).Order().ToArray());

        Assert.Equal(WorkflowExecutionStatus.Suspended,
            (await client.ResumeAsync(waiting.InstanceId, "one", new SwitchScenarios.Go())).Status);

        Assert.Equal(WorkflowExecutionStatus.Success,
            (await client.ResumeAsync(waiting.InstanceId, "two", new SwitchScenarios.Go())).Status);
    }
}