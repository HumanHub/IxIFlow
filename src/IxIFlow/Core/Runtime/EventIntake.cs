using System.Text.Json;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// Takes an instance's pending events at the start of a pass (D-32, D-33). Each event is decided once:
/// consumed by exactly one open wait, or rejected with the reason. An event addressed by key may
/// satisfy only a wait that was open when it arrived; one addressed to a wait may satisfy only that
/// wait. An event received before a wait's deadline still beats the deadline. Decisions are settled
/// by the next commit, together with the checkpoint they produce.
/// </summary>
internal sealed class EventIntake(IServiceProvider services, TimeProvider time)
{
    private const int PageSize = 100;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>Reads and decides the events that arrived since the last read; true when it decided any.</summary>
    public async Task<bool> TakeAsync(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        object workflowData, IExecutionHost host)
    {
        var inbox = checkpoint.Inbox;
        if (!host.PersistsState || !inbox.MayHaveUnread)
            return false;
        var decided = false;
        while (true)
        {
            var page = await host.ReadInboxAsync(inbox.ReadThrough ?? 0, PageSize);
            foreach (var stored in page.Events)
            {
                // An event taken for a child is settled once the child has decided it.
                if (inbox.IsDecided(stored.EventId) || checkpoint.Continuations.Any(item =>
                        item.AcceptedWait?.EventId == stored.EventId))
                    continue;
                await DecideAsync(scopes, checkpoint, workflowData, host, stored, page.OpenWaits);
                decided = true;
            }
            inbox.Read(page.ReadThrough);
            if (page.Events.Count < PageSize)
                return decided;
        }
    }

    private async Task DecideAsync(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        object workflowData, IExecutionHost host, StoredWorkflowEvent stored,
        IReadOnlyDictionary<string, long> openedAt)
    {
        var now = Now;
        var payload = Read(stored);
        if (payload == null)
        {
            Reject(checkpoint, stored, EventRejection.NotMatched);
            return;
        }
        var eventType = payload.GetType();
        // The waits this event is addressed to and that were open for it.
        var addressed = checkpoint.Waits.Where(wait => wait.TableKind != null &&
            (stored.TargetWaitId != null
                ? wait.Id == stored.TargetWaitId
                : (stored.Key == null || wait.Key == stored.Key) &&
                  (wait.TableKind == WaitKind.Child ||
                   openedAt.TryGetValue(wait.Id, out var opened) && opened < stored.Sequence)) &&
            checkpoint.Continuations.Any(item => item.Id == wait.ContinuationId &&
                                                 item.Status == ContinuationStatus.Waiting)).ToList();
        // A wait whose deadline has passed takes only an event received before it.
        var onTime = addressed.Where(wait => wait.DeadlineUtc is not { } deadline || deadline > now ||
                                              stored.ReceivedAtUtc <= deadline).ToList();
        var typed = onTime.Where(wait =>
            WorkflowTypeIdentity.Resolve(wait.EventType)?.IsAssignableFrom(eventType) == true).ToList();
        var matching = typed.Where(wait => Accepts(scopes, checkpoint, wait, payload, workflowData)).ToList();
        if (matching.Count == 1)
        {
            Consume(checkpoint, stored, matching[0], payload);
            return;
        }
        var rejection = matching.Count > 1 ? EventRejection.Ambiguous
            : typed.Count > 0 || onTime.Count > 0 ? EventRejection.NotMatched
            : addressed.Count > 0 ? EventRejection.WaitTimedOut
            : stored.TargetWaitId != null ? await ClosedTargetAsync(checkpoint, host, stored.TargetWaitId)
            : EventRejection.NoOpenWait;
        Reject(checkpoint, stored, rejection);
    }

    private object? Read(StoredWorkflowEvent stored)
    {
        if (WorkflowTypeIdentity.Resolve(stored.EventType) is not { } type)
            return null;
        try
        {
            return JsonSerializer.Deserialize(stored.PayloadJson, type);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // A child decides its own waits' conditions when the event is forwarded to it. A condition that
    // throws does not accept the event; the instance keeps waiting.
    private bool Accepts(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint, WaitState wait,
        object payload, object workflowData)
    {
        if (wait.ChildWorkflowId != null)
            return true;
        var continuation = checkpoint.Continuations.Single(item => item.Id == wait.ContinuationId);
        try
        {
            return WorkflowValueBinding.Matches(scopes.Step(wait.StepId), payload, workflowData,
                continuation.GetPrevious(services));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static void Consume(ExecutionCheckpoint checkpoint, StoredWorkflowEvent stored, WaitState wait,
        object payload)
    {
        var continuation = checkpoint.Continuations.Single(item => item.Id == wait.ContinuationId);
        continuation.AcceptedWait = new AcceptedWaitState
        {
            StepId = wait.StepId,
            Event = SerializedValue.From(payload)!,
            EventId = stored.EventId,
            WaitId = wait.Id,
            Key = wait.ChildWorkflowId == null ? null : wait.Key
        };
        continuation.Status = ContinuationStatus.Active;
        checkpoint.RemoveWaits(item => item.Id == wait.Id, WaitCloseReason.Answered);
        if (wait.ChildWorkflowId == null)
        {
            checkpoint.Inbox.Settle(new EventSettlement(stored.EventId, EventOutcome.Consumed, wait.Id));
            return;
        }
        // The child's other waits are shown again once it has taken the event.
        checkpoint.RemoveWaits(item => item.ChildWorkflowId == wait.ChildWorkflowId &&
                                       item.ContinuationId == continuation.Id, WaitCloseReason.Replaced);
    }

    private static void Reject(ExecutionCheckpoint checkpoint, StoredWorkflowEvent stored, EventRejection rejection) =>
        checkpoint.Inbox.Settle(new EventSettlement(stored.EventId, EventOutcome.Rejected, Rejection: rejection));

    // The named wait is gone: closed by this run, or before the event was read.
    private static async Task<EventRejection> ClosedTargetAsync(ExecutionCheckpoint checkpoint,
        IExecutionHost host, string waitId)
    {
        if (checkpoint.Inbox.ClosureOf(waitId) is { } reason)
            return reason.Rejection();
        return await host.ReadWaitAsync(waitId) is { IsOpen: false } closed ? closed.Rejection
            : EventRejection.UnknownWait;
    }
}
