using System.IO.Pipelines;
using System.Text;
using System.Text.Json;

namespace IxIFlow.Tests.Infrastructure;

/// <summary>Exercises the production framed adapter without sockets or spawned processes.</summary>
public sealed class InlineDebugProtocolClient : IAsyncDisposable
{
    public const string Token = "inline-caller-debugger-token-at-least-32-bytes";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);
    private readonly Pipe _requests = new(), _responses = new();
    private readonly IDisposable _session;
    private readonly Stream _input, _output;
    private readonly Task _server;
    private int _sequence;

    public InlineDebugProtocolClient(Func<Stream, Stream, Task> run, IDisposable session)
    {
        _session = session;
        _input = _responses.Reader.AsStream();
        _output = _requests.Writer.AsStream();
        _server = run(_requests.Reader.AsStream(), _responses.Writer.AsStream());
    }

    public async Task<JsonElement> Ok(string command, object? args = null)
    {
        var seq = ++_sequence;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { seq, type = "request", command, arguments = args ?? new { } });
        await _output.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n"));
        await _output.WriteAsync(bytes);
        await _output.FlushAsync();
        using var timeout = new CancellationTokenSource(Patience);

        while (true)
        {
            var one = new byte[1];
            var header = new StringBuilder();

            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                Assert.Equal(1, await _input.ReadAsync(one, timeout.Token));
                header.Append((char)one[0]);
            }

            var length = int.Parse(header.ToString().Split("\r\n")
                .Single(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1]);

            var body = new byte[length];
            await _input.ReadExactlyAsync(body, timeout.Token);
            using var parsed = JsonDocument.Parse(body);
            var message = parsed.RootElement;

            if (message.GetProperty("type").GetString() != "response" || message.GetProperty("request_seq").GetInt32() != seq)
                continue;

            Assert.True(message.GetProperty("success").GetBoolean(), message.GetRawText());

            return message.Clone();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _session.Dispose();
        _output.Dispose();
        await _server.WaitAsync(Patience);
        _input.Dispose();
    }
}