using Microsoft.Extensions.Logging;

namespace IxIFlow.Core;

/// <summary>
///     Interface for workflow execution tracing
/// </summary>
public interface IWorkflowTracer
{
    /// <summary>
    ///     Add a trace entry for workflow execution
    /// </summary>
    Task TraceAsync(string workflowInstanceId, ExecutionTraceEntry traceEntry,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Get trace entries for a workflow instance
    /// </summary>
    Task<IEnumerable<ExecutionTraceEntry>> GetTraceEntriesAsync(string workflowInstanceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Clear traces for a workflow instance
    /// </summary>
    Task ClearTracesAsync(string workflowInstanceId, CancellationToken cancellationToken = default);
}

/// <summary>
///     Workflow execution tracer for debugging and monitoring
/// </summary>
public class WorkflowTracer : IWorkflowTracer
{
    private readonly ILogger<WorkflowTracer> _logger;
    private readonly IWorkflowStateRepository _stateRepository;

    public WorkflowTracer(ILogger<WorkflowTracer> logger, IWorkflowStateRepository stateRepository)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _stateRepository = stateRepository ?? throw new ArgumentNullException(nameof(stateRepository));
    }

    /// <summary>
    ///     Add a trace entry for workflow execution
    /// </summary>
    public async Task TraceAsync(string workflowInstanceId, ExecutionTraceEntry traceEntry,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(workflowInstanceId))
            throw new ArgumentException("Workflow instance ID cannot be null or empty", nameof(workflowInstanceId));

        if (traceEntry == null)
            throw new ArgumentNullException(nameof(traceEntry));

        try
        {
            // Get the workflow instance and add the trace entry
            var instance = await _stateRepository.GetWorkflowInstanceAsync(workflowInstanceId);
            if (instance != null)
            {
                instance.ExecutionHistory.Add(traceEntry);
                await _stateRepository.SaveWorkflowInstanceAsync(instance);
            }

            _logger.LogDebug("Trace entry added: {TraceType} for instance {InstanceId}",
                traceEntry.EntryType, workflowInstanceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add trace entry for instance {InstanceId}", workflowInstanceId);
            // Don't throw - tracing failures shouldn't break workflow execution
        }
    }

    /// <summary>
    ///     Get trace entries for a workflow instance
    /// </summary>
    public async Task<IEnumerable<ExecutionTraceEntry>> GetTraceEntriesAsync(string workflowInstanceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(workflowInstanceId))
            throw new ArgumentException("Workflow instance ID cannot be null or empty", nameof(workflowInstanceId));

        try
        {
            var instance = await _stateRepository.GetWorkflowInstanceAsync(workflowInstanceId);
            return instance?.ExecutionHistory ?? new List<ExecutionTraceEntry>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get trace entries for instance {InstanceId}", workflowInstanceId);
            return new List<ExecutionTraceEntry>();
        }
    }

    /// <summary>
    ///     Clear traces for a workflow instance
    /// </summary>
    public async Task ClearTracesAsync(string workflowInstanceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(workflowInstanceId))
            throw new ArgumentException("Workflow instance ID cannot be null or empty", nameof(workflowInstanceId));

        try
        {
            var instance = await _stateRepository.GetWorkflowInstanceAsync(workflowInstanceId);
            if (instance != null)
            {
                instance.ExecutionHistory.Clear();
                await _stateRepository.SaveWorkflowInstanceAsync(instance);
            }

            _logger.LogDebug("Trace entries cleared for instance {InstanceId}", workflowInstanceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear trace entries for instance {InstanceId}", workflowInstanceId);
        }
    }
}