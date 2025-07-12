using System.Net.Http.Json;
using System.Text.Json;

namespace IxIFlow.Core;

/// <summary>
/// HTTP-based client for direct communication with workflow hosts
/// Enables real-time health checks and immediate workflow execution
/// </summary>
public class HttpWorkflowHostClient : IWorkflowHostClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly IHostRegistry _hostRegistry;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly TimeSpan _defaultTimeout = TimeSpan.FromSeconds(30);
    
    public HttpWorkflowHostClient(HttpClient httpClient, IHostRegistry hostRegistry)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _hostRegistry = hostRegistry ?? throw new ArgumentNullException(nameof(hostRegistry));
        
        // Configure JSON options for consistent serialization
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false
        };
        
        // Configure HTTP client defaults
        _httpClient.Timeout = TimeSpan.FromMinutes(5); // Overall client timeout
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "IxIFlow-WorkflowHost/1.0");
    }

    /// <summary>
    /// Check the health of a specific host
    /// </summary>
    public async Task<HostHealthResponse> CheckHealthAsync(string hostId, TimeSpan timeout = default)
    {
        if (timeout == default)
            timeout = _defaultTimeout;

        try
        {
            var hostEndpoint = await GetHostEndpointAsync(hostId);
            if (string.IsNullOrEmpty(hostEndpoint))
            {
                return new HostHealthResponse
                {
                    IsHealthy = false,
                    HostId = hostId,
                    CheckedAt = DateTime.UtcNow,
                    Status = "No endpoint available"
                };
            }

            using var cts = new CancellationTokenSource(timeout);
            var healthUrl = $"{hostEndpoint}/health";
            
            var response = await _httpClient.GetAsync(healthUrl, cts.Token);
            
            if (response.IsSuccessStatusCode)
            {
                var healthResponse = await response.Content.ReadFromJsonAsync<HostHealthResponse>(_jsonOptions, cts.Token);
                return healthResponse ?? new HostHealthResponse
                {
                    IsHealthy = false,
                    HostId = hostId,
                    CheckedAt = DateTime.UtcNow,
                    Status = "Invalid response format"
                };
            }
            else
            {
                return new HostHealthResponse
                {
                    IsHealthy = false,
                    HostId = hostId,
                    CheckedAt = DateTime.UtcNow,
                    Status = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}"
                };
            }
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
        catch (HttpRequestException ex)
        {
            return new HostHealthResponse
            {
                IsHealthy = false,
                HostId = hostId,
                CheckedAt = DateTime.UtcNow,
                Status = $"Network error: {ex.Message}"
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
    /// Execute a workflow immediately on a specific host
    /// </summary>
    public async Task<WorkflowExecutionResult> ExecuteImmediateAsync<TWorkflowData>(
        string hostId, 
        WorkflowDefinition definition, 
        TWorkflowData workflowData, 
        ImmediateExecutionOptions options)
        where TWorkflowData : class
    {
        try
        {
            var hostEndpoint = await GetHostEndpointAsync(hostId);
            if (string.IsNullOrEmpty(hostEndpoint))
            {
                return new WorkflowExecutionResult
                {
                    Status = WorkflowExecutionStatus.Failed,
                    ErrorMessage = $"No endpoint available for host '{hostId}'",
                    InstanceId = ""
                };
            }

            var executeUrl = $"{hostEndpoint}/workflows/execute-immediate";
            
            var request = new ImmediateExecutionRequest
            {
                Definition = definition,
                WorkflowData = workflowData,
                WorkflowDataType = typeof(TWorkflowData).AssemblyQualifiedName ?? "",
                Options = options
            };

            using var cts = new CancellationTokenSource(options.ExecutionTimeout);
            
            var response = await _httpClient.PostAsJsonAsync(executeUrl, request, _jsonOptions, cts.Token);
            
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<WorkflowExecutionResult>(_jsonOptions, cts.Token);
                return result ?? new WorkflowExecutionResult
                {
                    Status = WorkflowExecutionStatus.Failed,
                    ErrorMessage = "Invalid response format from host",
                    InstanceId = ""
                };
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync(cts.Token);
                return new WorkflowExecutionResult
                {
                    Status = WorkflowExecutionStatus.Failed,
                    ErrorMessage = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}. {errorContent}",
                    InstanceId = ""
                };
            }
        }
        catch (OperationCanceledException)
        {
            return new WorkflowExecutionResult
            {
                Status = WorkflowExecutionStatus.TimedOut,
                ErrorMessage = "Workflow execution timed out",
                InstanceId = ""
            };
        }
        catch (HttpRequestException ex)
        {
            return new WorkflowExecutionResult
            {
                Status = WorkflowExecutionStatus.Failed,
                ErrorMessage = $"Network error during execution: {ex.Message}",
                InstanceId = ""
            };
        }
        catch (Exception ex)
        {
            return new WorkflowExecutionResult
            {
                Status = WorkflowExecutionStatus.Failed,
                ErrorMessage = $"Error during execution: {ex.Message}",
                InstanceId = ""
            };
        }
    }

    /// <summary>
    /// Get detailed status information from a host
    /// </summary>
    public async Task<HostStatusResponse> GetStatusAsync(string hostId)
    {
        try
        {
            var hostEndpoint = await GetHostEndpointAsync(hostId);
            if (string.IsNullOrEmpty(hostEndpoint))
            {
                return new HostStatusResponse
                {
                    HostId = hostId,
                    Status = new HostStatus { HostId = hostId, IsHealthy = false, Status = "No endpoint" },
                    CollectedAt = DateTime.UtcNow,
                    Metrics = new Dictionary<string, object>
                    {
                        ["Error"] = "No endpoint available"
                    }
                };
            }

            var statusUrl = $"{hostEndpoint}/status";
            
            using var cts = new CancellationTokenSource(_defaultTimeout);
            var response = await _httpClient.GetAsync(statusUrl, cts.Token);
            
            if (response.IsSuccessStatusCode)
            {
                var statusResponse = await response.Content.ReadFromJsonAsync<HostStatusResponse>(_jsonOptions, cts.Token);
                return statusResponse ?? new HostStatusResponse
                {
                    HostId = hostId,
                    Status = new HostStatus { HostId = hostId, IsHealthy = false, Status = "Invalid response" },
                    CollectedAt = DateTime.UtcNow,
                    Metrics = new Dictionary<string, object>
                    {
                        ["Error"] = "Invalid response format"
                    }
                };
            }
            else
            {
                return new HostStatusResponse
                {
                    HostId = hostId,
                    Status = new HostStatus { HostId = hostId, IsHealthy = false, Status = $"HTTP {(int)response.StatusCode}" },
                    CollectedAt = DateTime.UtcNow,
                    Metrics = new Dictionary<string, object>
                    {
                        ["HttpStatusCode"] = (int)response.StatusCode,
                        ["Error"] = response.ReasonPhrase ?? "Unknown error"
                    }
                };
            }
        }
        catch (OperationCanceledException)
        {
            return new HostStatusResponse
            {
                HostId = hostId,
                Status = new HostStatus { HostId = hostId, IsHealthy = false, Status = "Timeout" },
                CollectedAt = DateTime.UtcNow,
                Metrics = new Dictionary<string, object>
                {
                    ["Error"] = "Request timeout"
                }
            };
        }
        catch (Exception ex)
        {
            return new HostStatusResponse
            {
                HostId = hostId,
                Status = new HostStatus { HostId = hostId, IsHealthy = false, Status = "Error" },
                CollectedAt = DateTime.UtcNow,
                Metrics = new Dictionary<string, object>
                {
                    ["Error"] = ex.Message
                }
            };
        }
    }

    /// <summary>
    /// Check if a host can accept another workflow
    /// </summary>
    public async Task<bool> CanAcceptWorkflowAsync(string hostId, bool allowOverride = false)
    {
        try
        {
            var hostEndpoint = await GetHostEndpointAsync(hostId);
            if (string.IsNullOrEmpty(hostEndpoint))
                return false;

            var canAcceptUrl = $"{hostEndpoint}/can-accept-workflow?allowOverride={allowOverride}";
            
            using var cts = new CancellationTokenSource(_defaultTimeout);
            var response = await _httpClient.GetAsync(canAcceptUrl, cts.Token);
            
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<CanAcceptWorkflowResponse>(_jsonOptions, cts.Token);
                return result?.CanAccept ?? false;
            }
            
            return false;
        }
        catch
        {
            return false; // Assume host cannot accept workflow on any error
        }
    }

    /// <summary>
    /// Resume a suspended workflow on a specific host
    /// </summary>
    public async Task<WorkflowExecutionResult> ResumeWorkflowAsync<TEvent>(
        string hostId, 
        string instanceId, 
        TEvent @event)
        where TEvent : class
    {
        try
        {
            var hostEndpoint = await GetHostEndpointAsync(hostId);
            if (string.IsNullOrEmpty(hostEndpoint))
            {
                return new WorkflowExecutionResult
                {
                    Status = WorkflowExecutionStatus.Failed,
                    ErrorMessage = $"No endpoint available for host '{hostId}'",
                    InstanceId = instanceId
                };
            }

            var resumeUrl = $"{hostEndpoint}/workflows/{instanceId}/resume";
            
            var request = new ResumeWorkflowRequest
            {
                InstanceId = instanceId,
                Event = @event,
                EventType = typeof(TEvent).AssemblyQualifiedName ?? ""
            };

            using var cts = new CancellationTokenSource(_defaultTimeout);
            
            var response = await _httpClient.PostAsJsonAsync(resumeUrl, request, _jsonOptions, cts.Token);
            
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<WorkflowExecutionResult>(_jsonOptions, cts.Token);
                return result ?? new WorkflowExecutionResult
                {
                    Status = WorkflowExecutionStatus.Failed,
                    ErrorMessage = "Invalid response format from host",
                    InstanceId = instanceId
                };
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync(cts.Token);
                return new WorkflowExecutionResult
                {
                    Status = WorkflowExecutionStatus.Failed,
                    ErrorMessage = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}. {errorContent}",
                    InstanceId = instanceId
                };
            }
        }
        catch (OperationCanceledException)
        {
            return new WorkflowExecutionResult
            {
                Status = WorkflowExecutionStatus.TimedOut,
                ErrorMessage = "Resume workflow request timed out",
                InstanceId = instanceId
            };
        }
        catch (Exception ex)
        {
            return new WorkflowExecutionResult
            {
                Status = WorkflowExecutionStatus.Failed,
                ErrorMessage = $"Error during resume: {ex.Message}",
                InstanceId = instanceId
            };
        }
    }

    /// <summary>
    /// Cancel a running workflow on a specific host
    /// </summary>
    public async Task<bool> CancelWorkflowAsync(string hostId, string instanceId, CancellationReason reason)
    {
        try
        {
            var hostEndpoint = await GetHostEndpointAsync(hostId);
            if (string.IsNullOrEmpty(hostEndpoint))
                return false;

            var cancelUrl = $"{hostEndpoint}/workflows/{instanceId}/cancel";
            
            using var cts = new CancellationTokenSource(_defaultTimeout);
            
            var response = await _httpClient.PostAsJsonAsync(cancelUrl, reason, _jsonOptions, cts.Token);
            
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false; // Assume cancellation failed on any error
        }
    }

    /// <summary>
    /// Get workflow instance status from a specific host
    /// </summary>
    public async Task<WorkflowInstance?> GetWorkflowInstanceAsync(string hostId, string instanceId)
    {
        try
        {
            var hostEndpoint = await GetHostEndpointAsync(hostId);
            if (string.IsNullOrEmpty(hostEndpoint))
                return null;

            var instanceUrl = $"{hostEndpoint}/workflows/{instanceId}";
            
            using var cts = new CancellationTokenSource(_defaultTimeout);
            var response = await _httpClient.GetAsync(instanceUrl, cts.Token);
            
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<WorkflowInstance>(_jsonOptions, cts.Token);
            }
            
            return null;
        }
        catch
        {
            return null; // Return null on any error
        }
    }

    /// <summary>
    /// Send a queued workflow request to a specific host
    /// </summary>
    public async Task<string> QueueWorkflowAsync<TWorkflowData>(
        string hostId, 
        WorkflowDefinition definition, 
        TWorkflowData workflowData, 
        WorkflowOptions? options = null)
        where TWorkflowData : class
    {
        try
        {
            var hostEndpoint = await GetHostEndpointAsync(hostId);
            if (string.IsNullOrEmpty(hostEndpoint))
                throw new InvalidOperationException($"No endpoint available for host '{hostId}'");

            var queueUrl = $"{hostEndpoint}/workflows/queue";
            
            var request = new QueueWorkflowRequest
            {
                Definition = definition,
                WorkflowData = workflowData,
                WorkflowDataType = typeof(TWorkflowData).AssemblyQualifiedName ?? "",
                Options = options
            };

            using var cts = new CancellationTokenSource(_defaultTimeout);
            
            var response = await _httpClient.PostAsJsonAsync(queueUrl, request, _jsonOptions, cts.Token);
            
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<QueueWorkflowResponse>(_jsonOptions, cts.Token);
                return result?.InstanceId ?? throw new InvalidOperationException("Invalid response from host");
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync(cts.Token);
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}. {errorContent}");
            }
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("Queue workflow request timed out");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Network error during queue request: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Get the HTTP endpoint for a specific host
    /// </summary>
    private async Task<string?> GetHostEndpointAsync(string hostId)
    {
        try
        {
            var hostRegistration = await _hostRegistry.GetHostAsync(hostId);
            return hostRegistration?.EndpointUrl;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}

/// <summary>
/// Request payload for immediate workflow execution
/// </summary>
public class ImmediateExecutionRequest
{
    public WorkflowDefinition Definition { get; set; } = null!;
    public object WorkflowData { get; set; } = null!;
    public string WorkflowDataType { get; set; } = "";
    public ImmediateExecutionOptions Options { get; set; } = null!;
}

/// <summary>
/// Response indicating whether a host can accept a workflow
/// </summary>
public class CanAcceptWorkflowResponse
{
    public bool CanAccept { get; set; }
    public string Reason { get; set; } = "";
    public int CurrentWorkflowCount { get; set; }
    public int MaxWorkflowCount { get; set; }
    public bool AllowOverride { get; set; }
}

/// <summary>
/// Request payload for resuming a suspended workflow
/// </summary>
public class ResumeWorkflowRequest
{
    public string InstanceId { get; set; } = "";
    public object Event { get; set; } = null!;
    public string EventType { get; set; } = "";
}

/// <summary>
/// Request payload for queuing a workflow
/// </summary>
public class QueueWorkflowRequest
{
    public WorkflowDefinition Definition { get; set; } = null!;
    public object WorkflowData { get; set; } = null!;
    public string WorkflowDataType { get; set; } = "";
    public WorkflowOptions? Options { get; set; }
}

/// <summary>
/// Response from queuing a workflow
/// </summary>
public class QueueWorkflowResponse
{
    public string InstanceId { get; set; } = "";
    public WorkflowExecutionStatus Status { get; set; } = WorkflowExecutionStatus.Scheduled;
    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;
}
