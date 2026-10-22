using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Threading.Channels;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Debugging;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using static IxIFlow.Tests.ExecutionTests.ThrowRethrowTests;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "tracing")]
[Trait("Spec", "W-05")]
public sealed class ThrowInspectionTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);
    private const string SessionToken = "throw-fault-inspection-session-token-for-tests";

    [Fact]
    public async Task ThrowCatchFaultIsAReadOnlyDapScopeAndReplayValuesRequireTheMatchingCheckpoint()
    {
        var store = new InMemoryWorkflowStateRepository();
        await using var services = Services(store);
        var definition = new WorkflowBuilder<Data, object>([], "ThrowFaultDap").WaitFor<Go>("go")
            .Try(t => t.Throw<BusinessError>(ctx => new("private business message") { Code = 42 }))
            .Catch<BusinessError, CodeFault>(c => c.Step<Echo>(s => s.Input(x => x.Value).From(ctx => ctx.Fault.Code)
                .Output(x => x.Value).To(ctx => ctx.WorkflowData.Result)).WithId("handler")).Build();
        var client = services.GetRequiredService<IWorkflowClient>(); var ops = services.GetRequiredService<IWorkflowOperations>();
        var idle = await client.RunAsync(definition, new Data());
        await ops.SetBreakpointsAsync(idle.InstanceId, [new("handler", "handler")], "test");
        Assert.Equal(WorkflowExecutionStatus.Paused, (await client.ResumeAsync(idle.InstanceId, "go", new Go())).Status);
        var options = new DebuggingOptions(); options.Tcp(IPAddress.Loopback, 0, SessionToken, "developer");
        options.Authorize = (_, _) => ValueTask.FromResult(true);
        await using var dap = new Client(ops, options);
        await dap.Attach(idle.InstanceId);
        var threadId = await dap.Thread();
        var stack = await dap.Ok("stackTrace", new { threadId });
        var frameId = stack["body"]!["stackFrames"]![0]!.Value<int>("id");
        var scopes = await dap.Ok("scopes", new { frameId });
        var fault = scopes["body"]!["scopes"]!.Single(s => s.Value<string>("name") == "Catch fault");
        var variablesReference = fault.Value<int>("variablesReference"); Assert.NotEqual(0, variablesReference);
        var variables = await dap.Ok("variables", new { variablesReference });
        var code = Assert.Single(variables["body"]!["variables"]!);
        Assert.Equal("Code", code.Value<string>("name")); Assert.Equal("42", code.Value<string>("value"));
        Assert.Contains("readOnly", code["presentationHint"]!["attributes"]!.Values<string>());
        var edit = await dap.Request("setVariable", new { variablesReference, name = "Code", value = "99" });
        Assert.False(edit.Value<bool>("success"));
        Assert.DoesNotContain("private business message", variables.ToString());
        var inspection = (await ops.InspectAsync(idle.InstanceId))!;
        var trace = await ops.GetTraceAsync(idle.InstanceId); var replay = TraceReplay.Load(definition, trace);
        replay.Seek(trace[^1].Sequence); Assert.Null(replay.Current.CatchFaults);
        replay.AttachCheckpoint(inspection); Assert.Single(replay.Current.CatchFaults!);
        replay.Seek(trace.First(e => e.Entry.EntryType == TraceEntryType.FaultRaised).Sequence);
        Assert.Null(replay.Current.CatchFaults); Assert.Null(replay.CurrentStepId);
        Assert.DoesNotContain("private business message", System.Text.Json.JsonSerializer.Serialize(inspection));
    }

    private sealed class Client : IAsyncDisposable
    {
        private readonly Pipe _toServer = new(), _toClient = new();
        private readonly DebugAdapterSession _session;
        private readonly Stream _input, _output;
        private readonly Task _server, _pump;
        private readonly Channel<JObject> _messages = Channel.CreateUnbounded<JObject>();
        private readonly List<JObject> _pending = [];
        private int _sequence;
        public System.Collections.Concurrent.ConcurrentQueue<JObject> Messages { get; } = new();
        public Client(IWorkflowOperations operations, DebuggingOptions options)
        {
            _session = new(operations, options);
            _input = _toClient.Reader.AsStream(); _output = _toServer.Writer.AsStream();
            _server = _session.RunAsync(_toServer.Reader.AsStream(), _toClient.Writer.AsStream());
            _pump = Pump();
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
            await Ok("initialize"); await Ok("attach", new { instanceId = id, sessionToken = SessionToken });
            await Event("initialized"); await Ok("configurationDone");
        }
        public async Task<int> Thread() => (await Ok("threads"))["body"]!["threads"]![0]!.Value<int>("id");
        public async Task<int> DataReference(int frameId) => (await Ok("scopes", new { frameId }))["body"]!["scopes"]![0]!.Value<int>("variablesReference");
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
                    var message = JObject.Parse(Encoding.UTF8.GetString(body));
                    Messages.Enqueue(message); await _messages.Writer.WriteAsync(message);
                }
            }
            catch (ObjectDisposedException) { }
            finally { _messages.Writer.TryComplete(); }
        }
        public async ValueTask DisposeAsync()
        {
            _session.Dispose(); _output.Dispose();
            await _server.WaitAsync(Patience); await _pump.WaitAsync(Patience);
            _input.Dispose();
        }
    }
}
