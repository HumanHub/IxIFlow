using System.Collections.Concurrent;
using System.Diagnostics;

namespace IxIFlow.Core;

/// <summary>
/// Core workflow host implementation that manages local workflow execution
/// and participates in the distributed workflow cluster
/// </summary>
public class WorkflowHost : IWorkflowHost, IDisposable
{
    private readonly IWorkflowEngine _workflowEngine;
    private readonly IHostRegistry _hostRegistry;
    private readonly WorkflowHostOptions _options;
    private readonly ConcurrentDictionary<string, WorkflowInstance> _runningWorkflows = new();
    private readonly Timer _heartbeatTimer;
    private readonly Timer _cleanupTimer;
    private volatile bool _isStarted = false;
    private volatile bool _isDisposed = false;

    public WorkflowHost(
        IWorkflowEngine workflowEngine,
        IHostRegistry hostRegistry,
        WorkflowHostOptions options)
    {
        _workflowEngine = workflowEngine ?? throw new ArgumentNullException(nameof(workflowEngine));
        _hostRegistry = hostRegistry ?? throw new ArgumentNullException(nameof(hostRegistry));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        // Initialize heartbeat timer
        _heartbeatTimer = new Timer(SendHeartbeat, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        
        // Initialize cleanup timer for completed workflows
        _cleanupTimer = new Timer(CleanupCompletedWorkflows, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Current status of this host
    /// </summary>
    public HostStatus Status { get; private set; } = new();

    /// <summary>
    /// Current number of workflows running on this host
    /// </summary>
    public int CurrentWorkflowCount => _runningWorkflows.Count;

    /// <summary>
    /// Start the workflow host and begin accepting workflow execution requests
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isStarted)
            return;

        // Register with the host registry
        var capabilities = new HostCapabilities
        {
            HostId = _options.HostId,
            MaxConcurrentWorkflows = _options.MaxConcurrentWorkflows,
            Weight = _options.Weight,
            Tags = _options.Tags,
            EndpointUrl = _options.EndpointUrl,
            AllowImmediateExecution = _options.AllowImmediateExecution,
            AllowCapacityOverride = _options.AllowCapacityOverride,
            HealthCheckInterval = _options.HealthCheckInterval
        };

        await _hostRegistry.RegisterHostAsync(_options.HostId, _options.EndpointUrl, capabilities);

        // Initialize status
        UpdateStatus();

        // Start heartbeat timer
        _heartbeatTimer.Change(TimeSpan.Zero, _options.HealthCheckInterval);
        
        // Start cleanup timer (cleanup every 5 minutes)
        _cleanupTimer.Change(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

        _isStarted = true;
        
        // Update status after setting _isStarted = true
        UpdateStatus();
    }

    /// <summary>
    /// Stop the workflow host and gracefully shut down
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_isStarted)
            return;

        // Stop timers
        _heartbeatTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _cleanupTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        // Wait for running workflows to complete (with timeout)
        var timeout = TimeSpan.FromMinutes(5);
        var stopwatch = Stopwatch.StartNew();
        
        while (_runningWorkflows.Count > 0 && stopwatch.Elapsed < timeout)
        {
            await Task.Delay(1000, cancellationToken);
        }

        // Unregister from host registry
        try
        {
            await _hostRegistry.UnregisterHostAsync(_options.HostId);
        }
        catch
        {
            // Ignore errors during shutdown
        }

        _isStarted = false;
    }

    /// <summary>
    /// Execute a workflow using standard queued execution
    /// </summary>
    public async Task<WorkflowExecutionResult> ExecuteWorkflowAsync<TWorkflowData>(
        WorkflowDefinition definition, 
        TWorkflowData workflowData, 
        WorkflowOptions? options = null)
        where TWorkflowData : class
    {
        EnsureStarted();

        // Check capacity unless override is allowed
        if (!CanAcceptWorkflowInternal(options?.OverrideCapacityLimit ?? false))
        {
            throw new InvalidOperationException($"Host '{_options.HostId}' is at capacity and cannot accept more workflows");
        }

        var instanceId = Guid.NewGuid().ToString();
        
        try
        {
            // Create workflow instance for tracking
            var instance = new WorkflowInstance
            {
                InstanceId = instanceId,
                WorkflowName = definition.Name,
                WorkflowVersion = definition.Version,
                Status = WorkflowStatus.Running,
                CreatedAt = DateTime.UtcNow,
                StartedAt = DateTime.UtcNow,
                WorkflowDataJson = System.Text.Json.JsonSerializer.Serialize(workflowData),
                WorkflowDataType = typeof(TWorkflowData).AssemblyQualifiedName ?? "",
                Properties = new Dictionary<string, object>
                {
                    ["HostId"] = _options.HostId,
                    ["ExecutionType"] = "Standard"
                }
            };

            // Add to running workflows for tracking
            _runningWorkflows.TryAdd(instanceId, instance);
            UpdateStatus();

            // Execute workflow using the existing engine
            var result = await _workflowEngine.ExecuteWorkflowAsync(definition, workflowData, options);

            // Update instance status
            instance.Status = result.IsSuccess ? WorkflowStatus.Completed : WorkflowStatus.Failed;
            instance.CompletedAt = DateTime.UtcNow;

            if (!result.IsSuccess && !string.IsNullOrEmpty(result.ErrorMessage))
            {
                instance.LastError = result.ErrorMessage;
                instance.LastErrorStackTrace = result.ErrorStackTrace;
            }

            return result;
        }
        finally
        {
            // Remove from running workflows immediately upon completion
            _runningWorkflows.TryRemove(instanceId, out _);
            UpdateStatus();
        }
    }

