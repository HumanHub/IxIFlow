using System.Threading.Channels;

namespace IxIFlow.Coordinator;

// Controls fail the stream when this bounded queue is full; disposable hints may be dropped.
internal sealed class SendQueue<T>(int capacity)
{
    private readonly Channel<T> _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
    { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, AllowSynchronousContinuations = false });
    internal bool TrySend(T message) => _channel.Writer.TryWrite(message);
    internal IAsyncEnumerable<T> ReadAsync(CancellationToken token) => _channel.Reader.ReadAllAsync(token);
}
