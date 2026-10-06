using System.Runtime.ExceptionServices;

namespace IxIFlow.Core;

/// <summary>Publishes terminal results that were committed before their event was sent.</summary>
public sealed class WorkflowCompletionPublisher
{
    private readonly IWorkflowCompletionOutbox _outbox;
    private readonly IMessageBus _bus;
    private readonly string _hostId;

    public WorkflowCompletionPublisher(IWorkflowStateRepository repository,
        IMessageBus bus, string hostId)
    {
        _outbox = repository as IWorkflowCompletionOutbox
            ?? throw new InvalidOperationException(
                "Distributed hosting requires a workflow repository with a completion outbox");
        _bus = bus;
        _hostId = hostId;
    }

    public async Task PublishPendingAsync(string? instanceId = null)
    {
        var token = Guid.NewGuid().ToString("N");
        var pending = await _outbox.ClaimUnpublishedCompletionsAsync(
            instanceId, token, TimeSpan.FromMinutes(5));
        var claimed = pending.ToArray();
        var errors = new List<Exception>();
        foreach (var instance in claimed)
        {
            try
            {
                await _bus.PublishAsync(new WorkflowExecutionCompletedEvent
                {
                    InstanceId = instance.InstanceId,
                    HostId = _hostId,
                    Status = instance.Status switch
                    {
                        WorkflowStatus.Completed => WorkflowExecutionStatus.Success,
                        WorkflowStatus.Cancelled => WorkflowExecutionStatus.Cancelled,
                        WorkflowStatus.Terminated => WorkflowExecutionStatus.Failed,
                        _ => WorkflowExecutionStatus.Faulted
                    },
                    ErrorMessage = instance.LastError,
                    CompletedAt = instance.CompletedAt ?? DateTime.UtcNow,
                    ExecutionTime = instance.CompletedAt.HasValue && instance.StartedAt.HasValue
                        ? instance.CompletedAt.Value - instance.StartedAt.Value
                        : TimeSpan.Zero
                });
                if (!await _outbox.MarkCompletionPublishedAsync(
                        instance.InstanceId, instance.Revision, token))
                    throw new InvalidOperationException(
                        $"Completion claim for '{instance.InstanceId}' was lost before publication was recorded");
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
            finally
            {
                try
                {
                    await _outbox.ReleaseCompletionClaimAsync(instance.InstanceId, token);
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
            }
        }
        if (errors.Count == 1)
            ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1)
            throw new AggregateException("Completion publication failed", errors);
    }
}