    /// <summary>
    /// Resume a suspended workflow with an event
    /// </summary>
    public async Task<WorkflowExecutionResult> ResumeWorkflowAsync<TEvent>(string instanceId, TEvent @event)
        where TEvent : class
    {
        EnsureStarted();

        try
        {
            // Check if we're tracking this workflow locally
            if (_runningWorkflows.TryGetValue(instanceId, out var instance))
            {
                instance.Status = WorkflowStatus.Running;
                UpdateStatus();
            }

            // Resume using the existing engine
            var result = await _workflowEngine.ResumeWorkflowAsync(instanceId, @event);

            // Update instance status if we're tracking it
            if (instance != null)
            {
                instance.Status = result.IsSuccess ? WorkflowStatus.Completed : WorkflowStatus.Failed;
                instance.CompletedAt = DateTime.UtcNow;

                if (!result.IsSuccess && !string.IsNullOrEmpty(result.ErrorMessage))
                {
                    instance.LastError = result.ErrorMessage;
                    instance.LastErrorStackTrace = result.ErrorStackTrace;
                }
            }

            return result;
        }
        finally
        {
            UpdateStatus();
        }
    }

    /// <summary>
    /// Execute a workflow immediately, bypassing the normal queue
    /// </summary>
    public async Task<WorkflowExecutionResult> ExecuteImmediateAsync<TWorkflowData>(
        WorkflowDefinition definition, 
        TWorkflowData workflowData, 
        ImmediateExecutionOptions options)
        where TWorkflowData : class
    {
        EnsureStarted();

        // Check if immediate execution is allowed
        if (!_options.AllowImmediateExecution)
        {
            throw new InvalidOperationException($"Host '{_options.HostId}' does not allow immediate execution");
        }

        // Check capacity unless override is allowed
        if (!options.OverrideCapacityLimit && !CanAcceptWorkflowInternal(false))
        {
            throw new InvalidOperationException($"Host '{_options.HostId}' is at capacity and cannot accept immediate execution");
        }

        var instanceId = Guid.NewGuid().ToString();

        try
        {
            // Create workflow instance for tracking
            var instance = new WorkflowInstance
            {
                InstanceId = instanceId,
                WorkflowName = definition.Name,
                WorkflowVersion = definition.Version,
                Status = WorkflowStatus.Running,
                CreatedAt = DateTime.UtcNow,
                StartedAt = DateTime.UtcNow,
                WorkflowDataJson = System.Text.Json.JsonSerializer.Serialize(workflowData),
                WorkflowDataType = typeof(TWorkflowData).AssemblyQualifiedName ?? "",
                Properties = new Dictionary<string, object>
                {
                    ["HostId"] = _options.HostId,
                    ["ExecutionType"] = "Immediate",
                    ["Priority"] = options.Priority,
                    ["BypassQueue"] = options.BypassQueue
                }
            };

            // Add to running workflows for tracking
            _runningWorkflows.TryAdd(instanceId, instance);
            UpdateStatus();

            // Create workflow options for immediate execution
            var workflowOptions = new WorkflowOptions
            {
                ExecutionTimeout = options.ExecutionTimeout,
                EnableTracing = true,
                Priority = options.Priority,
                Properties = new Dictionary<string, object>
                {
                    ["ImmediateExecution"] = true,
                    ["Priority"] = options.Priority
                }
            };

            // Execute workflow using the existing engine
            var result = await _workflowEngine.ExecuteWorkflowAsync(definition, workflowData, workflowOptions);

            // Update instance status
            instance.Status = result.IsSuccess ? WorkflowStatus.Completed : WorkflowStatus.Failed;
            instance.CompletedAt = DateTime.UtcNow;

            if (!result.IsSuccess && !string.IsNullOrEmpty(result.ErrorMessage))
            {
                instance.LastError = result.ErrorMessage;
                instance.LastErrorStackTrace = result.ErrorStackTrace;
            }

            return result;
        }
        finally
        {
            // Remove from running workflows immediately upon completion
            _runningWorkflows.TryRemove(instanceId, out _);
            UpdateStatus();
        }
    }

    /// <summary>
    /// Get current health status of this host
    /// </summary>
    public async Task<HostHealthResponse> GetHealthAsync()
    {
        var process = Process.GetCurrentProcess();
        var totalMemory = GC.GetTotalMemory(false);
        
        return new HostHealthResponse
        {
            IsHealthy = _isStarted && !_isDisposed,
            HostId = _options.HostId,
            CheckedAt = DateTime.UtcNow,
            CurrentWorkflowCount = CurrentWorkflowCount,
            MaxWorkflowCount = _options.MaxConcurrentWorkflows,
            CpuUsage = GetCpuUsage(),
            MemoryUsage = GetMemoryUsagePercentage(),
            Tags = _options.Tags,
            Status = GetHostStatusString()
        };
    }

