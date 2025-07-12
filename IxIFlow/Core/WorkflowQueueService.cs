using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Core;

/// <summary>
/// Background service that processes queued workflow execution commands
/// Handles distributed workflow execution with tag-based host selection
/// </summary>
public class WorkflowQueueService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<WorkflowQueueService> _logger;
    private readonly IMessageBus _messageBus;
    private readonly IWorkflowCoordinator _coordinator;
    private readonly string _hostId;

    public WorkflowQueueService(
        IServiceProvider serviceProvider,
        ILogger<WorkflowQueueService> logger,
        IMessageBus messageBus,
        IWorkflowCoordinator coordinator,
        WorkflowHostOptions hostOptions)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _hostId = hostOptions?.HostId ?? Environment.MachineName;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Workflow Queue Service started for host {HostId}", _hostId);

        try
        {
            // Process workflow execution commands
            var executeCommandTask = ProcessExecuteCommandsAsync(stoppingToken);
            
            // Process workflow resume commands  
            var resumeCommandTask = ProcessResumeCommandsAsync(stoppingToken);
            
            // Process workflow cancellation commands
            var cancelCommandTask = ProcessCancelCommandsAsync(stoppingToken);

            // Wait for any task to complete (should run indefinitely until cancellation)
            await Task.WhenAny(executeCommandTask, resumeCommandTask, cancelCommandTask);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Workflow Queue Service stopped for host {HostId}", _hostId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in Workflow Queue Service for host {HostId}", _hostId);
            throw;
        }
    }

    private async Task ProcessExecuteCommandsAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Starting to process execute commands for host {HostId}", _hostId);

        await foreach (var command in _messageBus.ConsumeAsync<ExecuteWorkflowCommand>().WithCancellation(cancellationToken))
        {
            try
            {
                // Only process commands targeted at this host
                if (command.TargetHostId != _hostId)
                    continue;

                _logger.LogInformation("Processing execute command {InstanceId} for host {HostId}", 
                    command.InstanceId, _hostId);

                await ProcessExecuteCommandAsync(command);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing execute command {InstanceId} for host {HostId}", 
                    command.InstanceId, _hostId);
            }
        }
    }

    private async Task ProcessResumeCommandsAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Starting to process resume commands for host {HostId}", _hostId);

        await foreach (var command in _messageBus.ConsumeAsync<ResumeWorkflowCommand>().WithCancellation(cancellationToken))
        {
            try
            {
                // Only process commands targeted at this host
                if (command.TargetHostId != _hostId)
                    continue;

                _logger.LogInformation("Processing resume command {InstanceId} for host {HostId}", 
                    command.InstanceId, _hostId);

                await ProcessResumeCommandAsync(command);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing resume command {InstanceId} for host {HostId}", 
                    command.InstanceId, _hostId);
            }
        }
    }

    private async Task ProcessCancelCommandsAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Starting to process cancel commands for host {HostId}", _hostId);

        await foreach (var command in _messageBus.ConsumeAsync<CancelWorkflowCommand>().WithCancellation(cancellationToken))
        {
            try
            {
                // Only process commands targeted at this host
                if (command.TargetHostId != _hostId)
                    continue;

                _logger.LogInformation("Processing cancel command {InstanceId} for host {HostId}", 
                    command.InstanceId, _hostId);

                await ProcessCancelCommandAsync(command);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing cancel command {InstanceId} for host {HostId}", 
                    command.InstanceId, _hostId);
            }
        }
    }

    private async Task ProcessExecuteCommandAsync(ExecuteWorkflowCommand command)
    {
        using var scope = _serviceProvider.CreateScope();
        var workflowEngine = scope.ServiceProvider.GetRequiredService<IWorkflowEngine>();

        try
        {
            // Publish started event
            await _messageBus.PublishAsync(new WorkflowExecutionStartedEvent
            {
                InstanceId = command.InstanceId,
                HostId = _hostId,
                WorkflowName = command.Definition.Name,
                StartedAt = DateTime.UtcNow
            });

            // Deserialize workflow data
            var workflowDataType = Type.GetType(command.WorkflowDataType);
            if (workflowDataType == null)
            {
                throw new InvalidOperationException($"Could not resolve workflow data type: {command.WorkflowDataType}");
            }

            var workflowData = System.Text.Json.JsonSerializer.Deserialize(command.WorkflowDataJson, workflowDataType);

            // Execute workflow
            var result = await workflowEngine.ExecuteWorkflowAsync(command.Definition, workflowData, command.Options);

            // Publish completion event
            await _messageBus.PublishAsync(new WorkflowExecutionCompletedEvent
            {
                InstanceId = command.InstanceId,
                HostId = _hostId,
                Status = result.Status,
                ErrorMessage = result.ErrorMessage,
                CompletedAt = DateTime.UtcNow,
                ExecutionTime = result.ExecutionTime
            });

            _logger.LogInformation("Successfully executed workflow {InstanceId} with status {Status}", 
                command.InstanceId, result.Status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute workflow {InstanceId}", command.InstanceId);

            // Publish failure event
            await _messageBus.PublishAsync(new WorkflowExecutionCompletedEvent
            {
                InstanceId = command.InstanceId,
                HostId = _hostId,
                Status = WorkflowExecutionStatus.Failed,
                ErrorMessage = ex.Message,
                CompletedAt = DateTime.UtcNow,
                ExecutionTime = TimeSpan.Zero
            });
        }
    }

    private async Task ProcessResumeCommandAsync(ResumeWorkflowCommand command)
    {
        using var scope = _serviceProvider.CreateScope();
        var workflowEngine = scope.ServiceProvider.GetRequiredService<IWorkflowEngine>();

        try
        {
            // Deserialize event data
            var eventDataType = Type.GetType(command.EventDataType);
            if (eventDataType == null)
            {
                throw new InvalidOperationException($"Could not resolve event data type: {command.EventDataType}");
            }

            var eventData = System.Text.Json.JsonSerializer.Deserialize(command.EventDataJson, eventDataType);

            // Resume workflow
            var result = await workflowEngine.ResumeWorkflowAsync(command.InstanceId, eventData);

            // Publish completion event if workflow finished
            if (result.Status != WorkflowExecutionStatus.Suspended)
            {
                await _messageBus.PublishAsync(new WorkflowExecutionCompletedEvent
                {
                    InstanceId = command.InstanceId,
                    HostId = _hostId,
                    Status = result.Status,
                    ErrorMessage = result.ErrorMessage,
                    CompletedAt = DateTime.UtcNow,
                    ExecutionTime = result.ExecutionTime
                });
            }

            _logger.LogInformation("Successfully resumed workflow {InstanceId} with status {Status}", 
                command.InstanceId, result.Status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resume workflow {InstanceId}", command.InstanceId);

            // Publish failure event
            await _messageBus.PublishAsync(new WorkflowExecutionCompletedEvent
            {
                InstanceId = command.InstanceId,
                HostId = _hostId,
                Status = WorkflowExecutionStatus.Failed,
                ErrorMessage = ex.Message,
                CompletedAt = DateTime.UtcNow,
                ExecutionTime = TimeSpan.Zero
            });
        }
    }

    private async Task ProcessCancelCommandAsync(CancelWorkflowCommand command)
    {
        using var scope = _serviceProvider.CreateScope();
        
        try
        {
            // For now, just log the cancellation request
            // In a full implementation, this would interact with the workflow engine to cancel execution
            _logger.LogInformation("Processing cancellation for workflow {InstanceId} with reason: {Reason}", 
                command.InstanceId, command.Reason);

            // Publish cancellation event
            await _messageBus.PublishAsync(new WorkflowExecutionCompletedEvent
            {
                InstanceId = command.InstanceId,
                HostId = _hostId,
                Status = WorkflowExecutionStatus.Cancelled,
                ErrorMessage = command.Reason?.ToString(),
                CompletedAt = DateTime.UtcNow,
                ExecutionTime = TimeSpan.Zero
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel workflow {InstanceId}", command.InstanceId);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Workflow Queue Service for host {HostId}", _hostId);
        await base.StopAsync(cancellationToken);
    }
}
