namespace IxIFlow.Core;

/// <summary>
/// Interface for host registry and service discovery
/// Manages the registration and discovery of workflow hosts in the distributed cluster
/// </summary>
public interface IHostRegistry
{
    /// <summary>
    /// Register a host in the registry
    /// </summary>
    /// <param name="hostId">Unique host identifier</param>
    /// <param name="endpointUrl">HTTP endpoint URL for the host</param>
    /// <param name="capabilities">Host capabilities and configuration</param>
    /// <returns>Task that completes when registration is done</returns>
    Task RegisterHostAsync(string hostId, string endpointUrl, HostCapabilities capabilities);

    /// <summary>
    /// Unregister a host from the registry
    /// </summary>
    /// <param name="hostId">Host to unregister</param>
    /// <returns>Task that completes when unregistration is done</returns>
    Task UnregisterHostAsync(string hostId);

    /// <summary>
    /// Get all registered hosts
    /// </summary>
    /// <returns>Array of registered host information</returns>
    Task<HostRegistration[]> GetRegisteredHostsAsync();

    /// <summary>
    /// Get a specific registered host
    /// </summary>
    /// <param name="hostId">Host ID to lookup</param>
    /// <returns>Host registration info or null if not found</returns>
    Task<HostRegistration?> GetHostAsync(string hostId);

    /// <summary>
    /// Update heartbeat timestamp for a host
    /// </summary>
    /// <param name="hostId">Host to update</param>
    /// <returns>Task that completes when heartbeat is updated</returns>
    Task UpdateHeartbeatAsync(string hostId);

    /// <summary>
    /// Get active hosts (recently heartbeated)
    /// </summary>
    /// <param name="maxAge">Maximum age for considering a host active</param>
    /// <returns>Array of active host registrations</returns>
    Task<HostRegistration[]> GetActiveHostsAsync(TimeSpan? maxAge = null);

    /// <summary>
    /// Find hosts by tags
    /// </summary>
    /// <param name="requiredTags">Tags that hosts MUST have</param>
    /// <param name="preferredTags">Tags that are preferred</param>
    /// <returns>Matching host registrations</returns>
    Task<HostRegistration[]> FindHostsByTagsAsync(string[] requiredTags, string[]? preferredTags = null);

    /// <summary>
    /// Clean up stale host registrations
    /// </summary>
    /// <param name="maxAge">Maximum age before considering a host stale</param>
    /// <returns>Number of stale hosts removed</returns>
    Task<int> CleanupStaleHostsAsync(TimeSpan maxAge);

    /// <summary>
    /// Update host status information
    /// </summary>
    /// <param name="hostId">Host to update</param>
    /// <param name="status">Updated status information</param>
    /// <returns>Task that completes when status is updated</returns>
    Task UpdateHostStatusAsync(string hostId, HostStatus status);

    /// <summary>
    /// Get current status for all registered hosts
    /// </summary>
    /// <returns>Array of host status information</returns>
    Task<HostStatus[]> GetAllHostStatusAsync();
}
