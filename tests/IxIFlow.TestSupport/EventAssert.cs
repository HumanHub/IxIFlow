using IxIFlow.Core;

namespace IxIFlow.Tests.Infrastructure;

/// <summary>Reads what became of an event an instance's owner decides on its own time (D-33).</summary>
public static class EventAssert
{
    /// <summary>Waits until the instance's owner has decided the event, and returns it.</summary>
    public static async Task<StoredWorkflowEvent> DecidedAsync(IWorkflowInbox inbox, string instanceId,
        string eventId, TimeSpan? patience = null)
    {
        var deadline = DateTime.UtcNow + (patience ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            var stored = await inbox.GetEventAsync(instanceId, eventId);
            if (stored is { Outcome: not EventOutcome.Pending })
                return stored;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(
                    $"Event '{eventId}' of instance '{instanceId}' is {stored?.Outcome.ToString() ?? "missing"}");
            await Task.Delay(20);
        }
    }

    /// <summary>The event was taken by a wait.</summary>
    public static void Consumed(WorkflowExecutionResult result) =>
        Assert.Equal(EventOutcome.Consumed, Assert.IsType<EventReceipt>(result.Event).Outcome);

    /// <summary>The event was refused for this reason.</summary>
    public static void Rejected(WorkflowExecutionResult result, EventRejection rejection)
    {
        var receipt = Assert.IsType<EventReceipt>(result.Event);
        Assert.Equal((EventOutcome.Rejected, rejection), (receipt.Outcome, receipt.Rejection));
    }

    /// <summary>The instance finished before the event was taken.</summary>
    public static void Closed(WorkflowExecutionResult result) =>
        Assert.Equal(EventOutcome.Closed, Assert.IsType<EventReceipt>(result.Event).Outcome);
}
