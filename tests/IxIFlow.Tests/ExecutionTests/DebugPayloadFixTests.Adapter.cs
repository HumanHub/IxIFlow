using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Debugging;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace IxIFlow.Tests.ExecutionTests;

public sealed partial class DebugPayloadFixTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task M_DapRefreshesOnWakeWithASlowerRemotePoll(bool sqlite)
    {
        await using var h = new Harness(sqlite);
        var started = await h.Client.RunAsync(Workflow.Create<Data>("adapter-refresh").Step<Noop>().WaitFor<Go>("go").Build(), new Data());
        var real = h.Services.GetRequiredService<IWorkflowOperations>();
        var inspection = (await real.InspectAsync(started.InstanceId))!;
        var summary = (await real.GetAsync(started.InstanceId))!;
        var calls = 0;
        Action? wake = null;
        var operations = new Mock<IWorkflowOperations>(MockBehavior.Strict);
        operations.Setup(o => o.GetAsync(started.InstanceId, It.IsAny<CancellationToken>())).ReturnsAsync(summary);
        operations.Setup(o => o.InspectAsync(started.InstanceId, It.IsAny<CancellationToken>()))
            .Callback(() => Interlocked.Increment(ref calls)).ReturnsAsync(inspection);
        operations.Setup(o => o.GetStepTableAsync(started.InstanceId, It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<StepTableEntry>());
        operations.Setup(o => o.GetTraceAsync(started.InstanceId, It.IsAny<long>(), 1000, It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<StoredTraceEntry>());
        operations.Setup(o => o.WatchChanges(started.InstanceId, It.IsAny<Action>()))
            .Callback<string, Action>((_, callback) => wake = callback).Returns(new CallbackSubscription());
        var options = new DebuggingOptions { Authorize = (_, _) => ValueTask.FromResult(true) };
        var sessionToken = new string('a', 64);
        options.Tcp(System.Net.IPAddress.Loopback, 0, sessionToken, "owner");
        using var session = new DebugAdapterSession(operations.Object, options);
        var toServer = new Pipe(); var toClient = new Pipe();
        await using var input = toClient.Reader.AsStream();
        await using var output = toServer.Writer.AsStream();
        var server = session.RunAsync(toServer.Reader.AsStream(), toClient.Writer.AsStream());
        try
        {
            await Request(1, "initialize", new { });
            await Request(2, "attach", new { instanceId = started.InstanceId, sessionToken });
            await Request(3, "configurationDone", new { });
            await Task.Delay(100);
            var baseline = Volatile.Read(ref calls);
            await Task.Delay(1100);
            Assert.True(Volatile.Read(ref calls) - baseline <= 1, $"Idle DAP session performed {calls - baseline} full inspections in 1.1 seconds");
            var beforeWake = Volatile.Read(ref calls);
            wake!();
            for (var i = 0; i < 100 && Volatile.Read(ref calls) == beforeWake; i++) await Task.Delay(10);
            Assert.True(Volatile.Read(ref calls) > beforeWake);
        }
        finally { session.Dispose(); await server.WaitAsync(TimeSpan.FromSeconds(5)); }

        async Task Request(int seq, string command, object arguments)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(new { seq, type = "request", command, arguments });
            await output.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
            await output.WriteAsync(body); await output.FlushAsync();
            while (true)
            {
                var header = new StringBuilder(); var one = new byte[1];
                while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                { await input.ReadExactlyAsync(one).AsTask().WaitAsync(TimeSpan.FromSeconds(5)); header.Append((char)one[0]); }
                var length = int.Parse(header.ToString().Split("\r\n").Single(s => s.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1]);
                var bytes = new byte[length]; await input.ReadExactlyAsync(bytes);
                using var json = JsonDocument.Parse(bytes);
                if (json.RootElement.GetProperty("type").GetString() != "response" || json.RootElement.GetProperty("request_seq").GetInt32() != seq) continue;
                Assert.True(json.RootElement.GetProperty("success").GetBoolean(), json.RootElement.ToString());
                return;
            }
        }
    }

    private sealed class CallbackSubscription : IDisposable { public void Dispose() { } }
}
