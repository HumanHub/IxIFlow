using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Coordinator;

internal sealed class CoordinatorLinks(CoordinatorClientOptions options, IWorkflowStateRepository store,
    WorkflowHost host, HostSettings settings, IWorkflowCatalog registry, CoordinationState state,
    TimeProvider time, ILogger<CoordinatorLinks> logger) : BackgroundService, IRunnableNotifier
{
    private readonly object _gate = new();
    private readonly Dictionary<Uri, CoordinatorLink> _links = [];
    private readonly HashSet<CoordinatorLink> _active = [];
    private readonly HashSet<string> _awaited = new(StringComparer.Ordinal);
    private long _next;
    internal int ConnectedCount { get { lock (_gate) return _active.Count; } }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        CoordinatorBuilderExtensions.RefuseSqlite(store);
        options.ValidateClient();
        state.PollInterval = options.CoordinatedPollInterval;
        host.AwaitSubscriptionAsync = ConfirmAsync;
        host.ObserveAwaiters(AwaitingChanged);
        host.FreeSlotsChanged += FreeSlotsChanged;
        return base.StartAsync(cancellationToken);
    }

    private void ConnectionChanged(CoordinatorLink link, bool connected)
    {
        lock (_gate)
        {
            if (connected)
            {
                if (!_active.Add(link)) return;
                state.AddLink();
                foreach (var id in _awaited.ToArray()) link.Subscribe(id, replay: true);
            }
            else { if (!_active.Remove(link)) return; state.RemoveLink(); }
        }
        // Link loss releases pending acknowledgements, and this signal makes existing awaiters
        // reread immediately even if their previous wait used the longer coordinated interval.
        if (!connected) foreach (var id in host.AwaitedInstances) host.NotifyCompletion(id);
    }
    private void AwaitingChanged(string id, bool watching)
    {
        lock (_gate)
        {
            if (watching)
            {
                if (_awaited.Add(id)) foreach (var link in _active.ToArray()) link.Subscribe(id, replay: false);
            }
            else if (_awaited.Remove(id)) foreach (var link in _active.ToArray()) link.Unsubscribe(id);
        }
    }
    private Task ConfirmAsync(string id, CancellationToken token)
    {
        lock (_gate) return Task.WhenAll(_active.Select(link => link.Acknowledged(id))).WaitAsync(token);
    }
    private void FreeSlotsChanged()
    {
        lock (_gate) foreach (var link in _active.ToArray()) link.Ready();
        if (state.Connected && host.FreeSlots > 0) state.RequestPass();
    }
    public async Task NotifyAsync(RunnableNotice notice, CancellationToken cancellationToken)
    {
        CoordinatorLink[] links;
        lock (_gate) links = _active.ToArray();
        if (links.Length == 0) return;
        // This is a writer-side placement read, outside the commit and its acknowledgement.
        cancellationToken.ThrowIfCancellationRequested();
        var demands = notice.Claimable ? await store.GetWorkflowDemandsAsync(notice.InstanceId) : [];
        cancellationToken.ThrowIfCancellationRequested();
        var message = new InstanceNotice(notice.InstanceId, notice.Claimable, notice.Idle, demands.ToArray(), notice.LastHostId);
        var start = (int)((ulong)Interlocked.Increment(ref _next) % (uint)links.Length);
        for (var i = 0; i < links.Length; i++)
            if (links[(start + i) % links.Length].TryNotify(message)) return;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            do
            {
                var desired = new HashSet<Uri>(options.Coordinators);
                var registryAvailable = false;
                try
                {
                    foreach (var member in await store.GetLiveMembersAsync(stoppingToken))
                        if (member.Kind == MemberKind.Coordinator && Uri.TryCreate(member.Endpoint, UriKind.Absolute, out var endpoint))
                            try { options.ValidateEndpoint(endpoint); desired.Add(endpoint); }
                            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
                            { logger.LogWarning("Coordinator link to {Endpoint} refused: {ReasonCategory}",
                                CoordinatorSecurity.EndpointName(endpoint), "endpoint-policy"); }
                    registryAvailable = true;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                { logger.LogWarning(error, "Could not discover coordinators; retaining existing links"); }
                List<CoordinatorLink> dropped = [];
                lock (_gate)
                {
                    if (registryAvailable)
                        foreach (var endpoint in _links.Keys.Where(endpoint => !desired.Contains(endpoint)).ToArray())
                        { dropped.Add(_links[endpoint]); _links.Remove(endpoint); }
                    foreach (var endpoint in desired.Where(endpoint => !_links.ContainsKey(endpoint)))
                    {
                        var link = new CoordinatorLink(endpoint, options, host, settings, registry, time, ConnectionChanged, logger);
                        link.Wake += state.RequestPass;
                        _links.Add(endpoint, link);
                        link.Start();
                    }
                }
                foreach (var link in dropped) link.Stop();
                foreach (var link in dropped) { await link.RunTask; link.Dispose(); }
                await Task.Delay(options.DiscoveryInterval, time, stoppingToken);
            } while (true);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            CoordinatorLink[] links;
            lock (_gate) { links = _links.Values.ToArray(); _links.Clear(); }
            foreach (var link in links) link.Stop();
            foreach (var link in links) { await link.RunTask; link.Dispose(); }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        Detach();
    }
    private void Detach()
    {
        host.AwaitingChanged -= AwaitingChanged;
        host.FreeSlotsChanged -= FreeSlotsChanged;
        host.AwaitSubscriptionAsync = null;
    }
    public override void Dispose() { Detach(); base.Dispose(); }
}
