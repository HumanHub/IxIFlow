using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using IxIFlow.Authoring;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Debugging;
using IxIFlow.Extensions;
using IxIFlow.Generators;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Newtonsoft.Json.Linq;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability"), Trait("Spec", "D-50")]
public sealed class GraphDebugAdapterTests
{
    private const string Token = "graph-debugger-application-session-token-for-tests";
    private const string BuildPath = "C:/build/graphs/tour.cs";
    private const string LocalPath = "C:/checkout/graphs/tour.cs";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task FlowchartVisitNamesLocationsAndReadOnlyGraphScopeSurviveReplay()
    {
        await using var services = Services();
        var operations = services.GetRequiredService<IWorkflowOperations>();
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = new WorkflowBuilder<Data, object>([], "GraphDapVisits").WaitFor<Signal>("start")
            .Flowchart(f => f.Start("route").Node("route", n => n.Step<Increment>(), e => e
                .When(c => c.WorkflowData.Value < 2, "route", sourcePath: BuildPath, sourceLine: 40)
                .Otherwise("end", sourcePath: BuildPath, sourceLine: 41), sourcePath: BuildPath, sourceLine: 20)
                .Named("Route"), sourcePath: BuildPath, sourceLine: 10).WithId("chart").Named("routing").Build();
        var result = await client.RunAsync(definition, new Data());
        await using var dap = new Client(operations);
        await dap.Attach(result.InstanceId);
        await dap.Ok("setBreakpoints", new { source = new { path = LocalPath },
            breakpoints = new[] { new { line = 20, hitCondition = "== 2" } } });
        await client.ResumeAsync(result.InstanceId, "start", new Signal());
        await dap.Event("stopped");
        var frame = await dap.Top();
        Assert.Equal("routing / Route (visit 2)", frame.Value<string>("name"));
        AssertLocation(frame, 20);
        var graph = await dap.Graph(frame.Value<int>("id"));
        Assert.Equal("\"route\"", graph["node"]!.Value<string>("value"));
        Assert.Equal("2", graph["visit"]!.Value<string>("value"));
        Assert.Equal("\"root/1/node/route/to/0/0\"", graph["via"]!.Value<string>("value"));
        Assert.All(graph.Values, v => Assert.Contains("readOnly", v["presentationHint"]!["attributes"]!.Values<string>()));
        var oldFrame = frame.Value<int>("id");
        Assert.False((await dap.Request("setExpression", new { frameId = oldFrame, expression = "graph.node", value = "\"other\"" })).Value<bool>("success"));
        await dap.Ok("stepIn", new { threadId = await dap.Thread() }); await dap.Event("stopped");
        Assert.False((await dap.Request("scopes", new { frameId = oldFrame })).Value<bool>("success"));
        await dap.Ok("reverseContinue", new { threadId = await dap.Thread() }); await dap.Event("stopped");
        frame = await dap.Top();
        Assert.Equal("routing / Route (visit 2)", frame.Value<string>("name"));
        AssertLocation(frame, 20);
        graph = await dap.Graph(frame.Value<int>("id"));
        Assert.Equal("2", graph["visit"]!.Value<string>("value"));
        Assert.Equal("\"route\"", (await dap.Ok("evaluate", new { frameId = frame.Value<int>("id"), expression = "graph.node" }))["body"]!.Value<string>("result"));
        await dap.Ok("disconnect");
    }

