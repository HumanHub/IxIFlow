using System.Text.Json;

namespace IxIFlow.Core;

/// <summary>
/// Core workflow coordinator implementation that manages workflow distribution
/// across multiple hosts using tag-based selection and immediate execution
/// </summary>
public class WorkflowCoordinator : IWorkflowCoordinator
{
    private readonly IWorkflowHostClient _hostClient;
    private readonly IHostRegistry _hostRegistry;
    private readonly TimeSpan _defaultHealthCheckTimeout = TimeSpan.FromSeconds(5);

    public WorkflowCoordinator(
        IWorkflowHostClient hostClient,
        IHostRegistry hostRegistry)
    {
        _hostClient = hostClient ?? throw new ArgumentNullException(nameof(hostClient));
        _hostRegistry = hostRegistry ?? throw new ArgumentNullException(nameof(hostRegistry));
    }

    /// <summary>
    /// Select the best host for workflow execution based on options
    /// </summary>
    public async Task<string> SelectHostForWorkflowAsync(WorkflowOptions? options = null)
    {
        var availableHosts = await GetAvailableHostsAsync();
        var eligibleHosts = availableHosts.Where(h => h.IsHealthy).ToList();

        if (!eligibleHosts.Any())
            throw new InvalidOperationException("No healthy hosts available for workflow execution");

        // 1. REQUIRED HOST ID (highest priority)
        if (!string.IsNullOrEmpty(options?.RequiredHostId))
        {
            var requiredHost = eligibleHosts.FirstOrDefault(h => h.HostId == options.RequiredHostId);
            if (requiredHost == null)
                throw new InvalidOperationException($"Required host '{options.RequiredHostId}' is not available or unhealthy");
            
            // Check if required host can accept workflow
            var canAccept = await _hostClient.CanAcceptWorkflowAsync(requiredHost.HostId, options.OverrideCapacityLimit);
            if (!canAccept)
                throw new InvalidOperationException($"Required host '{options.RequiredHostId}' cannot accept more workflows");
            
            return requiredHost.HostId;
        }

        // 2. REQUIRED TAGS (must have ALL required tags)
        if (options?.RequiredTags?.Length > 0)
        {
            eligibleHosts = eligibleHosts.Where(h => 
                options.RequiredTags.All(tag => h.Tags.Contains(tag))).ToList();
            
            if (!eligibleHosts.Any())
            {
                if (!options.AllowTagFallback)
                    throw new InvalidOperationException($"No hosts available with required tags: {string.Join(", ", options.RequiredTags)}");
                
                // Fallback to all healthy hosts if no tagged hosts found
                eligibleHosts = availableHosts.Where(h => h.IsHealthy).ToList();
            }
        }

        // 3. PREFERRED HOST ID (with fallback)
        if (!string.IsNullOrEmpty(options?.PreferredHostId))
        {
            var preferredHost = eligibleHosts.FirstOrDefault(h => h.HostId == options.PreferredHostId);
            if (preferredHost != null)
            {
                var canAccept = await _hostClient.CanAcceptWorkflowAsync(preferredHost.HostId, options.OverrideCapacityLimit);
                if (canAccept)
                    return preferredHost.HostId;
            }
        }

        // 4. PREFERRED TAGS (prioritize hosts with preferred tags)
        if (options?.PreferredTags?.Length > 0)
        {
            var preferredHosts = eligibleHosts.Where(h => 
                options.PreferredTags.Any(tag => h.Tags.Contains(tag))).ToList();
            
            if (preferredHosts.Any())
                eligibleHosts = preferredHosts;
        }

        // 5. CAPACITY FILTERING (only hosts that can accept workflows)
        var capacityFilteredHosts = new List<HostStatus>();
        foreach (var host in eligibleHosts)
        {
            try
            {
                var canAccept = await _hostClient.CanAcceptWorkflowAsync(host.HostId, options?.OverrideCapacityLimit ?? false);
                if (canAccept)
                    capacityFilteredHosts.Add(host);
            }
            catch
            {
                // Skip hosts that can't be reached
                continue;
            }
        }

        if (!capacityFilteredHosts.Any())
            throw new InvalidOperationException("No hosts have available capacity for workflow execution");

        // 6. WEIGHTED LOAD BALANCING (final selection)
        // Prefer hosts with lower current load relative to their weight and capacity
        var selectedHost = capacityFilteredHosts
            .OrderBy(h => CalculateLoadScore(h))
            .ThenBy(h => h.CurrentWorkflowCount) // Tie-breaker: prefer less loaded host
            .First();

        return selectedHost.HostId;
    }

