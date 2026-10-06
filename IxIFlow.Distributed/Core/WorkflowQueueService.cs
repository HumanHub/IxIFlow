using IxIFlow.Core.Runtime;
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
    private readonly TimeSpan _unmatchedCommandRetention;

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
        _unmatchedCommandRetention = hostOptions?.UnmatchedCommandRetention
            ?? TimeSpan.FromHours(1);
        if (_unmatchedCommandRetention <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(hostOptions),
                "Unmatched command retention must be positive");
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

            // A completed consumer is an error unless host shutdown requested it.
            var completed = await Task.WhenAny(executeCommandTask, resumeCommandTask, cancelCommandTask);
            await completed;
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

        await foreach (var delivery in ConsumeCommandsAsync<ExecuteWorkflowCommand>(cancellationToken))
        {
            var command = delivery.Message;
            try
            {
                // Only process commands targeted at this host
                if (command.TargetHostId != _hostId)
                    continue;

                _logger.LogInformation("Processing execute command {InstanceId} for host {HostId}", 
                    command.InstanceId, _hostId);

                await ProcessExecuteCommandAsync(command, cancellationToken);
                await delivery.AcknowledgeAsync();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (WorkflowInstanceBusyException error)
            {
                await DeferIfOwnedAsync(delivery, error);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing execute command {InstanceId} for host {HostId}", 
                    command.InstanceId, _hostId);
                await RejectIfOwnedAsync(delivery, ex);
            }
        }
    }

    private async Task ProcessResumeCommandsAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Starting to process resume commands for host {HostId}", _hostId);

        await foreach (var delivery in ConsumeCommandsAsync<ResumeWorkflowCommand>(cancellationToken))
        {
            var command = delivery.Message;
            try
            {
                // Only process commands targeted at this host
                if (command.TargetHostId != _hostId)
                    continue;

                _logger.LogInformation("Processing resume command {InstanceId} for host {HostId}", 
                    command.InstanceId, _hostId);

                await ProcessResumeCommandAsync(command, cancellationToken);
                await delivery.AcknowledgeAsync();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (WorkflowInstanceBusyException error)
            {
                await DeferIfOwnedAsync(delivery, error);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing resume command {InstanceId} for host {HostId}", 
                    command.InstanceId, _hostId);
                await RejectIfOwnedAsync(delivery, ex);
            }
        }
    }

    private async Task ProcessCancelCommandsAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Starting to process cancel commands for host {HostId}", _hostId);

        await foreach (var delivery in ConsumeCommandsAsync<CancelWorkflowCommand>(cancellationToken))
        {
            var command = delivery.Message;
            try
            {
                // Only process commands targeted at this host
                if (command.TargetHostId != _hostId)
                    continue;

                _logger.LogInformation("Processing cancel command {InstanceId} for host {HostId}", 
                    command.InstanceId, _hostId);

                await ProcessCancelCommandAsync(command, cancellationToken);
                await delivery.AcknowledgeAsync();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (WorkflowInstanceBusyException error)
            {
                await DeferIfOwnedAsync(delivery, error);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing cancel command {InstanceId} for host {HostId}", 
                    command.InstanceId, _hostId);
                await RejectIfOwnedAsync(delivery, ex);
            }
        }
    }

    private async IAsyncEnumerable<IMessageDelivery<T>> ConsumeCommandsAsync<T>(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        where T : class
    {
        if (_messageBus is IAcknowledgingMessageBus acknowledgingBus)
        {
            await foreach (var delivery in acknowledgingBus.ConsumeDeliveriesAsync<T>(_hostId, cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                yield return delivery;
            }
        }
        else
        {
            await foreach (var message in _messageBus.ConsumeAsync<T>().WithCancellation(cancellationToken))
            {
                yield return new ImmediateDelivery<T>(message);
            }
        }
    }

    private sealed class ImmediateDelivery<T>(T message) : IMessageDelivery<T> where T : class
    {
        public T Message => message;
        public Task AcknowledgeAsync() => Task.CompletedTask;
        public Task RejectAsync(Exception error) => Task.CompletedTask;
        public Task DeferAsync(TimeSpan delay) => Task.CompletedTask;
    }

    private async Task DeferIfOwnedAsync<T>(IMessageDelivery<T> delivery,
        WorkflowInstanceBusyException error) where T : class
    {
        try
        {
            await delivery.DeferAsync(error.RetryDelay);
        }
        catch (MessageClaimLostException claimError)
        {
            _logger.LogWarning(claimError, "Message claim was lost before deferral");
        }
    }

    private async Task RejectIfOwnedAsync<T>(IMessageDelivery<T> delivery, Exception error)
        where T : class
    {
        try
        {
            await delivery.RejectAsync(error);
        }
        catch (MessageClaimLostException claimError)
        {
            _logger.LogWarning(claimError, "Message claim was lost before rejection");
        }
    }

    internal async Task ProcessExecuteCommandAsync(ExecuteWorkflowCommand command,
        CancellationToken cancellationToken = default)
    {
        using var scope = _serviceProvider.CreateScope();
        var workflowEngine = scope.ServiceProvider.GetRequiredService<IWorkflowEngine>();
        var versionRegistry = scope.ServiceProvider.GetRequiredService<IWorkflowVersionRegistry>();
        var repository = scope.ServiceProvider.GetRequiredService<IWorkflowStateRepository>();

        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(command.InstanceId);
            var definition = await versionRegistry.GetWorkflowDefinitionAsync(command.WorkflowName, command.WorkflowVersion)
                ?? throw new InvalidOperationException($"Workflow definition not found: {command.WorkflowName} v{command.WorkflowVersion}");

            // Deserialize workflow data
            var workflowDataType = WorkflowTypeIdentity.Resolve(command.WorkflowDataType);
            if (workflowDataType == null)
            {
                throw new InvalidOperationException($"Could not resolve workflow data type: {command.WorkflowDataType}");
            }

            if (definition.WorkflowDataType != workflowDataType)
            {
                throw new InvalidOperationException($"Workflow data type does not match {command.WorkflowName} v{command.WorkflowVersion}");
            }

            var workflowData = System.Text.Json.JsonSerializer.Deserialize(command.WorkflowDataJson, workflowDataType)
                ?? throw new InvalidOperationException("Workflow data cannot be null");

            // Execute workflow
            var options = command.Options ?? new WorkflowOptions();
            if (options.InstanceId != null && options.InstanceId != command.InstanceId)
                throw new InvalidOperationException("The command and workflow options have different instance IDs");
            options.InstanceId = command.InstanceId;
            if (await repository.GetWorkflowInstanceAsync(command.InstanceId) == null)
            {
                await _messageBus.PublishAsync(new WorkflowExecutionStartedEvent
                {
                    InstanceId = command.InstanceId,
                    HostId = _hostId,
                    WorkflowName = command.WorkflowName,
                    StartedAt = DateTime.UtcNow
                });
            }
            var result = await workflowEngine.ExecuteWorkflowAsync(
                definition, workflowData, options, cancellationToken);
            if (result.Status == WorkflowExecutionStatus.Running)
            {
                var saved = await repository.GetWorkflowInstanceAsync(command.InstanceId);
                if (saved?.Status != WorkflowStatus.Running)
                    result = await workflowEngine.ExecuteWorkflowAsync(
                        definition, workflowData, options, cancellationToken);
                if (result.Status == WorkflowExecutionStatus.Running)
                    throw new WorkflowInstanceBusyException(
                        $"Workflow instance '{command.InstanceId}' is executing; keep the command for redelivery");
            }

            if (IsTerminal(result.Status))
                await PublishCompletionAsync(command.InstanceId);

            _logger.LogInformation("Successfully executed workflow {InstanceId} with status {Status}", 
                command.InstanceId, result.Status);
        }
        catch (WorkflowInstanceBusyException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute workflow {InstanceId}", command.InstanceId);
            throw;
        }
    }

    internal async Task ProcessResumeCommandAsync(ResumeWorkflowCommand command,
        CancellationToken cancellationToken = default)
    {
        if (IsExpired(command.QueuedAt))
            throw new InvalidOperationException(
                $"Resume command for '{command.InstanceId}' expired without an accepting wait");
        using var scope = _serviceProvider.CreateScope();
        var workflowEngine = scope.ServiceProvider.GetRequiredService<IWorkflowEngine>();
        var repository = scope.ServiceProvider.GetRequiredService<IWorkflowStateRepository>();

        try
        {
            // Deserialize event data
            var eventDataType = WorkflowTypeIdentity.Resolve(command.EventDataType);
            if (eventDataType == null)
            {
                throw new InvalidOperationException($"Could not resolve event data type: {command.EventDataType}");
            }

            var eventData = System.Text.Json.JsonSerializer.Deserialize(command.EventDataJson, eventDataType)
                ?? throw new InvalidOperationException("Resume event data cannot be null");

            // Resume workflow
            var result = await workflowEngine.ResumeWorkflowDeliveryAsync(
                command.InstanceId, command.Key, eventData, command.CommandId, cancellationToken);
            if (!result.EventAccepted)
            {
                var saved = await repository.GetWorkflowInstanceAsync(command.InstanceId);
                if (saved == null || saved.Status is WorkflowStatus.Running or
                    WorkflowStatus.Suspended or WorkflowStatus.NeedsResolution)
                    throw new WorkflowInstanceBusyException(
                        $"Workflow instance '{command.InstanceId}' has not accepted this event yet",
                        TimeSpan.FromSeconds(10));
                await PublishCompletionAsync(command.InstanceId);
                throw new InvalidOperationException(
                    $"Workflow instance '{command.InstanceId}' ended without accepting this event");
            }

            // Publish completion event if workflow finished
            if (IsTerminal(result.Status))
                await PublishCompletionAsync(command.InstanceId);

            _logger.LogInformation("Successfully resumed workflow {InstanceId} with status {Status}", 
                command.InstanceId, result.Status);
        }
        catch (WorkflowInstanceBusyException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resume workflow {InstanceId}", command.InstanceId);
            throw;
        }
    }

    internal async Task ProcessCancelCommandAsync(CancelWorkflowCommand command,
        CancellationToken cancellationToken = default)
    {
        using var scope = _serviceProvider.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IWorkflowEngine>();
        WorkflowExecutionResult result;
        try
        {
            result = await engine.CancelWorkflowAsync(
                command.InstanceId, command.Reason, cancellationToken);
        }
        catch (KeyNotFoundException) when (!IsExpired(command.QueuedAt))
        {
            throw new WorkflowInstanceBusyException(
                $"Cancellation for '{command.InstanceId}' arrived before its execute command",
                TimeSpan.FromSeconds(10));
        }
        if (!IsTerminal(result.Status))
            return;
        await PublishCompletionAsync(command.InstanceId);
    }

    private bool IsExpired(DateTime queuedAt) =>
        DateTime.UtcNow - queuedAt.ToUniversalTime() > _unmatchedCommandRetention;

    private async Task PublishCompletionAsync(string instanceId)
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWorkflowStateRepository>();
        await new WorkflowCompletionPublisher(repository, _messageBus, _hostId)
            .PublishPendingAsync(instanceId);
    }

    private static bool IsTerminal(WorkflowExecutionStatus status) => status is
        WorkflowExecutionStatus.Success or WorkflowExecutionStatus.Faulted or
        WorkflowExecutionStatus.Failed or WorkflowExecutionStatus.TimedOut or
        WorkflowExecutionStatus.Cancelled;

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping Workflow Queue Service for host {HostId}", _hostId);
        await base.StopAsync(cancellationToken);
    }
}

internal sealed class WorkflowInstanceBusyException(string message, TimeSpan? retryDelay = null)
    : Exception(message)
{
    public TimeSpan RetryDelay { get; } = retryDelay ?? TimeSpan.FromSeconds(1);
}
