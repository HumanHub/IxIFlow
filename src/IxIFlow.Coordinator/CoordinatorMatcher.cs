using IxIFlow.Core;

namespace IxIFlow.Coordinator;

// No repository dependency: even a notice's demand snapshot comes from its writer.
internal sealed class CoordinatorMatcher(CoordinatorOptions options, TimeProvider time)
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Peer> _hosts = [];
    private readonly Dictionary<string, Held> _notices = new(StringComparer.Ordinal);
    private long _order;
    internal int HeldCount { get { lock (_gate) { Expire(); return _notices.Count; } } }

    internal sealed class Peer(HostHello hello, Func<CoordinatorMessage, bool> send, Action abort)
    {
        internal HostHello Hello = hello;
        internal int FreeSlots;
        internal long LastWake;
        internal readonly Dictionary<string, long> Awaits = new(StringComparer.Ordinal);
        internal bool Send(CoordinatorMessage message) => send(message);
        internal void Abort() => abort();
    }
    private sealed record Held(InstanceNotice Notice, long Started);

    internal Peer Connect(HostHello hello, Func<CoordinatorMessage, bool> send, Action abort)
    {
        if (hello.ContractVersion != LinkProtocol.Version)
            throw new Grpc.Core.RpcException(new(Grpc.Core.StatusCode.FailedPrecondition, "Unsupported coordinator protocol version"));
        if (hello.HostId == Guid.Empty || string.IsNullOrWhiteSpace(hello.Name) || hello.Name.Length > 200 ||
            hello.Slots < 1 || hello.Capabilities == null)
            throw LinkProtocol.Invalid();
        var snapshot = hello with { Capabilities = new(Core.MemberRegistry.SnapshotCapabilities(hello.Capabilities)) };
        lock (_gate)
        {
            if (_hosts.Count >= options.MaxConnections && !_hosts.ContainsKey(hello.HostId))
                throw new Grpc.Core.RpcException(new(Grpc.Core.StatusCode.ResourceExhausted, "Coordinator connection limit"));
            if (_hosts.Remove(hello.HostId, out var old)) old.Abort();
            return _hosts[hello.HostId] = new(snapshot, send, abort);
        }
    }
    internal void Disconnect(Peer peer)
    {
        lock (_gate)
            if (_hosts.GetValueOrDefault(peer.Hello.HostId) == peer) _hosts.Remove(peer.Hello.HostId);
    }
    internal void DisconnectAll()
    {
        Peer[] peers;
        lock (_gate) { peers = _hosts.Values.ToArray(); _hosts.Clear(); }
        foreach (var peer in peers) peer.Abort();
    }
    internal void Update(Peer peer, HostHello hello)
    {
        if (hello.HostId != peer.Hello.HostId || hello.ContractVersion != LinkProtocol.Version || hello.Slots != peer.Hello.Slots)
            throw LinkProtocol.Invalid("Hello cannot change a link's identity, slots or contract");
        lock (_gate)
        {
            peer.Hello = hello with { Capabilities = new(MemberRegistry.SnapshotCapabilities(hello.Capabilities)) };
            Expire();
            foreach (var held in _notices.Values.ToArray()) Match(held);
        }
    }
    internal void Ready(Peer peer, int freeSlots)
    {
        if (freeSlots < 0 || freeSlots > peer.Hello.Slots) throw LinkProtocol.Invalid();
        lock (_gate)
        {
            peer.FreeSlots = freeSlots;
            Expire();
            foreach (var held in _notices.Values.ToArray()) Match(held);
        }
    }
    internal void Subscribe(Peer peer, string id, long subscription, bool watching)
    {
        ValidateId(id);
        lock (_gate)
        {
            if (!watching) { peer.Awaits.Remove(id); return; }
            if (subscription <= 0) throw LinkProtocol.Invalid();
            if (peer.Awaits.Count >= options.MaxAwaitedInstancesPerHost && !peer.Awaits.ContainsKey(id))
                throw new Grpc.Core.RpcException(new(Grpc.Core.StatusCode.ResourceExhausted, "Coordinator subscription limit"));
            peer.Awaits[id] = subscription;
            if (!peer.Send(new(CoordinatorMessageKind.Awaited, InstanceId: id, Subscription: subscription))) peer.Abort();
        }
    }
    internal void Notify(InstanceNotice notice)
    {
        ValidateId(notice.InstanceId);
        if (notice.Demands == null || notice.Demands.Length > 1024) throw LinkProtocol.Invalid();
        foreach (var demand in notice.Demands) WorkflowDemands.Validate(demand);
        lock (_gate)
        {
            if (notice.Idle)
                foreach (var peer in _hosts.Values.Where(p => p.Awaits.ContainsKey(notice.InstanceId)))
                    peer.Send(new(CoordinatorMessageKind.Changed, InstanceId: notice.InstanceId));
            Expire();
            if (!notice.Claimable || _notices.ContainsKey(notice.InstanceId) || _notices.Count >= options.NoticeCapacity) return;
            var held = new Held(notice with { Demands = notice.Demands.ToArray() }, time.GetTimestamp());
            _notices.Add(notice.InstanceId, held);
            Match(held);
        }
    }
    internal void ExpireNotices() { lock (_gate) Expire(); }
    private void Expire()
    {
        foreach (var key in _notices.Where(p => time.GetElapsedTime(p.Value.Started) >= options.NoticeLifetime)
                     .Select(p => p.Key).ToArray()) _notices.Remove(key);
    }
    private void Match(Held held)
    {
        var now = time.GetUtcNow().UtcDateTime;
        foreach (var peer in _hosts.Values.Where(p => p.FreeSlots > 0 && DemandMatching.Meets(held.Notice.Demands,
                     new ClaimQuery(p.Hello.HostId, p.Hello.Capabilities), now))
                 .OrderByDescending(p => p.Hello.HostId == held.Notice.LastHostId)
                 .ThenByDescending(p => p.FreeSlots).ThenBy(p => p.LastWake))
        {
            if (!peer.Send(new(CoordinatorMessageKind.Wake, InstanceIds: [held.Notice.InstanceId]))) continue;
            peer.FreeSlots--;
            peer.LastWake = ++_order;
            _notices.Remove(held.Notice.InstanceId);
            return;
        }
    }
    private static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 400) throw LinkProtocol.Invalid();
    }
}
