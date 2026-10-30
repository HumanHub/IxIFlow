using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Channels;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Debugging;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json.Linq;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
public sealed class PrereleaseSlice3DebugTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private const string SessionToken = "slice-three-debug-token-at-least-32-bytes";

    private static DebuggingOptions Options()
    {
        var options = new DebuggingOptions { Authorize = (_, _) => ValueTask.FromResult(true) };
        options.Tcp(IPAddress.Loopback, 0, SessionToken, "operator");

        return options;
    }

    [Fact]
    public async Task FailedReplayAttachCanRetryWithTheSameSourceRoots()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow();
        await using var provider = services.BuildServiceProvider();
        var definition = new WorkflowBuilder<Data, object>([], "Slice3Debug").WaitFor<Go>("go").Build();
        var result = await provider.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        await using var dap = new Client(provider.GetRequiredService<IWorkflowOperations>(), Options());
        await dap.Ok("initialize");
        var roots = new Dictionary<string, string> { ["/authored"] = "/local" };

        Assert.False((await dap.Request("attach",
                new { instanceId = result.InstanceId, sessionToken = SessionToken, sourceRoots = roots, replay = true }))
            .Value<bool>("success"));

        await dap.Ok("attach", new { instanceId = result.InstanceId, sessionToken = SessionToken, sourceRoots = roots });
    }

    [Fact]
    public async Task UnauthenticatedSessionClosesEvenWhenThePeerNeverFinishesAHeader()
    {
        var options = Options();
        options.AuthenticationTimeout = TimeSpan.FromMilliseconds(50);
        using var session = new DebugAdapterSession(Mock.Of<IWorkflowOperations>(), options);
        var input = new Pipe();
        var output = new Pipe();
        await input.Writer.WriteAsync(Encoding.ASCII.GetBytes("Content-Length:"));
        var serving = session.RunAsync(input.Reader.AsStream(), output.Writer.AsStream());

        try
        {
            await serving.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            session.Dispose();
            await input.Writer.CompleteAsync();
        }
    }

    [Fact]
    public async Task ListenerPublishesItsAssignedPortAndRefusesSessionsAboveTheCap()
    {
        var options = Options();
        options.MaxConcurrentSessions = 1;
        options.AuthenticationTimeout = TimeSpan.FromSeconds(5);

        using var service =
            new DebugAdapterService(Mock.Of<IWorkflowOperations>(), options.Snapshot(), NullLogger<DebugAdapterService>.Instance);

        await service.StartAsync(default);

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            while (options.BoundTcpEndpoint == null)
                await Task.Delay(10, timeout.Token);

            Assert.True(options.BoundTcpEndpoint.Port > 0);
            using var first = new TcpClient();
            await first.ConnectAsync(options.BoundTcpEndpoint, timeout.Token);
            await first.GetStream().WriteAsync(Encoding.ASCII.GetBytes("Content-Length:"), timeout.Token);
            using var second = new TcpClient();
            await second.ConnectAsync(options.BoundTcpEndpoint, timeout.Token);
            var one = new byte[1];
            Assert.Equal(0, await second.GetStream().ReadAsync(one, timeout.Token));
        }
        finally
        {
            await service.StopAsync(default);
        }

        Assert.Null(options.BoundTcpEndpoint);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(1, 0)]
    public void InvalidSessionLimitsAreRefusedAtRegistration(int sessions, int milliseconds)
    {
        var options = Options();
        options.MaxConcurrentSessions = sessions;
        options.AuthenticationTimeout = TimeSpan.FromMilliseconds(milliseconds);
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Snapshot());
    }

    [Fact]
    public async Task UnexpectedSessionFailureIsLoggedWithItsException()
    {
        var logger = new FailureLogger();
        using var service = new DebugAdapterService(Mock.Of<IWorkflowOperations>(), Options().Snapshot(), logger);
        var method = typeof(DebugAdapterService).GetMethod("ServeAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var error = new IOException("connection disposal failed");
        await ((Task)method.Invoke(service, [new FailingDisposeStream(error), CancellationToken.None])!).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Same(error, logger.Error);
    }

    private sealed class FailingDisposeStream(Exception error) : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing)
                throw error;
        }
    }

    private sealed class FailureLogger : Microsoft.Extensions.Logging.ILogger<DebugAdapterService>
    {
        internal Exception? Error;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id, TState state,
            Exception? error, Func<TState, Exception?, string> formatter) => Error = error;
    }

    public sealed class Data;

    public sealed class Go;

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
            _input = _toClient.Reader.AsStream();
            _output = _toServer.Writer.AsStream();
            _server = _session.RunAsync(_toServer.Reader.AsStream(), _toClient.Writer.AsStream());
            _pump = Pump();
        }

        public async Task<JObject> Request(string command, object? args = null)
        {
            var sequence = ++_sequence;

            var json = JObject.FromObject(new { seq = sequence, type = "request", command, arguments = args ?? new { } })
                .ToString(Newtonsoft.Json.Formatting.None);

            var body = Encoding.UTF8.GetBytes(json);
            await _output.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
            await _output.WriteAsync(body);
            await _output.FlushAsync();

            return await Read(m => m.Value<string>("type") == "response" && m.Value<int>("request_seq") == sequence);
        }

        public async Task<JObject> Ok(string command, object? args = null)
        {
            var response = await Request(command, args);
            Assert.True(response.Value<bool>("success"), response.ToString());

            return response;
        }

        public Task<JObject> Event(string name) => Read(m => m.Value<string>("event") == name);

        private async Task<JObject> Read(Func<JObject, bool> predicate)
        {
            var index = _pending.FindIndex(m => predicate(m));

            if (index >= 0)
            {
                var found = _pending[index];
                _pending.RemoveAt(index);

                return found;
            }

            while (true)
            {
                var message = await _messages.Reader.ReadAsync().AsTask().WaitAsync(Patience);

                if (predicate(message))
                    return message;

                _pending.Add(message);
            }
        }

        public async Task Attach(string id)
        {
            await Ok("initialize");
            await Ok("attach", new { instanceId = id, sessionToken = SessionToken });
            await Event("initialized");
            await Ok("configurationDone");
        }

        public async Task<int> Thread() => (await Ok("threads"))["body"]!["threads"]![0]!.Value<int>("id");

        public async Task<int> DataReference(int frameId) =>
            (await Ok("scopes", new { frameId }))["body"]!["scopes"]![0]!.Value<int>("variablesReference");

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
                        if (await _input.ReadAsync(one) == 0)
                            return;

                        header.Append((char)one[0]);
                    }

                    var length = int.Parse(header.ToString().Split("\r\n")
                        .Single(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1]);

                    var body = new byte[length];
                    await _input.ReadExactlyAsync(body);
                    var message = JObject.Parse(Encoding.UTF8.GetString(body));
                    Messages.Enqueue(message);
                    await _messages.Writer.WriteAsync(message);
                }
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                _messages.Writer.TryComplete();
            }
        }

        public async ValueTask DisposeAsync()
        {
            _session.Dispose();
            _output.Dispose();
            await _server.WaitAsync(Patience);
            await _pump.WaitAsync(Patience);
            _input.Dispose();
        }
    }
}