    [Fact]
    public async Task StateExitTriggerAndCaseFramesKeepTheirNamesSourceAndPendingChoice()
    {
        await using var services = Services();
        var operations = services.GetRequiredService<IWorkflowOperations>();
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = new WorkflowBuilder<Data, object>([], "GraphDapPhases").WaitFor<Signal>("start")
            .StateMachine(m => m.Start("pending").State("pending", s => s
                .Exit(e => e, sourcePath: BuildPath, sourceLine: 30)
                .Immediately(t => t.Otherwise("done", a => a.Step<Increment>(), sourcePath: BuildPath, sourceLine: 50),
                    sourcePath: BuildPath, sourceLine: 40), sourcePath: BuildPath, sourceLine: 20)
                .State("done", s => s.Final(), sourcePath: BuildPath, sourceLine: 60),
                sourcePath: BuildPath, sourceLine: 10).WithId("machine").Named("approval").Build();
        var result = await client.RunAsync(definition, new Data());
        await using var dap = new Client(operations); await dap.Attach(result.InstanceId);
        await dap.Ok("setBreakpoints", new { source = new { path = LocalPath },
            breakpoints = new[] { 20, 30, 40, 50 }.Select(line => new { line }).ToArray() });
        await client.ResumeAsync(result.InstanceId, "start", new Signal());
        foreach (var (line, name, phase) in new[] { (20, "pending (visit 1)", "Running"),
            (30, "pending / exit", "Exiting"), (40, "pending / on 0", "TransitionChosen"),
            (50, "pending / on 0 / case 0", "Action") })
        {
            await dap.Event("stopped");
            var frame = await dap.Top();
            Assert.Equal("approval / " + name, frame.Value<string>("name")); AssertLocation(frame, line);
            var graph = await dap.Graph(frame.Value<int>("id"));
            Assert.Equal(JsonSerializer.Serialize(phase), graph["phase"]!.Value<string>("value"));
            if (line > 20)
            {
                var pending = await dap.Variables(graph["pending"]!.Value<int>("variablesReference"));
                Assert.Equal("\"done\"", pending["Target"]!.Value<string>("value"));
                Assert.Equal("\"Immediate\"", pending["Cause"]!.Value<string>("value"));
                Assert.Equal("\"root/1/state/pending/on/0/0\"", pending["Trigger"]!.Value<string>("value"));
                Assert.DoesNotContain("Event", pending.Keys);
            }
            if (line < 50) await dap.Ok("continue", new { threadId = await dap.Thread() });
        }
        await dap.Ok("stepIn", new { threadId = await dap.Thread() }); await dap.Event("stopped");
        await dap.Ok("reverseContinue", new { threadId = await dap.Thread() }); await dap.Event("stopped");
        var historical = await dap.Top();
        Assert.Equal("approval / pending / on 0 / case 0", historical.Value<string>("name"));
        AssertLocation(historical, 50);
        Assert.Equal("{…}", (await dap.Graph(historical.Value<int>("id")))["pending"]!.Value<string>("value"));
        await dap.Ok("disconnect");
    }

    [Fact]
    public async Task ArmedStateShowsItsArmLocationOccurrenceAndUnselectedChoice()
    {
        await using var services = Services();
        var operations = services.GetRequiredService<IWorkflowOperations>();
        var definition = Workflow.Create<Data>("GraphDapArmed").StateMachine(m => m.Start("pending")
            .State("pending", s => s.On<Signal>("go", "end", sourcePath: BuildPath, sourceLine: 40),
                sourcePath: BuildPath, sourceLine: 20)).Named("approval").Build();
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        await operations.PauseAsync(result.InstanceId, "test");
        await using var dap = new Client(operations); await dap.Attach(result.InstanceId); await dap.Event("stopped");
        var frame = await dap.Top();
        Assert.Equal("approval / pending (visit 1)", frame.Value<string>("name")); AssertLocation(frame, 20);
        var graph = await dap.Graph(frame.Value<int>("id"));
        Assert.Equal("\"Armed\"", graph["phase"]!.Value<string>("value"));
        Assert.Equal("1", graph["armOccurrence"]!.Value<string>("value"));
        Assert.Equal("null", graph["pending"]!.Value<string>("value"));
        await dap.Ok("disconnect");
    }

    [Fact]
    public async Task NestedGraphScopesFollowTheSelectedFrame()
    {
        await using var services = Services();
        var operations = services.GetRequiredService<IWorkflowOperations>();
        var definition = Workflow.Create<Data>("NestedGraphDap").Flowchart(f => f.Start("outer")
            .Node("outer", n => n.StateMachine(m => m.Start("pending").State("pending", s => s.On<Signal>("go", "end")))
                .Named("approval"))).Named("routing").Build();
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        await operations.PauseAsync(result.InstanceId, "test");
        await using var dap = new Client(operations); await dap.Attach(result.InstanceId); await dap.Event("stopped");
        var frames = (await dap.Ok("stackTrace", new { threadId = await dap.Thread() }))["body"]!["stackFrames"]!;
        var inner = frames.Single(f => f.Value<string>("name") == "approval / pending (visit 1)").Value<int>("id");
        var outer = frames.Single(f => f.Value<string>("name") == "routing / outer (visit 1)").Value<int>("id");
        Assert.Equal("\"pending\"", (await dap.Graph(inner))["node"]!.Value<string>("value"));
        Assert.Equal("\"outer\"", (await dap.Graph(outer))["node"]!.Value<string>("value"));
        Assert.Equal("\"outer\"", (await dap.Ok("evaluate", new { frameId = outer, expression = "graph.node" }))["body"]!.Value<string>("result"));
        await dap.Ok("disconnect");
    }