    /// <summary>
    /// Select host for resuming a workflow (typically the same host or best available)
    /// </summary>
    public async Task<string> SelectHostForResumeAsync(string instanceId)
    {
        // Try to find the host that originally executed this workflow
        var availableHosts = await GetAvailableHostsAsync();
        
        // For now, select any available host (could be enhanced to prefer the original host)
        // In a real implementation, you'd query the workflow state to find the original host
        var healthyHosts = availableHosts.Where(h => h.IsHealthy).ToList();
        
        if (!healthyHosts.Any())
            throw new InvalidOperationException($"No healthy hosts available to resume workflow {instanceId}");

        // Select the least loaded host
        var selectedHost = healthyHosts
            .OrderBy(h => CalculateLoadScore(h))
            .First();

        return selectedHost.HostId;
    }

    /// <summary>
    /// Get hosts that match specific tag requirements
    /// </summary>
    public async Task<IEnumerable<HostStatus>> GetHostsByTagsAsync(string[] requiredTags, string[]? preferredTags = null)
    {
        var allHosts = await GetAvailableHostsAsync();
        
        // Filter by required tags (must have ALL)
        var matchingHosts = allHosts.Where(h => 
            requiredTags.All(tag => h.Tags.Contains(tag))).ToList();

        // If preferred tags specified, sort by preference
        if (preferredTags?.Length > 0)
        {
            matchingHosts = matchingHosts
                .OrderByDescending(h => preferredTags.Count(tag => h.Tags.Contains(tag)))
                .ThenBy(h => CalculateLoadScore(h))
                .ToList();
        }
        else
        {
            // Sort by load if no preferred tags
            matchingHosts = matchingHosts
                .OrderBy(h => CalculateLoadScore(h))
                .ToList();
        }

        return matchingHosts;
    }

    /// <summary>
    /// Get healthy host IDs, optionally filtered by tags
    /// </summary>
    public async Task<string[]> GetHealthyHostsAsync(string[]? requiredTags = null)
    {
        var hosts = await GetAvailableHostsAsync();
        var healthyHosts = hosts.Where(h => h.IsHealthy);

        if (requiredTags?.Length > 0)
        {
            healthyHosts = healthyHosts.Where(h => 
                requiredTags.All(tag => h.Tags.Contains(tag)));
        }

        return healthyHosts.Select(h => h.HostId).ToArray();
    }

