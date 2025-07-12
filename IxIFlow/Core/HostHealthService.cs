using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace IxIFlow.Core;

/// <summary>
/// Background service that monitors host health and publishes heartbeat events
/// Tracks system metrics and maintains host status in the registry
/// </summary>
public class HostHealthService : BackgroundService
{
    private readonly ILogger<HostHealthService> _logger;
    private readonly IHostRegistry _hostRegistry;
    private readonly IMessageBus _messageBus;
    private readonly WorkflowHostOptions _hostOptions;
    private readonly string _hostId;
    private readonly TimeSpan _heartbeatInterval;
    private volatile int _currentWorkflowCount = 0;

    public HostHealthService(
        ILogger<HostHealthService> logger,
        IHostRegistry hostRegistry,
        IMessageBus messageBus,
        WorkflowHostOptions hostOptions)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _hostRegistry = hostRegistry ?? throw new ArgumentNullException(nameof(hostRegistry));
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _hostOptions = hostOptions ?? throw new ArgumentNullException(nameof(hostOptions));
        _hostId = _hostOptions.HostId;
        _heartbeatInterval = _hostOptions.HealthCheckInterval;
    }

    public int CurrentWorkflowCount => _currentWorkflowCount;

    public void IncrementWorkflowCount() => Interlocked.Increment(ref _currentWorkflowCount);
    
    public void DecrementWorkflowCount() => Interlocked.Decrement(ref _currentWorkflowCount);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Host Health Service started for host {HostId} with interval {Interval}", 
            _hostId, _heartbeatInterval);

        try
        {
            // Register host on startup
            await RegisterHostAsync();

            // Start health monitoring loop
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await PublishHealthStatusAsync();
                    await UpdateHostRegistryAsync();
                    
                    await Task.Delay(_heartbeatInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in health monitoring for host {HostId}", _hostId);
                    
                    // Continue monitoring even after errors
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Host Health Service stopped for host {HostId}", _hostId);
        }
        finally
        {
            // Unregister host on shutdown
            await UnregisterHostAsync();
        }
    }

    private async Task RegisterHostAsync()
    {
        try
        {
            var capabilities = new HostCapabilities
            {
                HostId = _hostId,
                MaxConcurrentWorkflows = _hostOptions.MaxConcurrentWorkflows,
                Weight = _hostOptions.Weight,
                Tags = _hostOptions.Tags,
                EndpointUrl = _hostOptions.EndpointUrl,
                AllowImmediateExecution = _hostOptions.AllowImmediateExecution,
                AllowCapacityOverride = _hostOptions.AllowCapacityOverride,
                HealthCheckInterval = _hostOptions.HealthCheckInterval
            };

            await _hostRegistry.RegisterHostAsync(_hostId, _hostOptions.EndpointUrl, capabilities);
            _logger.LogInformation("Successfully registered host {HostId} with capabilities: {Tags}", 
                _hostId, string.Join(", ", _hostOptions.Tags));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register host {HostId}", _hostId);
            throw;
        }
    }

    private async Task UnregisterHostAsync()
    {
        try
        {
            await _hostRegistry.UnregisterHostAsync(_hostId);
            _logger.LogInformation("Successfully unregistered host {HostId}", _hostId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to unregister host {HostId}", _hostId);
        }
    }

    private async Task PublishHealthStatusAsync()
    {
        try
        {
            var systemMetrics = GetSystemMetrics();
            var status = DetermineHostStatus(systemMetrics);

            var heartbeatEvent = new HostHeartbeatEvent
            {
                HostId = _hostId,
                CurrentWorkflowCount = _currentWorkflowCount,
                MaxWorkflowCount = _hostOptions.MaxConcurrentWorkflows,
                Tags = _hostOptions.Tags,
                CpuUsage = systemMetrics.CpuUsage,
                MemoryUsage = systemMetrics.MemoryUsage,
                Status = status,
                Timestamp = DateTime.UtcNow
            };

            await _messageBus.PublishAsync(heartbeatEvent);
            
            _logger.LogDebug("Published heartbeat for host {HostId}: {Status}, Workflows: {Current}/{Max}, CPU: {Cpu:F1}%, Memory: {Memory:F1}%",
                _hostId, status, _currentWorkflowCount, _hostOptions.MaxConcurrentWorkflows, 
                systemMetrics.CpuUsage, systemMetrics.MemoryUsage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish health status for host {HostId}", _hostId);
        }
    }

    private async Task UpdateHostRegistryAsync()
    {
        try
        {
            var systemMetrics = GetSystemMetrics();
            var status = new HostStatus
            {
                HostId = _hostId,
                IsHealthy = IsHostHealthy(systemMetrics),
                CurrentWorkflowCount = _currentWorkflowCount,
                MaxConcurrentWorkflows = _hostOptions.MaxConcurrentWorkflows,
                Weight = _hostOptions.Weight,
                Tags = _hostOptions.Tags,
                EndpointUrl = _hostOptions.EndpointUrl,
                LastHeartbeat = DateTime.UtcNow,
                CpuUsage = systemMetrics.CpuUsage,
                MemoryUsage = systemMetrics.MemoryUsage,
                Status = DetermineHostStatus(systemMetrics)
            };

            await _hostRegistry.UpdateHostStatusAsync(_hostId, status);
            await _hostRegistry.UpdateHeartbeatAsync(_hostId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update host registry for host {HostId}", _hostId);
        }
    }

    private SystemMetrics GetSystemMetrics()
    {
        try
        {
            var process = Process.GetCurrentProcess();
            
            // Get memory usage
            var workingSet = process.WorkingSet64;
            var availableMemory = GC.GetTotalMemory(false);
            var memoryUsage = (double)workingSet / (1024 * 1024 * 1024); // Convert to GB, then to percentage (rough estimate)
            
            // Get CPU usage (simplified - would need more sophisticated tracking in production)
            var cpuUsage = GetCpuUsage();
            
            return new SystemMetrics
            {
                CpuUsage = cpuUsage,
                MemoryUsage = Math.Min(memoryUsage * 10, 100), // Rough approximation
                WorkingSetBytes = workingSet,
                AvailableMemoryBytes = availableMemory
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get system metrics for host {HostId}, using defaults", _hostId);
            return new SystemMetrics
            {
                CpuUsage = 0,
                MemoryUsage = 0,
                WorkingSetBytes = 0,
                AvailableMemoryBytes = 0
            };
        }
    }

    private double GetCpuUsage()
    {
        // Simplified CPU usage calculation
        // In production, this would use performance counters or more sophisticated monitoring
        try
        {
            var process = Process.GetCurrentProcess();
            var startTime = DateTime.UtcNow;
            var startCpuUsage = process.TotalProcessorTime;
            
            Task.Delay(100).Wait(); // Small delay to measure CPU
            
            var endTime = DateTime.UtcNow;
            var endCpuUsage = process.TotalProcessorTime;
            
            var cpuUsedMs = (endCpuUsage - startCpuUsage).TotalMilliseconds;
            var totalMsPassed = (endTime - startTime).TotalMilliseconds;
            var cpuUsageTotal = cpuUsedMs / (Environment.ProcessorCount * totalMsPassed);
            
            return Math.Min(cpuUsageTotal * 100, 100);
        }
        catch
        {
            return 0; // Default to 0 if calculation fails
        }
    }

    private bool IsHostHealthy(SystemMetrics metrics)
    {
        // Host is healthy if:
        // 1. CPU usage is below 90%
        // 2. Memory usage is below 90%
        // 3. Current workflow count is within limits (unless override allowed)
        
        if (metrics.CpuUsage > 90 || metrics.MemoryUsage > 90)
            return false;
            
        if (!_hostOptions.AllowCapacityOverride && _currentWorkflowCount >= _hostOptions.MaxConcurrentWorkflows)
            return false;
            
        return true;
    }

    private string DetermineHostStatus(SystemMetrics metrics)
    {
        if (!IsHostHealthy(metrics))
            return "Unhealthy";
            
        if (_currentWorkflowCount >= _hostOptions.MaxConcurrentWorkflows)
            return "Full";
            
        if (_currentWorkflowCount > _hostOptions.MaxConcurrentWorkflows * 0.8)
            return "Busy";
            
        return "Available";
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Host Health Service for host {HostId}", _hostId);
        await base.StopAsync(cancellationToken);
    }
}

/// <summary>
/// System metrics for health monitoring
/// </summary>
public class SystemMetrics
{
    public double CpuUsage { get; set; }
    public double MemoryUsage { get; set; }
    public long WorkingSetBytes { get; set; }
    public long AvailableMemoryBytes { get; set; }
}
