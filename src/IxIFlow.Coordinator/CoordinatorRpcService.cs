using Grpc.AspNetCore.Server.Model;
using Grpc.Core;
using Microsoft.AspNetCore.Http;

namespace IxIFlow.Coordinator;

internal sealed class CoordinatorMethods : IServiceMethodProvider<CoordinatorRpcService>
{
    public void OnServiceMethodDiscovery(ServiceMethodProviderContext<CoordinatorRpcService> context) =>
        context.AddDuplexStreamingMethod(LinkProtocol.Link, [], (service, input, output, call) => service.Link(input, output, call));
}

internal sealed class CoordinatorRpcService(CoordinatorMatcher matcher, CoordinatorOptions options,
    CoordinatorIdentity identity, TimeProvider time)
{
    internal async Task Link(IAsyncStreamReader<HostMessage> input, IServerStreamWriter<CoordinatorMessage> output,
        ServerCallContext context)
    {
        var http = context.GetHttpContext();
        if (!options.AllowInsecure || http.Request.IsHttps || http.Connection.RemoteIpAddress is not { } remote ||
            !System.Net.IPAddress.IsLoopback(remote))
            throw new RpcException(new(StatusCode.Unauthenticated, "Only explicitly trusted loopback links are enabled"));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        var queue = new SendQueue<CoordinatorMessage>(options.SendQueueCapacity);
        var lastSeen = time.GetTimestamp();
        CoordinatorMatcher.Peer? peer = null;
        var reading = ReadAsync();
        var writing = WriteAsync();
        var heartbeat = HeartbeatAsync();
        try
        {
            var finished = await Task.WhenAny(reading, writing, heartbeat);
            await finished;
        }
        finally
        {
            if (peer != null) matcher.Disconnect(peer);
            stop.Cancel();
            foreach (var task in new[] { reading, writing, heartbeat })
                try { await task; } catch (Exception) { /* The first failing task owns the link outcome. */ }
        }

        async Task ReadAsync()
        {
            while (await input.MoveNext(stop.Token))
            {
                var message = input.Current;
                Interlocked.Exchange(ref lastSeen, time.GetTimestamp());
                if (peer == null)
                {
                    if (message.Kind != HostMessageKind.Hello || message.Hello == null) throw LinkProtocol.Invalid("Hello is required first");
                    peer = matcher.Connect(message.Hello, queue.TrySend, Abort);
                    if (!queue.TrySend(new(CoordinatorMessageKind.Welcome, identity.Id, LinkProtocol.Version)))
                        throw LinkProtocol.Invalid();
                    continue;
                }
                switch (message.Kind)
                {
                    case HostMessageKind.Hello when message.Hello != null:
                        matcher.Update(peer, message.Hello);
                        break;
                    case HostMessageKind.Ready: matcher.Ready(peer, message.FreeSlots); break;
                    case HostMessageKind.Notify when message.Notice != null: matcher.Notify(message.Notice); break;
                    case HostMessageKind.Await when message.InstanceId != null:
                        matcher.Subscribe(peer, message.InstanceId, message.Subscription, true); break;
                    case HostMessageKind.Unawait when message.InstanceId != null:
                        matcher.Subscribe(peer, message.InstanceId, message.Subscription, false); break;
                    case HostMessageKind.Heartbeat: break;
                    default: throw LinkProtocol.Invalid();
                }
            }
        }
        async Task WriteAsync()
        {
            await foreach (var message in queue.ReadAsync(stop.Token))
                await output.WriteAsync(message, stop.Token);
        }
        void Abort()
        {
            // A disconnected peer may be in a shutdown snapshot. Both stream operations use this
            // token; avoid touching an HttpContext that can already have returned to Kestrel.
            try { _ = stop.CancelAsync(); } catch (ObjectDisposedException) { }
        }
        async Task HeartbeatAsync()
        {
            using var timer = new PeriodicTimer(options.LinkHeartbeatInterval, time);
            while (await timer.WaitForNextTickAsync(stop.Token))
            {
                matcher.ExpireNotices();
                if (time.GetElapsedTime(Interlocked.Read(ref lastSeen)) >= options.LinkTimeout)
                    throw new RpcException(new(StatusCode.DeadlineExceeded, "Coordinator peer heartbeat timed out"));
                if (!queue.TrySend(new(CoordinatorMessageKind.Heartbeat)))
                    throw new RpcException(new(StatusCode.ResourceExhausted, "Coordinator send queue full"));
            }
        }
    }
}

internal sealed class CoordinatorIdentity { internal Guid Id { get; } = Guid.NewGuid(); }