    /// <summary>
    /// Check health of a specific host with timeout
    /// </summary>
    public async Task<HostHealthResponse> CheckHostHealthAsync(string hostId, TimeSpan timeout = default)
    {
        if (timeout == default)
            timeout = _defaultHealthCheckTimeout;

        try
        {
            using var cts = new CancellationTokenSource(timeout);
            return await _hostClient.CheckHealthAsync(hostId, timeout);
        }
        catch (OperationCanceledException)
        {
            return new HostHealthResponse
            {
                IsHealthy = false,
                HostId = hostId,
                CheckedAt = DateTime.UtcNow,
                Status = "Timeout"
            };
        }
        catch (Exception ex)
        {
            return new HostHealthResponse
            {
                IsHealthy = false,
                HostId = hostId,
                CheckedAt = DateTime.UtcNow,
                Status = $"Error: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Bulk health check multiple hosts in parallel
    /// </summary>
    public async Task<HostHealthResponse[]> BulkHealthCheckAsync(string[] hostIds, TimeSpan timeout = default)
    {
        if (timeout == default)
            timeout = _defaultHealthCheckTimeout;

        var healthCheckTasks = hostIds.Select(hostId => CheckHostHealthAsync(hostId, timeout)).ToArray();
        
        try
        {
            return await Task.WhenAll(healthCheckTasks);
        }
        catch
        {
            // Return partial results - some tasks may have completed
            var results = new List<HostHealthResponse>();
            for (int i = 0; i < healthCheckTasks.Length; i++)
            {
                try
                {
                    if (healthCheckTasks[i].IsCompletedSuccessfully)
                        results.Add(healthCheckTasks[i].Result);
                    else
                        results.Add(new HostHealthResponse
                        {
                            IsHealthy = false,
                            HostId = hostIds[i],
                            CheckedAt = DateTime.UtcNow,
                            Status = "Failed"
                        });
                }
                catch
                {
                    results.Add(new HostHealthResponse
                    {
                        IsHealthy = false,
                        HostId = hostIds[i],
                        CheckedAt = DateTime.UtcNow,
                        Status = "Error"
                    });
                }
            }
            
            return results.ToArray();
        }
    }

    /// <summary>
    /// Execute workflow immediately on a specific host, bypassing normal distribution
    /// </summary>
    public async Task<WorkflowExecutionResult> ExecuteImmediateAsync<TWorkflowData>(
        string hostId,
        WorkflowDefinition definition, 
        TWorkflowData workflowData, 
        ImmediateExecutionOptions options)
        where TWorkflowData : class
    {
        // 1. Real-time health check
        var health = await CheckHostHealthAsync(hostId, TimeSpan.FromSeconds(5));
        if (!health.IsHealthy)
            throw new InvalidOperationException($"Host '{hostId}' is not healthy: {health.Status}");
        
        // 2. Capacity check (unless override allowed)
        if (!options.OverrideCapacityLimit)
        {
            var canAccept = await _hostClient.CanAcceptWorkflowAsync(hostId, false);
            if (!canAccept)
                throw new InvalidOperationException($"Host '{hostId}' cannot accept more workflows");
        }
        
        // 3. Direct execution
        return await _hostClient.ExecuteImmediateAsync(hostId, definition, workflowData, options);
    }

    /// <summary>
    /// Register a new host in the cluster
    /// </summary>
    public async Task RegisterHostAsync(string hostId, HostCapabilities capabilities)
    {
        await _hostRegistry.RegisterHostAsync(hostId, capabilities.EndpointUrl, capabilities);
    }

    /// <summary>
    /// Update host status information
    /// </summary>
    public async Task UpdateHostStatusAsync(HostStatus status)
    {
        await _hostRegistry.UpdateHostStatusAsync(status.HostId, status);
    }

    /// <summary>
    /// Unregister a host from the cluster
    /// </summary>
    public async Task UnregisterHostAsync(string hostId)
    {
        await _hostRegistry.UnregisterHostAsync(hostId);
    }

    /// <summary>
    /// Get all available hosts with their current status
    /// </summary>
    public async Task<IEnumerable<HostStatus>> GetAvailableHostsAsync()
    {
        // Get all registered hosts
        var registeredHosts = await _hostRegistry.GetRegisteredHostsAsync();
        var activeHosts = registeredHosts.Where(h => h.IsActive).ToArray();
        
        if (!activeHosts.Any())
            return Enumerable.Empty<HostStatus>();

        // Get current status for active hosts
        var allHostStatus = await _hostRegistry.GetAllHostStatusAsync();
        
        // Combine registration info with current status
        var availableHosts = new List<HostStatus>();
        
        foreach (var registration in activeHosts)
        {
            var currentStatus = allHostStatus.FirstOrDefault(s => s.HostId == registration.HostId);
            
            if (currentStatus != null)
            {
                // Use current status but ensure registration info is up to date
                currentStatus.EndpointUrl = registration.EndpointUrl;
                currentStatus.Tags = registration.Tags;
                currentStatus.Weight = registration.Weight;
                currentStatus.MaxConcurrentWorkflows = registration.MaxConcurrentWorkflows;
                availableHosts.Add(currentStatus);
            }
            else
            {
                // Create status from registration info (host may not have reported status yet)
                availableHosts.Add(new HostStatus
                {
                    HostId = registration.HostId,
                    IsHealthy = DateTime.UtcNow - registration.LastHeartbeat < TimeSpan.FromMinutes(2),
                    CurrentWorkflowCount = 0,
                    MaxConcurrentWorkflows = registration.MaxConcurrentWorkflows,
                    Weight = registration.Weight,
                    Tags = registration.Tags,
                    EndpointUrl = registration.EndpointUrl,
                    LastHeartbeat = registration.LastHeartbeat,
                    Status = "Unknown"
                });
            }
        }
        
        return availableHosts;
    }

    /// <summary>
    /// Discover available hosts through various discovery mechanisms
    /// </summary>
    public async Task<string[]> DiscoverAvailableHostsAsync()
    {
        // Get hosts from registry
        var registryHosts = await _hostRegistry.GetActiveHostsAsync(TimeSpan.FromMinutes(5));
        
        // In a real implementation, you might also:
        // - Use service discovery (Consul, etcd, etc.)
        // - Scan network for hosts
        // - Use DNS discovery
        // - Use cloud provider APIs (AWS ECS, K8s, etc.)
        
        return registryHosts.Select(h => h.HostId).ToArray();
    }

    /// <summary>
    /// Calculate load score for a host (lower is better)
    /// </summary>
    private double CalculateLoadScore(HostStatus host)
    {
        if (host.MaxConcurrentWorkflows == 0)
            return double.MaxValue; // Avoid division by zero
        
        // Base load ratio (0.0 to 1.0+)
        double loadRatio = (double)host.CurrentWorkflowCount / host.MaxConcurrentWorkflows;
        
        // Apply weight factor (higher weight = better capacity = lower score)
        double weightedScore = loadRatio / Math.Max(host.Weight, 1);
        
        // Apply health penalty
        if (!host.IsHealthy)
            weightedScore += 1000; // Heavy penalty for unhealthy hosts
        
        // Apply CPU/memory usage if available
        if (host.CpuUsage > 80)
            weightedScore += 0.5;
        if (host.MemoryUsage > 80)
            weightedScore += 0.5;
        
        return weightedScore;
    }
}