    [Theory]
    [InlineData(false, false, false)] [InlineData(false, false, true)]
    [InlineData(false, true, false)] [InlineData(false, true, true)]
    [InlineData(true, false, false)] [InlineData(true, false, true)]
    [InlineData(true, true, false)] [InlineData(true, true, true)]
    public async Task OrdinaryForksInGraphsKeepTheirScopeAcrossParentThreads(bool stateMachine, bool items, bool replay)
    {
        await using var services = Services();
        var operations = services.GetRequiredService<IWorkflowOperations>();
        var client = services.GetRequiredService<IWorkflowClient>();
        IWorkflowBuilder<Data, Signal> Fork(ISequenceBuilder<Data, Signal> body) => body.Parallel(p => p.Do(branch =>
        {
            // Two fork levels require following more than the immediate parent continuation.
            if (items) branch.ForEach(_ => new[] { 1, 2 }, (item, _) => item.Step<Noop>()
                .WithId("branch-work", sourcePath: BuildPath, sourceLine: 70), setup => setup.Parallel());
            else branch.Parallel(inner => inner
                .Do(b => b.Step<Noop>().WithId("branch-work", sourcePath: BuildPath, sourceLine: 70))
                .Do(b => b.Step<Noop>().WithId("other-work", sourcePath: BuildPath, sourceLine: 80)));
        }));
        var builder = new WorkflowBuilder<Data, object>([], "GraphDapOrdinaryForks").WaitFor<Signal>("start");
        var definition = (stateMachine
            ? builder.StateMachine(m => m.Start("work").State("work", s => s.Entry(Fork).Final()))
            : builder.Flowchart(f => f.Start("work").Node("work", Fork)))
            .WaitFor<Signal>("tail").Build();
        var result = await client.RunAsync(definition, new Data());
        await using var dap = new Client(operations); await dap.Attach(result.InstanceId);
        var breakpoints = new { source = new { path = LocalPath }, breakpoints = new[] { new { line = 70 } } };
        await dap.Ok("setBreakpoints", breakpoints);
        await client.ResumeAsync(result.InstanceId, "start", new Signal()); await dap.Event("stopped");
        if (replay)
        {
            await dap.Ok("setBreakpoints", new { source = new { path = LocalPath }, breakpoints = Array.Empty<object>() });
            await dap.Ok("continue", new { threadId = await dap.Thread() });
            using var timeout = new CancellationTokenSource(Patience);
            while ((await operations.InspectAsync(result.InstanceId))!.Summary.Status != WorkflowStatus.Suspended)
                await Task.Delay(20, timeout.Token);
            await operations.PauseAsync(result.InstanceId, "test"); await dap.Event("stopped");
            Assert.All((await operations.InspectAsync(result.InstanceId))!.Threads,
                t => Assert.All(t.Frames, f => Assert.Null(f.GraphId)));
            await dap.Ok("setBreakpoints", breakpoints);
            await dap.Ok("reverseContinue", new { threadId = await dap.Thread() }); await dap.Event("stopped");
        }
        var threads = (await dap.Ok("threads"))["body"]!["threads"]!;
        var branches = threads.Where(t => t.Value<string>("name")!.StartsWith("Branch ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, branches.Length);
        foreach (var thread in branches)
        {
            var frames = (await dap.Ok("stackTrace", new { threadId = thread.Value<int>("id") }))["body"]!["stackFrames"]!;
            foreach (var frame in frames)
            {
                var frameId = frame.Value<int>("id");
                var graph = await dap.Graph(frameId);
                Assert.Equal("\"root/1\"", graph["id"].Value<string>("value"));
                Assert.Equal("\"work\"", graph["node"].Value<string>("value"));
                Assert.Equal("1", graph["visit"].Value<string>("value"));
                Assert.Equal("\"work\"", (await dap.Ok("evaluate", new { frameId, expression = "graph.node" }))["body"]!.Value<string>("result"));
            }
        }
        var root = threads.Single(t => t.Value<string>("name")!.StartsWith("Root", StringComparison.Ordinal));
        var outside = (await dap.Ok("stackTrace", new { threadId = root.Value<int>("id") }))["body"]!["stackFrames"]!.Last()!.Value<int>("id");
        Assert.DoesNotContain((await dap.Ok("scopes", new { frameId = outside }))["body"]!["scopes"]!, s => s.Value<string>("name") == "Graph");
        await dap.Ok("disconnect");
    }

    [Fact]
    public async Task ForkedNestedGraphScopesPreferLocalAncestorsOfTheSelectedFrame()
    {
        await using var services = Services();
        var operations = services.GetRequiredService<IWorkflowOperations>();
        var definition = Workflow.Create<Data>("ForkedNestedGraphDap").Flowchart(f => f.Start("outer").Node("outer", n => n
            .Parallel(p => p.Do(b => b.Sequence(sequence => sequence.StateMachine(m => m.Start("inner")
                .State("inner", s => s.On<Signal>("hold", "end")))))))).Build();
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        await operations.PauseAsync(result.InstanceId, "test");
        await using var dap = new Client(operations); await dap.Attach(result.InstanceId); await dap.Event("stopped");
        var thread = (await dap.Ok("threads"))["body"]!["threads"]!.Single(t => t.Value<string>("name")!.StartsWith("Branch ", StringComparison.Ordinal));
        var frames = (await dap.Ok("stackTrace", new { threadId = thread.Value<int>("id") }))["body"]!["stackFrames"]!.ToArray();
        Assert.Equal(3, frames.Length);
        for (var index = 0; index < frames.Length; index++)
        {
            var frameId = frames[index].Value<int>("id");
            var expected = index == 0 ? "\"inner\"" : "\"outer\"";
            Assert.Equal(expected, (await dap.Graph(frameId))["node"].Value<string>("value"));
            Assert.Equal(expected, (await dap.Ok("evaluate", new { frameId, expression = "graph.node" }))["body"]!.Value<string>("result"));
        }
        await dap.Ok("disconnect");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FinishedGraphFrameKeepsItsOwnPickLocationAndMissingLocationsAreSubtle(bool missing)
    {
        await using var services = Services();
        var operations = services.GetRequiredService<IWorkflowOperations>();
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(
            new WorkflowBuilder<Data, object>([], "FinishedGraphFrameDap").WaitFor<Signal>("go").Build(), new Data());
        await operations.PauseAsync(result.InstanceId, "test");
        var inspection = (await operations.InspectAsync(result.InstanceId))!;
        var edge = new InspectionFrame("edge", "routing / route / to 0", "Edge", "root/0/node/route/to/0", null, 1, 0, null)
            { Phase = "EdgeChosen", Visit = 2, GraphId = "root/0", Node = "route" };
        inspection = inspection with { Threads = [inspection.Threads.Single() with { Frames = [edge] }] };
        var saved = new Mock<IWorkflowOperations>();
        saved.Setup(o => o.GetAsync(result.InstanceId, It.IsAny<CancellationToken>())).ReturnsAsync(await operations.GetAsync(result.InstanceId));
        saved.Setup(o => o.InspectAsync(result.InstanceId, It.IsAny<CancellationToken>())).ReturnsAsync(inspection);
        saved.Setup(o => o.GetTraceAsync(result.InstanceId, It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<StoredTraceEntry>());
        saved.Setup(o => o.GetStepTableAsync(result.InstanceId, It.IsAny<CancellationToken>())).ReturnsAsync(
        [
            new("root/0", "chart", "routing", WorkflowStepType.Flowchart, "root", BuildPath + ":10"),
            new(edge.ScopeId + "/0", "chart/route/to/0", "Edge", WorkflowStepType.Edge, edge.ScopeId, missing ? null : BuildPath + ":40")
        ]);
        await using var dap = new Client(saved.Object); await dap.Attach(result.InstanceId); await dap.Event("stopped");
        var frame = await dap.Top(); Assert.Equal(edge.Name, frame.Value<string>("name"));
        if (missing)
        {
            Assert.Equal("subtle", frame.Value<string>("presentationHint"));
            Assert.True(frame["source"] == null || frame["source"]!.Type == JTokenType.Null);
        }
        else AssertLocation(frame, 40);
        await dap.Ok("disconnect");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task YamlAndFluentLinesResolveToTheSameGraphPicks(bool yaml)
    {
        const string source = """
            workflow: {name: GraphDapSources, dataType: Data}
            definitions:
              - kind: flowchart
                start: route
                nodes:
                  - key: route
                    transitions:
                      - to: end
              - kind: stateMachine
                start: pending
                states:
                  - key: pending
                    transitions:
                      - on: go
                        event: Signal
                        cases:
                          - to: end
            """;
        var options = new WorkflowSourceOptions();
        options.Types["Data"] = "global::IxIFlow.Tests.ExecutionTests.GraphDebugAdapterTests.Data";
        options.Types["Signal"] = "global::IxIFlow.Tests.ExecutionTests.GraphDebugAdapterTests.Signal";
        var definition = yaml ? WorkflowCompilation.Build(WorkflowSourceTranslator.Translate(source, BuildPath, options)) :
            Workflow.Create<Data>("GraphDapSources").Flowchart(f => f.Start("route")
                .Node("route", e => e.Always("end", sourcePath: BuildPath, sourceLine: 8), sourcePath: BuildPath, sourceLine: 6))
                .StateMachine(m => m.Start("pending").State("pending", s => s
                    .On<Signal>("go", t => t.Otherwise("end", sourcePath: BuildPath, sourceLine: 17), sourcePath: BuildPath, sourceLine: 14),
                    sourcePath: BuildPath, sourceLine: 12)).Build();
        var expected = new Dictionary<int, string> { [6] = "root/0/node/route/0", [8] = "root/0/node/route/to/0/0",
            [12] = "root/1/state/pending/0", [14] = "root/1/state/pending/on/0/0", [17] = "root/1/state/pending/on/0/case/0/0" };
        await using var services = Services();
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        var operations = services.GetRequiredService<IWorkflowOperations>();
        await using var dap = new Client(operations); await dap.Attach(result.InstanceId, zeroBased: true);
        var response = await dap.Ok("setBreakpoints", new { source = new { path = LocalPath },
            breakpoints = expected.Keys.Select(line => new { line = line - 1 }).ToArray() });
        Assert.Equal(expected.Keys.Select(line => line - 1), response["body"]!["breakpoints"]!.Select(b => b.Value<int>("line")));
        Assert.Equal(expected.Values, (await operations.GetBreakpointsAsync(result.InstanceId)).Select(b => b.StepId));
        Assert.All(BreakpointResolver.Resolve(definition, expected.Select(p => new Breakpoint(p.Key.ToString(), Source: new(BuildPath, p.Key))).ToArray()),
            b => Assert.Equal(expected[b.Source!.Line], b.StepId));
        var locations = await dap.Ok("breakpointLocations", new { source = new { path = LocalPath }, line = 0, endLine = 16 });
        Assert.All(expected.Keys, line => Assert.Contains(locations["body"]!["breakpoints"]!, b => b.Value<int>("line") == line - 1));
        await operations.PauseAsync(result.InstanceId, "test"); await dap.Event("stopped");
        var frame = await dap.Top(); Assert.Equal(11, frame.Value<int>("line")); Assert.Equal(0, frame.Value<int>("column"));
        await dap.Ok("disconnect");
    }

    private static void AssertLocation(JToken frame, int line)
    {
        Assert.Equal(line, frame.Value<int>("line")); Assert.Equal(1, frame.Value<int>("column"));
        Assert.Equal(LocalPath, frame["source"]!.Value<string>("path"));
        Assert.Equal("normal", frame.Value<string>("presentationHint"));
    }
    private static ServiceProvider Services()
    {
        var services = new ServiceCollection().AddLogging(); services.AddIxIFlow(o => o.Debugging = true);
        return services.BuildServiceProvider();
    }
    public sealed class Data { public int Value { get; set; } }
    public sealed class Signal;
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public sealed class Increment : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { ((Data)context.WorkflowData).Value++; return Task.CompletedTask; }
    }

    // Exercise the production framed protocol without sockets or child processes.
    private sealed class Client : IAsyncDisposable
    {
        private readonly Pipe _toServer = new(), _toClient = new();
        private readonly DebugAdapterSession _session;
        private readonly Stream _input, _output;
        private readonly Task _server, _pump;
        private readonly Channel<JObject> _messages = Channel.CreateUnbounded<JObject>();
        private readonly List<JObject> _pending = [];
        private int _sequence;
        public Client(IWorkflowOperations operations)
        {
            var options = new DebuggingOptions(); options.Tcp(IPAddress.Loopback, 0, Token, "test");
            options.Authorize = (_, _) => ValueTask.FromResult(true);
            _session = new(operations, options);
            _input = _toClient.Reader.AsStream(); _output = _toServer.Writer.AsStream();
            _server = _session.RunAsync(_toServer.Reader.AsStream(), _toClient.Writer.AsStream()); _pump = Pump();
        }
        public async Task<JObject> Request(string command, object? args = null)
        {
            var sequence = ++_sequence;
            var json = JObject.FromObject(new { seq = sequence, type = "request", command, arguments = args ?? new { } }).ToString(Newtonsoft.Json.Formatting.None);
            var body = Encoding.UTF8.GetBytes(json);
            await _output.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
            await _output.WriteAsync(body); await _output.FlushAsync();
            return await Read(m => m.Value<string>("type") == "response" && m.Value<int>("request_seq") == sequence);
        }
        public async Task<JObject> Ok(string command, object? args = null)
        { var response = await Request(command, args); Assert.True(response.Value<bool>("success"), response.ToString()); return response; }
        public Task<JObject> Event(string name) => Read(m => m.Value<string>("event") == name);
        private async Task<JObject> Read(Func<JObject, bool> predicate)
        {
            var index = _pending.FindIndex(m => predicate(m));
            if (index >= 0) { var found = _pending[index]; _pending.RemoveAt(index); return found; }
            while (true)
            {
                var message = await _messages.Reader.ReadAsync().AsTask().WaitAsync(Patience);
                if (predicate(message)) return message;
                _pending.Add(message);
            }
        }
        public async Task Attach(string id, bool zeroBased = false)
        {
            await Ok("initialize", new { linesStartAt1 = !zeroBased, columnsStartAt1 = !zeroBased });
            await Ok("attach", new { instanceId = id, sessionToken = Token,
                sourceRoots = new Dictionary<string, string> { ["C:/build/graphs"] = "C:/checkout/graphs" } });
            await Event("initialized"); await Ok("configurationDone");
        }
        public async Task<int> Thread() => (await Ok("threads"))["body"]!["threads"]![0]!.Value<int>("id");
        public async Task<JToken> Top() => (await Ok("stackTrace", new { threadId = await Thread() }))["body"]!["stackFrames"]![0]!;
        public async Task<Dictionary<string, JToken>> Graph(int frameId)
        {
            var scopes = (await Ok("scopes", new { frameId }))["body"]!["scopes"]!;
            return await Variables(scopes.Single(s => s.Value<string>("name") == "Graph").Value<int>("variablesReference"));
        }
        public async Task<Dictionary<string, JToken>> Variables(int reference) =>
            (await Ok("variables", new { variablesReference = reference }))["body"]!["variables"]!.ToDictionary(v => v.Value<string>("name")!);
        private async Task Pump()
        {
            try
            {
                var one = new byte[1];
                while (true)
                {
                    var header = new StringBuilder();
                    while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    {
                        if (await _input.ReadAsync(one) == 0) return;
                        header.Append((char)one[0]);
                    }
                    var length = int.Parse(header.ToString().Split("\r\n").Single(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1]);
                    var body = new byte[length]; await _input.ReadExactlyAsync(body);
                    await _messages.Writer.WriteAsync(JObject.Parse(Encoding.UTF8.GetString(body)));
                }
            }
            catch (ObjectDisposedException) { }
            finally { _messages.Writer.TryComplete(); }
        }
        public async ValueTask DisposeAsync()
        {
            _session.Dispose(); _output.Dispose(); await _server.WaitAsync(Patience); await _pump.WaitAsync(Patience); _input.Dispose();
        }
    }
}