    /// <summary>
    /// Get detailed status information about this host
    /// </summary>
    public async Task<HostStatusResponse> GetDetailedStatusAsync()
    {
        var health = await GetHealthAsync();
        
        return new HostStatusResponse
        {
            HostId = _options.HostId,
            Status = Status,
            CollectedAt = DateTime.UtcNow,
            Metrics = new Dictionary<string, object>
            {
                ["RunningWorkflows"] = CurrentWorkflowCount,
                ["MaxWorkflows"] = _options.MaxConcurrentWorkflows,
                ["CpuUsage"] = health.CpuUsage,
                ["MemoryUsage"] = health.MemoryUsage,
                ["IsStarted"] = _isStarted,
                ["Uptime"] = DateTime.UtcNow - Process.GetCurrentProcess().StartTime,
                ["Tags"] = _options.Tags,
                ["AllowImmediateExecution"] = _options.AllowImmediateExecution,
                ["Weight"] = _options.Weight
            }
        };
    }

    /// <summary>
    /// Check if this host can accept another workflow
    /// </summary>
    public async Task<bool> CanAcceptWorkflow(bool allowOverride = false)
    {
        return CanAcceptWorkflowInternal(allowOverride);
    }

    private bool CanAcceptWorkflowInternal(bool allowOverride)
    {
        if (!_isStarted || _isDisposed)
            return false;

        if (allowOverride && _options.AllowCapacityOverride)
            return true;

        return CurrentWorkflowCount < _options.MaxConcurrentWorkflows;
    }

    private void UpdateStatus()
    {
        Status = new HostStatus
        {
            HostId = _options.HostId,
            IsHealthy = _isStarted && !_isDisposed,
            CurrentWorkflowCount = CurrentWorkflowCount,
            MaxConcurrentWorkflows = _options.MaxConcurrentWorkflows,
            Weight = _options.Weight,
            Tags = _options.Tags,
            EndpointUrl = _options.EndpointUrl,
            LastHeartbeat = DateTime.UtcNow,
            CpuUsage = GetCpuUsage(),
            MemoryUsage = GetMemoryUsagePercentage(),
            Status = GetHostStatusString()
        };
    }

    private string GetHostStatusString()
    {
        if (!_isStarted || _isDisposed)
            return "Offline";

        var currentCount = CurrentWorkflowCount;
        var maxCount = _options.MaxConcurrentWorkflows;

        if (currentCount == 0)
            return "Available";
        if (currentCount < maxCount * 0.8)
            return "Busy";
        if (currentCount >= maxCount)
            return "Full";
        
        return "Busy";
    }

    private double GetCpuUsage()
    {
        try
        {
            var process = Process.GetCurrentProcess();
            return process.TotalProcessorTime.TotalMilliseconds / Environment.ProcessorCount / Environment.TickCount * 100;
        }
        catch
        {
            return 0.0;
        }
    }

    private double GetMemoryUsagePercentage()
    {
        try
        {
            var process = Process.GetCurrentProcess();
            var totalMemory = GC.GetTotalMemory(false);
            var workingSet = process.WorkingSet64;
            
            // Rough estimate - could be improved with more accurate system memory info
            return (double)workingSet / (1024 * 1024 * 1024) * 100; // % of 1GB baseline
        }
        catch
        {
            return 0.0;
        }
    }

    private void SendHeartbeat(object? state)
    {
        if (_isDisposed || !_isStarted)
            return;

        // Use Task.Run to avoid async void in timer callback
        _ = Task.Run(async () =>
        {
            try
            {
                UpdateStatus();
                await _hostRegistry.UpdateHeartbeatAsync(_options.HostId);
                await _hostRegistry.UpdateHostStatusAsync(_options.HostId, Status);
            }
            catch
            {
                // Log error but don't throw - heartbeat should be resilient
            }
        });
    }

    private void CleanupCompletedWorkflows(object? state)
    {
        if (_isDisposed)
            return;

        try
        {
            var cutoffTime = DateTime.UtcNow.AddMinutes(-30); // Keep completed workflows for 30 minutes

            var completedWorkflows = _runningWorkflows
                .Where(kvp => kvp.Value.Status == WorkflowStatus.Completed && 
                             kvp.Value.CompletedAt.HasValue && 
                             kvp.Value.CompletedAt.Value < cutoffTime)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var instanceId in completedWorkflows)
            {
                _runningWorkflows.TryRemove(instanceId, out _);
            }

            if (completedWorkflows.Count > 0)
            {
                UpdateStatus();
            }
        }
        catch
        {
            // Log error but don't throw - cleanup should be resilient
        }
    }

    private void EnsureStarted()
    {
        if (!_isStarted)
            throw new InvalidOperationException("WorkflowHost must be started before use");
        
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(WorkflowHost));
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Ignore errors during disposal
        }

        _heartbeatTimer?.Dispose();
        _cleanupTimer?.Dispose();
    }
}
