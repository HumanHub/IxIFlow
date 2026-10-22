using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Threading.Channels;
using IxIFlow.Debugging;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "control-flow"), Trait("Spec", "F-17")]
public sealed class InlineDebugTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);
    private const string Token = "inline-debug-session-token-at-least-32-bytes";

    [Theory]
    [InlineData(DebugStepKind.Into), InlineData(DebugStepKind.Over)]
    public async Task ArmedStateRearmReturnsSuspendedWhenAnotherHostAppliesContinueBeforeTheRequestReturns(DebugStepKind kind)
    {
        var store = new InMemoryWorkflowStateRepository();
        var catalog = new WorkflowCatalog();
        var proxy = System.Reflection.DispatchProxy.Create<IWorkflowStateRepository, DebugWakeRepository>();
        var requests = (DebugWakeRepository)(object)proxy;
        requests.Inner = store;
        await using var first = DebugServices(proxy, catalog);
        await using var second = DebugServices(store, catalog);
        var client = first.GetRequiredService<IWorkflowClient>();
        var ops = first.GetRequiredService<IWorkflowOperations>();
        var definition = new WorkflowBuilder<Data, object>([], "InlineReviewArmedState" + kind).WaitFor<Signal>("start")
            .StateMachine(m => m.Start("a").State("a", s => s.On<Signal>("pulse", t => t.When(c => false, "end"))))
            .WithId("machine").Build();
        var idle = await client.RunAsync(definition, new Data());
        await ops.SetBreakpointsAsync(idle.InstanceId, [new("arm", "machine/a/arm")], "set");
        Assert.Equal(WorkflowExecutionStatus.Paused, (await client.ResumeAsync(idle.InstanceId, "start", new Signal())).Status);
        await ops.SetBreakpointsAsync(idle.InstanceId, [], "clear");
        var thread = (await ops.InspectAsync(idle.InstanceId))!.Threads[0];
        Assert.Equal(WorkflowExecutionStatus.Paused, (await ops.StepAsync(idle.InstanceId, thread.ContinuationId, kind, "arm")).Status);
        Assert.Equal("Blocked", (await ops.InspectAsync(idle.InstanceId))!.DebugState.Stop!.Reason);
        Assert.Equal(EventAppendStatus.Stored, (await store.AppendEventAsync(new(idle.InstanceId, "pulse",
            WorkflowTypeIdentity.SavedName(typeof(Signal)), System.Text.Json.JsonSerializer.Serialize(new Signal()), "pulse"))).Status);
        Assert.Equal(WorkflowExecutionStatus.Paused, (await ops.StepAsync(idle.InstanceId, thread.ContinuationId, kind, "fire")).Status);
        var fired = (await ops.InspectAsync(idle.InstanceId))!;
        Assert.Equal(2, fired.DebugState.Stop!.ArmOccurrence);
        Assert.NotNull(ExecutionCheckpoint.Read(StoredCheckpoint.WholeState((await store.GetWorkflowInstanceAsync(idle.InstanceId))!))
            .Continuations[0].AcceptedWait);
        Assert.Equal(WorkflowExecutionStatus.Paused, (await ops.StepAsync(idle.InstanceId, thread.ContinuationId, kind, "guard")).Status);
        Assert.Equal(3, (await ops.InspectAsync(idle.InstanceId))!.DebugState.Stop!.ArmOccurrence);
        requests.AfterContinueAccepted = async id => Assert.Equal(WorkflowExecutionStatus.Suspended,
            (await second.GetRequiredService<IWorkflowClient>().RecoverAsync(id)).Status);
        var result = await ops.ContinueAsync(idle.InstanceId, "rearm").WaitAsync(Patience);
        Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
        Assert.True(requests.Woke);
        var commands = (await store.GetDebugStatesAsync([idle.InstanceId]))[idle.InstanceId].Commands;
        Assert.All(commands, c => Assert.NotNull(c.AcknowledgedRevision));
        var trace = await ops.GetTraceAsync(idle.InstanceId);
        var replay = TraceReplay.Load(definition, trace);
        var arms = trace.Where(t => t.Entry.EntryType == TraceEntryType.StepEntered && t.Entry.StepId == "root/1/state/a/1").ToArray();
        Assert.Equal(3, arms.Length);
        for (var index = 0; index < arms.Length; index++)
        {
            replay.Seek(arms[index].Sequence);
            Assert.Equal(index + 1, replay.Current.Threads.Single().Frames[^1].ArmOccurrence);
        }
    }

    private static ServiceProvider DebugServices(IWorkflowStateRepository store, IWorkflowCatalog catalog)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow(o => o.Debugging = true);
        services.AddSingleton(store);
        services.AddSingleton(catalog);
        return services.BuildServiceProvider();
    }

    public class DebugWakeRepository : System.Reflection.DispatchProxy
    {
        internal IWorkflowStateRepository Inner { get; set; } = null!;
        internal Func<string, Task>? AfterContinueAccepted { get; set; }
        internal bool Woke { get; private set; }
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IWorkflowStateRepository.RequestDebugCommandAsync))
                return Request((string)args![0]!, (DebugCommandRequest)args[1]!);
            try { return method.Invoke(Inner, args); }
            catch (System.Reflection.TargetInvocationException error) when (error.InnerException != null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
        private async Task<DebugCommandResult> Request(string id, DebugCommandRequest request)
        {
            var result = await Inner.RequestDebugCommandAsync(id, request);
            if (result.Status == DebugCommandStatus.Accepted && request.Kind == DebugCommandKind.Continue &&
                AfterContinueAccepted is { } wake)
            {
                AfterContinueAccepted = null;
                await wake(id);
                Woke = true;
            }
            return result;
        }
    }

    [Fact]
    public async Task AdapterUsesPhysicalCalleeSourceForDirectAndNestedOccurrences()
    {
        await using var services = InlineRuntimeTests.Services();
        var client = services.GetRequiredService<IWorkflowClient>();
        var operations = services.GetRequiredService<IWorkflowOperations>();
        var definition = Workflow.Create<Caller>("InlinePhysicalSources").Step<Noop>().WaitFor<Signal>("start")
            .Use<PhysicalBody, Data>().WithId("direct")
            .Use<NestedPhysical, Data>().WithId("nested")
            .Use<PhysicalBody, Data>().WithId("last").Build();
        var sites = definition.StepTable.Where(s => s.AuthorId.EndsWith("/physical", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, sites.Length);
        var location = Assert.Single(sites.Select(s => s.SourceLocation).Distinct())!;
        var separator = location.LastIndexOf(':');
        var path = location[..separator];
        var line = int.Parse(location[(separator + 1)..]);
        Assert.True(File.Exists(path));
        var idle = await client.RunAsync(definition, new Caller());
        await using var dap = new Client(operations);
        await dap.Attach(idle.InstanceId);
        var sources = (JArray)(await dap.Ok("loadedSources"))["body"]!["sources"]!;
        var source = Assert.Single(sources, s => string.Equals(s.Value<string>("path")!.Replace('\\', '/'), path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
        var content = await dap.Ok("source", new { sourceReference = source.Value<int>("sourceReference") });
        Assert.Contains("class PhysicalBody", content["body"]!.Value<string>("content"));
        var breakpoints = await dap.Ok("setBreakpoints", new { source = new { path }, breakpoints = new[] { new { line } } });
        Assert.True(breakpoints["body"]!["breakpoints"]![0]!.Value<bool>("verified"));
        var result = await client.ResumeAsync(idle.InstanceId, "start", new Signal());
        foreach (var author in new[] { "direct/physical", "nested/body/physical", "last/physical" })
        {
            Assert.Equal(WorkflowExecutionStatus.Paused, result.Status);
            await dap.Event("stopped");
            Assert.Equal(author, (await operations.InspectAsync(idle.InstanceId))!.Threads[0].AuthorStepId);
            var top = await dap.Top();
            Assert.Equal(path.Replace('\\', '/'), top["source"]!.Value<string>("path")!.Replace('\\', '/'));
            Assert.Equal(line, top.Value<int>("line"));
            result = await operations.ContinueAsync(idle.InstanceId, Guid.NewGuid().ToString("N"));
        }
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
    }

    public sealed class Signal { }
    [Workflow("PhysicalBody")] public sealed class PhysicalBody : IWorkflow<Data>
    { public void Build(IWorkflowBuilder<Data> b) => b.Step<Noop>().WithId("physical"); }
    [Workflow("NestedPhysical")] public sealed class NestedPhysical : IWorkflow<Data>
    { public void Build(IWorkflowBuilder<Data> b) => b.Use<PhysicalBody, Data>().WithId("body"); }

    [Fact]
    public void SourceLineMapsToEveryGraftSiteAndFunctionIdsAreQualified()
    {
        var definition = Workflow.Create<Caller>("Sources").Use<Body, Data>().WithId("first")
            .Use<Body, Data>().WithId("second").Build();
        WorkflowDefinitionValidator.Validate(definition);
        var sites = BreakpointResolver.AtSourceSites(definition.StepTable, "workflow.yaml", 12);
        Assert.Equal(new[] { "first/work", "second/work" }, sites.Select(s => s.AuthorId));
        var resolved = BreakpointResolver.Resolve(definition, [new("one", "first/work"), new("two", "second/work")]);
        Assert.Equal(new[] { "root/0/inline/0", "root/1/inline/0" }, resolved.Select(b => b.StepId));
        Assert.Equal(typeof(Data), BreakpointConditionInputs.Request(definition, sites[0].CatalogId, "context.WorkflowData.Value == 3").DataType);
    }

    [Fact]
    public void OutLeavesInlineBeforeItsEnclosingGraphNode()
    {
        var root = new ScopePosition { ActivationId = "root", ScopeId = "root" };
        var node = new ScopePosition { Kind = FrameKind.Node, ActivationId = "node", ScopeId = "node" };
        var inline = new ScopePosition { Kind = FrameKind.Inline, ActivationId = "inline", ScopeId = "inline" };
        var branch = new ContinuationState { Id = "main", Stack = [root, node, inline] };
        var checkpoint = new ExecutionCheckpoint { Continuations = [branch] };
        var stepping = SteppingPolicy.Start(checkpoint, new("out", 1, DebugCommandKind.Step, 1, "test",
            ContinuationId: branch.Id, StepKind: DebugStepKind.Out));
        Assert.Equal("inline", stepping.Origin.ActivationId);
        checkpoint.Debug = checkpoint.Debug with { Stepping = stepping };
        branch.Stack.Remove(inline);
        Assert.True(SteppingPolicy.StopAtPick(checkpoint, branch, "after", DateTime.UtcNow));
        Assert.Contains(node, branch.Stack);
    }

    public sealed class Caller { public string CallerOnly { get; set; } = ""; }
    public sealed class Data { public int Value { get; set; } }
    public sealed class Noop : IAsyncActivity { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken) => Task.CompletedTask; }
    [Workflow("Body")] public sealed class Body : IWorkflow<Data>
    { public void Build(IWorkflowBuilder<Data> b) => b.Step<Noop>().WithId("work", "workflow.yaml", 12); }
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
        public async Task Attach(string id)
        {
            await Ok("initialize", new { linesStartAt1 = true, columnsStartAt1 = true });
            await Ok("attach", new { instanceId = id, sessionToken = Token });
            await Event("initialized"); await Ok("configurationDone");
        }
        public async Task<int> Thread() => (await Ok("threads"))["body"]!["threads"]![0]!.Value<int>("id");
        public async Task<JToken> Top() => (await Ok("stackTrace", new { threadId = await Thread() }))["body"]!["stackFrames"]![0]!;
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
