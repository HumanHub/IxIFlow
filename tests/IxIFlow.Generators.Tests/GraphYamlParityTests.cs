using System.Text.Json;
using System.Text.RegularExpressions;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Dsl.Documents;
using IxIFlow.Extensions;
using IxIFlow.Generators.Tests.Generated;
using Json.Schema;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace IxIFlow.Generators.Tests;

[Trait("Spec", "F-06")]
public sealed class GraphYamlParityTests
{
    [Theory]
    [InlineData("enum", true)] [InlineData("enum", false)]
    [InlineData("decimal", true)] [InlineData("decimal", false)]
    [InlineData("boxedLong", true)] [InlineData("boxedLong", false)]
    public async Task TypedSwitchLabelsMatchFluentHashAndRoutingThroughBothSerializers(string type, bool matches)
    {
        var selector = type switch
        {
            "enum" => matches ? "ParityMode.Other" : "ParityMode.Ready",
            "decimal" => matches ? "1.25m" : "2.5m",
            _ => matches ? "(object)1L" : "(object)1"
        };
        var label = type switch { "enum" => "ParityMode.Other", "decimal" => "1.25m", _ => "1L" };
        var yaml = Document("""
            - kind: flowchart
              start: route
              nodes:
                - key: route
                  switch: {kind: function, function: selector}
                  transitions: [{case: {kind: function, function: label}, to: selected}, {to: end}]
                - {key: selected, steps: [{kind: activity, activity: ParityWork}]}
            """) + "\ncode:\n  selector: {language: csharp-expression, source: '" + selector.Replace("ParityMode", "IxIFlow.Generators.Tests.ParityMode") +
            "'}\n  label: {language: csharp-expression, source: '" + label.Replace("ParityMode", "IxIFlow.Generators.Tests.ParityMode") + "'}\n";
        var fluent = Start().Flowchart(f =>
        {
            f.Start("route");
            if (type == "enum")
            {
                if (matches) f.Node("route", e => e.Switch(c => ParityMode.Other).Case(ParityMode.Other, "selected").Otherwise("end"));
                else f.Node("route", e => e.Switch(c => ParityMode.Ready).Case(ParityMode.Other, "selected").Otherwise("end"));
            }
            else if (type == "decimal")
            {
                if (matches) f.Node("route", e => e.Switch(c => 1.25m).Case(1.25m, "selected").Otherwise("end"));
                else f.Node("route", e => e.Switch(c => 2.5m).Case(1.25m, "selected").Otherwise("end"));
            }
            else
            {
                if (matches) f.Node("route", e => e.Switch(c => (object)1L).Case(1L, "selected").Otherwise("end"));
                else f.Node("route", e => e.Switch(c => (object)1).Case(1L, "selected").Otherwise("end"));
            }
            f.Node("selected", n => n.Step<ParityWork>());
        }).Build();
        var document = WorkflowYamlSerializer.Read(yaml);
        var schema = JsonSchema.FromFile(WorkflowSchemaFiles.SchemaPath, new BuildOptions { SchemaRegistry = new SchemaRegistry() });
        foreach (var text in new[] { yaml, WorkflowYamlSerializer.Write(document), WorkflowYamlSerializer.Write(WorkflowJsonSerializer.Read(WorkflowJsonSerializer.Write(document))) })
        {
            Assert.True(schema.Evaluate(WorkflowYamlSchemaValueTests.Read(text)).IsValid);
            Assert.Equal(matches ? 1 : 0, (await Match(fluent, GeneratorRegressionTests.Build(text))).Work);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task EmptyExitPresenceSurvivesLoweringSerializationAndTrace(bool exit)
    {
        var yaml = Document("- {kind: stateMachine, start: a, states: [{key: a, " + (exit ? "exit: [], " : "") + "transitions: [{to: done}]}, {key: done, final: true}]}");
        var fluent = Start().StateMachine(m => m.Start("a").State("a", s =>
        {
            if (exit) s.Exit(e => e);
            s.Immediately(t => t.Otherwise("done"));
        }).State("done", s => s.Final())).Build();
        var document = WorkflowYamlSerializer.Read(yaml);
        foreach (var text in new[] { yaml, WorkflowYamlSerializer.Write(document), WorkflowYamlSerializer.Write(WorkflowJsonSerializer.Read(WorkflowJsonSerializer.Write(document))) })
        {
            var generated = GeneratorRegressionTests.Build(text);
            await Match(fluent, generated);
            Assert.Equal(exit, generated.GraphTable.Single().Nodes.Single(n => n.Key == "a").ExitScopeId != null);
            await using var services = Services();
            var run = await services.GetRequiredService<IWorkflowClient>().RunAsync(generated, new ParityData());
            var trace = await services.GetRequiredService<IWorkflowOperations>().GetTraceAsync(run.InstanceId);
            Assert.Equal(exit ? 1 : 0, trace.Count(t => t.Entry.EntryType == TraceEntryType.ExitEntered && t.Entry.StepId == "root/0/state/a/exit/0"));
        }
    }

    [Fact]
    public async Task FlowchartGuardsSwitchActionsAndTerminalDecisionsMatchFluent()
    {
        var yaml = Build("""
            - kind: flowchart
              id: graph
              start: check
              nodes:
                - key: check
                  steps: [{kind: activity, activity: ParityEcho, input: [{target: Value, from: {kind: constant, value: 5}}]}]
                  transitions: [{when: {path: previousStep.Value > 3}, to: route}, {to: end}]
                - key: route
                  switch: {path: workflow.Subject}
                  transitions: [{case: order-1, to: done}, {to: end}]
                - key: done
                  steps: [{kind: activity, activity: ParityWork}]
            """);
        var fluent = Start().Flowchart(f => f.Start("check")
            .Node("check", n => n.Step<ParityEcho>(a => a.Input(x => x.Value).From(c => 5)),
                e => e.When(c => c.PreviousStep.Value > 3, "route").Otherwise("end"))
            .Node("route", e => e.Switch(c => c.WorkflowData.Subject).Case("order-1", "done").Otherwise("end"))
            .Node("done", n => n.Step<ParityWork>())).WithId("graph").Build();
        var result = await Match(fluent, yaml);
        Assert.Equal(1, result.Work);
        await using var services = Services();
        var run = await services.GetRequiredService<IWorkflowClient>().RunAsync(yaml, new ParityData());
        var trace = await services.GetRequiredService<IWorkflowOperations>().GetTraceAsync(run.InstanceId);
        var replay = TraceReplay.Load(yaml, trace);
        foreach (var entry in trace.Where(e => e.Entry.EntryType == TraceEntryType.TransitionTaken))
        { replay.Seek(entry.Sequence); Assert.Null(replay.CurrentStepId); }
        Assert.Single(trace, e => e.Entry.StepId == "root/0/node/done/2" && e.Entry.EntryType == TraceEntryType.StepEntered);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ExplicitTimeoutTransitionsMatchAndKeepRealEdgeReplayStops(bool state)
    {
        var body = """
            [{kind: waitFor, key: go, event: Answer, timeout: '00:01:00', onTimeout: [{kind: transition, id: leave, to: done}]}]
            """;
        var yaml = Build(state
            ? "- kind: stateMachine\n  start: wait\n  states:\n    - key: wait\n      entry: " + body + "\n      transitions: [{on: unused, event: Answer, to: done}]\n    - {key: done, final: true}"
            : "- kind: flowchart\n  start: wait\n  nodes:\n    - key: wait\n      steps: " + body + "\n    - {key: done}");
        var fluent = state
            ? Start().StateMachine(m => m.Start("wait").State("wait", s => s.Entry(e => e.WaitFor<Answer>("go", configure: w => w
                    .TimeoutAfter(TimeSpan.FromMinutes(1)).OnTimeout(t => t.Transition("done").WithId("leave"))))
                    .On<Answer>("unused", "done")).State("done", s => s.Final())).Build()
            : Start().Flowchart(f => f.Start("wait").Node("wait", n => n.WaitFor<Answer>("go", configure: w => w
                    .TimeoutAfter(TimeSpan.FromMinutes(1)).OnTimeout(t => t.Transition("done").WithId("leave"))))
                    .Node("done")).Build();
        await Match(fluent, yaml, async (services, result) => await Advance(services, result));
        Assert.Contains(yaml.GraphTable.Single().Transitions, t => t.Kind == TransitionCause.Explicit && t.Cases.Single().To == "done");
    }

    [Theory]
    [InlineData("event")] [InlineData("reply")] [InlineData("timer")] [InlineData("expression")]
    public async Task NativeTriggersExitAndActionsUseTheSameTypesHashAndResults(string form)
    {
        var trigger = form switch
        {
            "event" => "on: answer\n          event: Answer",
            "reply" => "reply: answer\n          event: Answer",
            "timer" => "after: '00:01:00'",
            _ => "after: {path: 'previousStep.Value == 6 ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(2)'}"
        };
        var guard = form is "event" or "reply" ? "previousStep.Value == 7" : "previousStep.Value == 6";
        var yaml = Build("""
            - kind: activity
              activity: ParityEcho
              input: [{target: Value, from: {kind: constant, value: 6}}]
            - kind: stateMachine
              id: machine
              start: pending
              states:
                - key: pending
                  entry: [{ENTRY}]
                  exit: [{kind: activity, activity: ParityEcho, input: [{target: Value, from: {path: previousStep.Value}}], output: [{source: Value, to: {path: workflow.Value}}]}]
                  transitions:
                    - TRIGGER
                      cases:
                        - when: {path: GUARD}
                          to: done
                          steps: [{kind: activity, activity: ParityEcho, input: [{target: Value, from: {path: previousStep.Value}}], output: [{source: Value, to: {path: workflow.Result}}]}]
                        - to: end
                - {key: done, final: true, entry: [{kind: activity, activity: ParitySecondWork}]}
            """.Replace("ENTRY", form == "reply" ? "kind: request, activity: ParityRequest, key: answer" : "kind: activity, activity: ParityWork")
            .Replace("TRIGGER", trigger).Replace("GUARD", guard));
        var fluent = Workflow.Create<ParityData>("GraphParity").Step<ParityEcho>(a => a.Input(x => x.Value).From(c => 6)).StateMachine(m => m.Start("pending")
            .State("pending", s =>
            {
                if (form == "reply") s.Entry(e => e.Request<ParityRequest>("answer"));
                else s.Entry(e => e.Step<ParityWork>());
                s.Exit(e => e.Step<ParityEcho>(a => a.Input(x => x.Value).From(c => c.PreviousStep.Value)
                    .Output(x => x.Value).To(c => c.WorkflowData.Value)));
                if (form is "event" or "reply")
                {
                    if (form == "event") s.On<Answer>("answer", Configure<Answer>);
                    else s.OnReply<Answer>("answer", Configure<Answer>);
                }
                else if (form == "timer") s.After(TimeSpan.FromMinutes(1), Configure<ParityEcho>);
                else s.After(c => c.PreviousStep.Value == 6 ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(2), Configure<ParityEcho>);
            }).State("done", s => s.Final().Entry(e => e.Step<ParitySecondWork>()))).WithId("machine").Build();
        var result = await Match(fluent, yaml, async (services, idle) =>
        {
            var client = services.GetRequiredService<IWorkflowClient>();
            if (form == "reply") return await client.ReplyAsync(services.GetRequiredService<ParityProbe>().Tokens.Single(), new Answer { Value = 7 });
            if (form == "event") return await client.ResumeAsync(idle.InstanceId, "answer", new Answer { Value = 7 });
            return await Advance(services, idle);
        });
        Assert.Equal(6, result.Value);
        Assert.Equal(form is "event" or "reply" ? 7 : 6, result.Result);
        Assert.Equal(1, result.Second);

        void Configure<T>(StateTransitionBuilder<ParityData, ParityEcho, T> t) where T : class
        {
            // Explicit typed expressions below keep the two fired-value contracts distinct.
            if (typeof(T) == typeof(Answer))
                ((StateTransitionBuilder<ParityData, ParityEcho, Answer>)(object)t)
                    .When(c => c.PreviousStep.Value == 7, "done", a => a.Step<ParityEcho>(x => x
                        .Input(x => x.Value).From(c => c.PreviousStep.Value).Output(x => x.Value).To(c => c.WorkflowData.Result)))
                    .Otherwise("end");
            else ((StateTransitionBuilder<ParityData, ParityEcho, ParityEcho>)(object)t)
                    .When(c => c.PreviousStep.Value == 6, "done", a => a.Step<ParityEcho>(x => x
                        .Input(x => x.Value).From(c => c.PreviousStep.Value).Output(x => x.Value).To(c => c.WorkflowData.Result)))
                    .Otherwise("end");
        }
    }

    [Fact]
    public async Task ImmediateCasesAreOneTriggerAndMatchFluent()
    {
        var generated = Build("""
            - kind: stateMachine
              start: route
              states:
                - key: route
                  transitions:
                    - cases:
                        - {when: {path: workflow.Selected}, to: end}
                        - {to: done, steps: [{kind: activity, activity: ParityWork}]}
                - {key: done, final: true}
            """);
        var fluent = Start().StateMachine(m => m.Start("route").State("route", s => s.Immediately(t => t
            .When(c => c.WorkflowData.Selected, "end").Otherwise("done", a => a.Step<ParityWork>())))
            .State("done", s => s.Final())).Build();
        Assert.Equal(1, (await Match(fluent, generated)).Work);
        Assert.Single(generated.GraphTable.Single().Transitions);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ActivityForkRearmsAndFireAndArmReplayMetadataMatch(bool listener)
    {
        var generated = listener ? Build("""
            - kind: stateMachine
              id: machine
              start: pending
              states:
                - key: pending
                  transitions:
                    - activity: GraphAttempt
                      output: [{source: Value, to: {path: workflow.Result}}]
                      when: {path: previousStep.Value == 2}
                      to: end
            """ + "\n        - {on: go, event: Answer, to: end}") : Workflow_graph_activity.Build();
        var fluent = Start().StateMachine(m => m.Start("pending").State("pending", s =>
        {
            s.OnActivity<GraphAttempt>(t => t.Output(x => x.Value).To(c => c.WorkflowData.Result)
                .When(c => c.PreviousStep.Value == 2, "end"));
            if (listener) s.On<Answer>("go", "end");
        })).WithId("machine").Build();
        Assert.Equal(2, (await Match(fluent, generated)).Result);
        await using var services = Services();
        var run = await services.GetRequiredService<IWorkflowClient>().RunAsync(generated, new ParityData());
        var traces = await services.GetRequiredService<IWorkflowOperations>().GetTraceAsync(run.InstanceId);
        var replay = TraceReplay.Load(generated, traces);
        var fires = traces.Where(t => t.Entry.EntryType == TraceEntryType.StepEntered && t.Entry.StepId!.EndsWith("/run/1")).ToArray();
        Assert.Equal(2, fires.Length);
        var arms = traces.Where(t => t.Entry.EntryType == TraceEntryType.StepEntered && t.Entry.StepId == "root/0/state/pending/1").ToArray();
        Assert.Equal(new long[] { 1, 2, 3, 4 }, arms.Select(t =>
        {
            replay.Seek(t.Sequence);
            return replay.Current.Threads.Single(p => p.ParentContinuationId == null).Frames.Last().ArmOccurrence;
        }));
        foreach (var pick in fires.Concat(arms))
        { replay.Seek(pick.Sequence); Assert.Equal(pick.Entry.StepId, replay.CurrentStepId); }
        var catalog = WorkflowDefinitionValidator.ForExecution(generated);
        Assert.Equal(typeof(GraphAttempt), catalog.Step("root/0/state/pending/on/0/run/1").PreviousStepDataType);
        Assert.Equal(listener, generated.StepTable.Any(s => s.Kind == WorkflowStepType.Listen));
        if (listener) Assert.Equal(typeof(object), catalog.Step("root/0/state/pending/listen/1").PreviousStepDataType);
    }

    [Fact]
    public async Task TriggerInputsMatchesSuggestionsAndOutputsMatchFluent()
    {
        var generated = Build("""
            - kind: stateMachine
              start: pending
              states:
                - key: pending
                  transitions:
                    - on: go
                      event: Answer
                      matches: {path: event.Value > 0}
                      defaultPayload: {Value: 99}
                      input: [{target: Value, from: {path: workflow.Value}}]
                      output: [{source: Value, to: {path: workflow.Result}}]
                      when: {path: previousStep.Value == 7}
                      to: end
            """);
        var fluent = Start().StateMachine(m => m.Start("pending").State("pending", s => s.On<Answer>("go", t => t
            .Matches((evt, c) => evt.Value > 0).DefaultPayload(new Answer { Value = 99 })
            .Input(x => x.Value).From(c => c.WorkflowData.Value)
            .Output(x => x.Value).To(c => c.WorkflowData.Result)
            .When(c => c.PreviousStep.Value == 7, "end")))).Build();
        Assert.Equal(7, (await Match(fluent, generated, async (services, result) =>
        {
            var client = services.GetRequiredService<IWorkflowClient>();
            var wait = Assert.Single(await client.GetOpenWaitsAsync(new(InstanceId: result.InstanceId)));
            Assert.Equal(0, JsonSerializer.Deserialize<Answer>(wait.DefaultPayloadJson!)!.Value);
            Assert.Equal(EventRejection.NotMatched, (await client.ResumeAsync(result.InstanceId, "go", new Answer())).Event!.Rejection);
            Assert.Equal(wait.WaitId, Assert.Single(await client.GetOpenWaitsAsync(new(InstanceId: result.InstanceId))).WaitId);
            return await client.ResumeAsync(result.InstanceId, "go", new Answer { Value = 7 });
        })).Result);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void ExplicitFallbackFlagsPreserveBothFluentEdgeForms(bool fallback)
    {
        var body = "- kind: flowchart\n  start: a\n  nodes:\n    - key: a\n      transitions:\n" +
            (fallback ? "" : "        - {when: {path: workflow.Selected}, to: end}\n") +
            "        - {default: " + (fallback ? "true" : "false") + ", to: end}";
        var fluent = Start().Flowchart(f => f.Start("a").Node("a", e =>
        {
            if (fallback) e.Otherwise("end");
            else e.When(c => c.WorkflowData.Selected, "end").Always("end");
        })).Build();
        Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(Build(body)));
    }

    [Fact]
    public async Task ListenerForkWinsWithItsOwnExecutableFireStopAndEventTypedAction()
    {
        var generated = Workflow_graph_listener.Build();
        var fluent = new WorkflowBuilder<ParityData, object>([], "GraphListenerParity").StateMachine(m => m.Start("pending").State("pending", s => s
            .OnActivity<GraphBlocked>("end").On<Answer>("go", t => t.Otherwise("end", a => a.Step<ParityEcho>(x => x
                .Input(x => x.Value).From(c => c.PreviousStep.Value).Output(x => x.Value).To(c => c.WorkflowData.Result)))))).WithId("machine").Build();
        Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(generated));
        Assert.Equal(await Execute(fluent), await Execute(generated));

        async Task<int> Execute(WorkflowDefinition definition)
        {
            await using var services = Services();
            var client = services.GetRequiredService<IWorkflowClient>();
            var id = Guid.NewGuid().ToString("N");
            var running = client.RunAsync(definition, new ParityData(), new WorkflowOptions { InstanceId = id });
            await services.GetRequiredService<GraphProbe>().Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!(await client.GetOpenWaitsAsync(new(InstanceId: id))).Any()) await Task.Delay(10, deadline.Token);
            var result = await client.ResumeAsync(id, "go", new Answer { Value = 9 });
            if (result.Status != WorkflowExecutionStatus.Success)
                result = await client.AwaitAsync(id, WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(10));
            await running.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
            var traces = await services.GetRequiredService<IWorkflowOperations>().GetTraceAsync(id);
            var fire = Assert.Single(traces, t => t.Entry.EntryType == TraceEntryType.StepEntered && t.Entry.StepId == "root/0/state/pending/listen/1");
            var replay = TraceReplay.Load(definition, traces);
            replay.Seek(fire.Sequence);
            Assert.Equal(fire.Entry.StepId, replay.CurrentStepId);
            foreach (var choice in traces.Where(t => t.Entry.EntryType == TraceEntryType.TransitionTaken))
            { replay.Seek(choice.Sequence); Assert.Null(replay.CurrentStepId); }
            return Assert.IsType<ParityData>(result.WorkflowData).Result;
        }
    }

    [Fact]
    public void GraphNamesLocationsIdsAndDeclarationOrderDoNotChangeTheHash()
    {
        const string body = """
            - kind: flowchart
              id: original
              start: a
              nodes:
                - {key: a, name: First, transitions: [{to: b}]}
                - {key: b, name: Last}
            """;
        var document = WorkflowYamlSerializer.Read(Document(body));
        var chart = (FlowchartStepDocument)document.Definitions.Single();
        var changed = document with { Definitions = [chart with { Id = "renamed", Name = "Display", Nodes = chart.Nodes.AsEnumerable().Reverse()
            .Select(n => n with { Name = "Other", Location = new(100, 20) }).ToList() }] };
        Assert.Equal(WorkflowDefinitionValidator.Inspect(Build(body)), WorkflowDefinitionValidator.Inspect(
            GeneratorRegressionTests.Build(WorkflowYamlSerializer.Write(changed))));
        Assert.NotEqual(WorkflowDefinitionValidator.Inspect(Build(body)), WorkflowDefinitionValidator.Inspect(Build(body.Replace("to: b", "to: end"))));
    }

    internal static string Document(string body) => "workflow: {name: GraphParity, dataType: IxIFlow.Generators.Tests.ParityData}\ndefinitions:\n" +
        Regex.Replace(body, @"\b(Parity[A-Z]\w*|Answer|GraphAttempt|GraphBlocked)\b", m => "IxIFlow.Generators.Tests." + m.Value);
    internal static WorkflowDefinition Build(string body)
    {
        var yaml = Document(body);
        Assert.True(JsonSchema.FromFile(WorkflowSchemaFiles.SchemaPath, new BuildOptions { SchemaRegistry = new SchemaRegistry() })
            .Evaluate(WorkflowYamlSchemaValueTests.Read(yaml)).IsValid, yaml);
        Assert.Empty(WorkflowGeneratorHarness.Generate(yaml, "graph.yaml").Diagnostics);
        return GeneratorRegressionTests.Build(yaml);
    }
    private static WorkflowBuilder<ParityData, object> Start() => new([], "GraphParity");
    internal static ServiceProvider Services()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<TimeProvider>(new ParityClock());
        services.AddSingleton<ParityProbe>();
        services.AddSingleton<GraphProbe>();
        services.AddTransient<ParityRequest>();
        services.AddIxIFlow(o => o.Debugging = true);
        return services.BuildServiceProvider();
    }
    private static async Task<ParityData> Match(WorkflowDefinition fluent, WorkflowDefinition generated,
        Func<ServiceProvider, WorkflowExecutionResult, Task<WorkflowExecutionResult>>? idle = null)
    {
        Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(generated));
        Assert.Equal(JsonSerializer.Serialize(Shape(fluent)), JsonSerializer.Serialize(Shape(generated)));
        var first = await Execute(fluent);
        var second = await Execute(generated);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        return second;
        async Task<ParityData> Execute(WorkflowDefinition definition)
        {
            await using var services = Services();
            var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new ParityData());
            if (idle != null) result = await idle(services, result);
            Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
            var saved = await services.GetRequiredService<IWorkflowStateRepository>().GetWorkflowInstanceAsync(result.InstanceId);
            using var json = JsonDocument.Parse(saved!.ExecutionStateJson);
            Assert.Equal(WorkflowDefinitionValidator.Inspect(definition), json.RootElement.GetProperty("StructureHash").GetString());
            return Assert.IsType<ParityData>(result.WorkflowData);
        }
        static object Shape(WorkflowDefinition definition) => WorkflowDefinitionValidator.ForExecution(definition).AllSteps
            .Select(p => new { p.Key, p.Value.StepType, Activity = p.Value.ActivityType?.FullName, Previous = p.Value.PreviousStepDataType?.FullName,
                p.Value.Target, p.Value.TriggerKind, p.Value.IsDefault, p.Value.Explicit });
    }
    private static async Task<WorkflowExecutionResult> Advance(ServiceProvider services, WorkflowExecutionResult result)
    {
        ((ParityClock)services.GetRequiredService<TimeProvider>()).Advance(TimeSpan.FromMinutes(1));
        using var recovery = new WorkflowRecoveryService(services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<IWorkflowStateRepository>(), NullLogger<WorkflowRecoveryService>.Instance,
            services.GetRequiredService<TimeProvider>());
        await recovery.RecoverOnceAsync(CancellationToken.None);
        return await services.GetRequiredService<IWorkflowClient>().AwaitAsync(result.InstanceId, WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(10));
    }
}

public sealed class GraphProbe
{
    public int Attempts;
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
public sealed class GraphAttempt(GraphProbe probe) : IAsyncActivity
{
    public int Value { get; set; }
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    { Value = Interlocked.Increment(ref probe.Attempts); return Task.CompletedTask; }
}
public sealed class GraphBlocked(GraphProbe probe) : IAsyncActivity
{
    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        probe.Started.TrySetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }
}
