using System.Collections.Concurrent;
using IxIFlow.Core;

namespace IxIFlow.Tests.Infrastructure;

/// <summary>
/// In-memory host registry implementation for testing distributed workflows
/// Mimics the behavior of a database-backed host registry without requiring database setup
/// </summary>
public class InMemoryHostRegistry : IHostRegistry
{
    private readonly ConcurrentDictionary<string, HostRegistration> _hosts = new();
    private readonly ConcurrentDictionary<string, HostStatus> _hostStatus = new();
    private readonly object _lock = new();

    public async Task RegisterHostAsync(string hostId, string endpointUrl, HostCapabilities capabilities)
    {
        if (string.IsNullOrWhiteSpace(hostId)) throw new ArgumentException("HostId cannot be empty", nameof(hostId));
        if (string.IsNullOrWhiteSpace(endpointUrl)) throw new ArgumentException("EndpointUrl cannot be empty", nameof(endpointUrl));
        if (capabilities == null) throw new ArgumentNullException(nameof(capabilities));

        var registration = new HostRegistration
        {
            HostId = hostId,
            EndpointUrl = endpointUrl,
            Tags = capabilities.Tags ?? Array.Empty<string>(),
            Weight = capabilities.Weight,
            MaxConcurrentWorkflows = capabilities.MaxConcurrentWorkflows,
            LastHeartbeat = DateTime.UtcNow,
            IsActive = true
        };

        _hosts.AddOrUpdate(hostId, registration, (key, existing) =>
        {
            existing.EndpointUrl = endpointUrl;
            existing.Tags = capabilities.Tags ?? Array.Empty<string>();
            existing.Weight = capabilities.Weight;
            existing.MaxConcurrentWorkflows = capabilities.MaxConcurrentWorkflows;
            existing.LastHeartbeat = DateTime.UtcNow;
            existing.IsActive = true;
            return existing;
        });

        await Task.CompletedTask;
    }

    public async Task UnregisterHostAsync(string hostId)
    {
        if (string.IsNullOrWhiteSpace(hostId)) throw new ArgumentException("HostId cannot be empty", nameof(hostId));

        if (_hosts.TryGetValue(hostId, out var existing))
        {
            existing.IsActive = false;
            existing.LastHeartbeat = DateTime.UtcNow;
        }

        await Task.CompletedTask;
    }

    public async Task<HostRegistration[]> GetRegisteredHostsAsync()
    {
        var activeHosts = _hosts.Values
            .Where(h => h.IsActive)
            .ToArray();

        await Task.CompletedTask;
        return activeHosts;
    }

    public async Task<HostRegistration?> GetHostAsync(string hostId)
    {
        if (string.IsNullOrWhiteSpace(hostId)) throw new ArgumentException("HostId cannot be empty", nameof(hostId));

        _hosts.TryGetValue(hostId, out var host);
        await Task.CompletedTask;
        return host?.IsActive == true ? host : null;
    }

    public async Task UpdateHeartbeatAsync(string hostId)
    {
        if (string.IsNullOrWhiteSpace(hostId)) throw new ArgumentException("HostId cannot be empty", nameof(hostId));

        if (_hosts.TryGetValue(hostId, out var existing))
        {
            existing.LastHeartbeat = DateTime.UtcNow;
            existing.IsActive = true;
        }

        await Task.CompletedTask;
    }

    public async Task<HostRegistration[]> GetActiveHostsAsync(TimeSpan? maxAge = null)
    {
        var cutoff = DateTime.UtcNow - (maxAge ?? TimeSpan.FromMinutes(5));
        var activeHosts = _hosts.Values
            .Where(h => h.IsActive && h.LastHeartbeat >= cutoff)
            .ToArray();

        await Task.CompletedTask;
        return activeHosts;
    }

    public async Task<HostRegistration[]> FindHostsByTagsAsync(string[] requiredTags, string[]? preferredTags = null)
    {
        if (requiredTags == null) throw new ArgumentNullException(nameof(requiredTags));

        var activeHosts = _hosts.Values.Where(h => h.IsActive).ToList();

        // Filter by required tags (host must have ALL required tags)
        if (requiredTags.Length > 0)
        {
            activeHosts = activeHosts.Where(h => 
                requiredTags.All(tag => h.Tags.Contains(tag))).ToList();
        }

        // Sort by preferred tags (hosts with preferred tags come first)
        if (preferredTags?.Length > 0)
        {
            activeHosts = activeHosts
                .OrderByDescending(h => preferredTags.Count(tag => h.Tags.Contains(tag)))
                .ToList();
        }

        await Task.CompletedTask;
        return activeHosts.ToArray();
    }

    public async Task<int> CleanupStaleHostsAsync(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var staleHosts = _hosts.Values
            .Where(h => h.LastHeartbeat < cutoff)
            .ToList();

        foreach (var host in staleHosts)
        {
            host.IsActive = false;
        }

        await Task.CompletedTask;
        return staleHosts.Count;
    }

    public async Task UpdateHostStatusAsync(string hostId, HostStatus status)
    {
        if (string.IsNullOrWhiteSpace(hostId)) throw new ArgumentException("HostId cannot be empty", nameof(hostId));
        if (status == null) throw new ArgumentNullException(nameof(status));

        _hostStatus.AddOrUpdate(hostId, status, (key, existing) => status);

        // Also update the registration if it exists
        if (_hosts.TryGetValue(hostId, out var registration))
        {
            registration.LastHeartbeat = DateTime.UtcNow;
            registration.IsActive = status.IsHealthy;
        }

        await Task.CompletedTask;
    }

    public async Task<HostStatus[]> GetAllHostStatusAsync()
    {
        var statusList = _hostStatus.Values.ToArray();
        await Task.CompletedTask;
        return statusList;
    }

    // Test helper methods
    public void Clear()
    {
        _hosts.Clear();
    }

    public int GetHostCount() => _hosts.Count(kvp => kvp.Value.IsActive);

    public HostRegistration[] GetAllHosts() => _hosts.Values.ToArray();

    public HostRegistration[] GetHostsByTag(string tag)
    {
        return _hosts.Values
            .Where(h => h.IsActive && h.Tags.Contains(tag))
            .ToArray();
    }

    public HostRegistration[] GetHealthyHosts(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        return _hosts.Values
            .Where(h => h.IsActive && h.LastHeartbeat >= cutoff)
            .ToArray();
    }

    public void SimulateHostOffline(string hostId)
    {
        if (_hosts.TryGetValue(hostId, out var host))
        {
            host.IsActive = false;
            host.LastHeartbeat = DateTime.UtcNow - TimeSpan.FromMinutes(10); // Old heartbeat
        }
    }

    public void SimulateStaleHost(string hostId, TimeSpan age)
    {
        if (_hosts.TryGetValue(hostId, out var host))
        {
            host.LastHeartbeat = DateTime.UtcNow - age;
        }
    }

    public async Task SimulateHeartbeatsAsync(params string[] hostIds)
    {
        foreach (var hostId in hostIds)
        {
            await UpdateHeartbeatAsync(hostId);
        }
    }
}
