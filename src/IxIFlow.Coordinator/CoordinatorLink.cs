using Grpc.Core;
using Grpc.Net.Client;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Coordinator;

internal sealed class CoordinatorLink(Uri endpoint, CoordinatorClientOptions options, WorkflowHost host,
    HostSettings settings, IWorkflowVersionRegistry registry, TimeProvider time,
    Action<CoordinatorLink, bool> connectionChanged, ILogger logger) : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private SendQueue<HostMessage>? _queue;
    private CancellationTokenSource? _session;
    private readonly Dictionary<string, Subscription> _subscriptions = new(StringComparer.Ordinal);
    private long _sequence;
    private bool _ready;
    internal Task RunTask { get; private set; } = Task.CompletedTask;
    internal bool Connected { get { lock (_gate) return _ready; } }
    private sealed record Subscription(long Sequence, bool Replay, long Started)
    {
        internal TaskCompletionSource Ack { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    internal void Start() => RunTask = RunAsync();
    internal void Stop() => _stop.Cancel();
    public void Dispose() { _stop.Cancel(); _stop.Dispose(); }

    internal bool TryNotify(InstanceNotice notice)
    {
        lock (_gate) return _ready && _queue!.TrySend(new(HostMessageKind.Notify, Notice: notice));
    }
    internal void Ready()
    {
        lock (_gate)
            if (_ready && !_queue!.TrySend(new(HostMessageKind.Ready, FreeSlots: host.FreeSlots))) AbortSession();
    }
    internal void Subscribe(string id, bool replay)
    {
        lock (_gate)
        {
            if (!_ready || _subscriptions.ContainsKey(id)) return;
            var subscription = new Subscription(++_sequence, replay, time.GetTimestamp());
            _subscriptions.Add(id, subscription);
            if (!_queue!.TrySend(new(HostMessageKind.Await, InstanceId: id, Subscription: subscription.Sequence))) AbortSession();
        }
    }
    internal void Unsubscribe(string id)
    {
        lock (_gate)
        {
            if (_subscriptions.Remove(id, out var subscription)) subscription.Ack.TrySetResult();
            if (_ready && !_queue!.TrySend(new(HostMessageKind.Unawait, InstanceId: id))) AbortSession();
        }
    }
    internal Task Acknowledged(string id)
    {
        lock (_gate) return _subscriptions.TryGetValue(id, out var subscription) ? subscription.Ack.Task : Task.CompletedTask;
    }
    private void AbortSession()
    {
        // Queue failures can occur under the host's watcher lock. Run transport cancellation
        // callbacks asynchronously so teardown cannot reenter the host/link lock hierarchy.
        if (_session != null) _ = _session.CancelAsync();
    }

    private async Task RunAsync()
    {
        var failures = 0;
        while (!_stop.IsCancellationRequested)
        {
            var started = time.GetTimestamp();
            try { await SessionAsync(_stop.Token); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                // Transport/RPC exceptions can contain peer-controlled metadata. Never log them.
                var category = error is RpcException rpc ? rpc.StatusCode switch
                {
                    StatusCode.Unauthenticated or StatusCode.PermissionDenied => "authentication",
                    StatusCode.FailedPrecondition or StatusCode.InvalidArgument => "protocol",
                    _ => "transport"
                } : error is TimeoutException ? "timeout" : "transport";
                logger.LogDebug("Coordinator link to {Endpoint} closed: {ReasonCategory}", CoordinatorSecurity.EndpointName(endpoint), category);
            }
            if (_stop.IsCancellationRequested) break;
            if (time.GetElapsedTime(started) >= options.LinkTimeout) failures = 0;
            var delay = ReconnectDelay(options, failures++);
            try { await Task.Delay(delay, time, _stop.Token); }
            catch (OperationCanceledException) { break; }
        }
    }
    internal static TimeSpan ReconnectDelay(CoordinatorClientOptions options, int failures) =>
        TimeSpan.FromTicks((long)Math.Min(options.ReconnectMaxDelay.Ticks,
            options.ReconnectMinDelay.Ticks * Math.Pow(2, Math.Min(failures, 20))));

    private async Task SessionAsync(CancellationToken stoppingToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var authenticatedServer = 0;
        using var handler = new SocketsHttpHandler
        {
            KeepAlivePingDelay = TimeSpan.FromSeconds(Math.Max(1, options.LinkHeartbeatInterval.TotalSeconds)),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(Math.Max(1, options.LinkTimeout.TotalSeconds)),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            EnableMultipleHttp2Connections = true,
            AllowAutoRedirect = false,
            UseProxy = false
        };
        if (endpoint.Scheme == "https") handler.SslOptions = CoordinatorSecurity.ClientTls(options, endpoint, accepted =>
        {
            Volatile.Write(ref authenticatedServer, accepted ? 1 : 0);
            if (!accepted) logger.LogWarning("Coordinator link to {Endpoint} refused: {ReasonCategory}",
                CoordinatorSecurity.EndpointName(endpoint), "server-identity");
        });
        using var channel = GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions
        {
            HttpHandler = handler, MaxReceiveMessageSize = LinkProtocol.MaxMessageBytes, MaxSendMessageSize = LinkProtocol.MaxMessageBytes
        });
        // HTTP headers are transmitted only after TLS validates the configured server identity.
        var headers = new Metadata();
        if (endpoint.Scheme == "https" && options.AuthenticationMode == CoordinatorAuthenticationMode.SharedKey)
            headers.Add("authorization", "Bearer " + options.SharedKey);
        using var call = channel.CreateCallInvoker().AsyncDuplexStreamingCall(LinkProtocol.Link, null,
            new(headers: headers, cancellationToken: stop.Token));
        var queue = new SendQueue<HostMessage>(options.SendQueueCapacity);
        var capabilities = new Dictionary<string, string>(await WorkflowHeartbeatService.CapabilitiesAsync(registry, settings));
        queue.TrySend(Hello(capabilities));
        lock (_gate) { _queue = queue; _session = stop; }
        long lastSeen = time.GetTimestamp();
        var reading = ReadAsync();
        var writing = WriteAsync();
        var heartbeat = HeartbeatAsync();
        try { await await Task.WhenAny(reading, writing, heartbeat); }
        finally
        {
            bool wasReady;
            lock (_gate) { wasReady = _ready; _ready = false; }
            if (wasReady) connectionChanged(this, false);
            lock (_gate)
            {
                foreach (var subscription in _subscriptions.Values) subscription.Ack.TrySetResult();
                _subscriptions.Clear(); _queue = null; _session = null;
            }
            stop.Cancel();
            call.Dispose();
            foreach (var task in new[] { reading, writing, heartbeat })
                try { await task; } catch (Exception) { }
        }

        HostMessage Hello(Dictionary<string, string> current) => new(HostMessageKind.Hello,
            new(settings.Id, settings.Name, current, settings.Slots, LinkProtocol.Version));
        async Task ReadAsync()
        {
            var welcomed = false;
            while (await call.ResponseStream.MoveNext(stop.Token))
            {
                var message = call.ResponseStream.Current;
                Interlocked.Exchange(ref lastSeen, time.GetTimestamp());
                if (!welcomed)
                {
                    if (message.Kind != CoordinatorMessageKind.Welcome || message.CoordinatorId == Guid.Empty ||
                        message.ContractVersion != LinkProtocol.Version) throw LinkProtocol.Invalid("Compatible Welcome is required first");
                    if (endpoint.Scheme == "https" && Volatile.Read(ref authenticatedServer) == 0)
                        throw new RpcException(new(StatusCode.Unauthenticated, "Coordinator identity was not authenticated"));
                    welcomed = true;
                    lock (_gate) _ready = true;
                    Ready();
                    connectionChanged(this, true);
                    continue;
                }
                switch (message.Kind)
                {
                    case CoordinatorMessageKind.Wake when message.InstanceIds is { Length: > 0 }:
                        connectionWake(); break;
                    case CoordinatorMessageKind.Changed when message.InstanceId != null:
                        host.NotifyCompletion(message.InstanceId); break;
                    case CoordinatorMessageKind.Awaited when message.InstanceId != null:
                        bool reread = false;
                        lock (_gate)
                            if (_subscriptions.TryGetValue(message.InstanceId, out var subscription) &&
                                subscription.Sequence == message.Subscription)
                            { subscription.Ack.TrySetResult(); reread = subscription.Replay; }
                        if (reread) host.NotifyCompletion(message.InstanceId);
                        break;
                    case CoordinatorMessageKind.Heartbeat: break;
                    default: throw LinkProtocol.Invalid();
                }
            }
        }
        void connectionWake() => Wake?.Invoke();
        async Task WriteAsync()
        {
            await foreach (var message in queue.ReadAsync(stop.Token)) await call.RequestStream.WriteAsync(message, stop.Token);
        }
        async Task HeartbeatAsync()
        {
            using var timer = new PeriodicTimer(options.LinkHeartbeatInterval, time);
            while (await timer.WaitForNextTickAsync(stop.Token))
            {
                if (time.GetElapsedTime(Interlocked.Read(ref lastSeen)) >= options.LinkTimeout)
                    throw new TimeoutException("Coordinator application heartbeat timed out");
                lock (_gate)
                    if (_subscriptions.Values.Any(subscription => !subscription.Ack.Task.IsCompleted &&
                        time.GetElapsedTime(subscription.Started) >= options.LinkTimeout))
                        throw new TimeoutException("Coordinator subscription acknowledgement timed out");
                var current = new Dictionary<string, string>(await WorkflowHeartbeatService.CapabilitiesAsync(registry, settings));
                if (!capabilities.OrderBy(p => p.Key, StringComparer.Ordinal).SequenceEqual(current.OrderBy(p => p.Key, StringComparer.Ordinal)))
                {
                    if (!queue.TrySend(Hello(current))) throw new IOException("Coordinator send queue full");
                    capabilities = current;
                }
                if (!queue.TrySend(new(HostMessageKind.Heartbeat))) throw new IOException("Coordinator send queue full");
            }
        }
    }
    internal event Action? Wake;
}
