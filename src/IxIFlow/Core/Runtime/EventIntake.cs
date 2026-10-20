using JsonSerializer = IxIFlow.Core.Runtime.WorkflowJson;
using System.Text.Json;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// Takes an instance's pending events at the start of a pass. Each event is decided once:
/// consumed by exactly one open wait, or rejected with the reason. An event addressed by key may
/// satisfy only a wait that was open when it arrived; one addressed to a wait may satisfy only that
/// wait. The store's deadlines and its time are read with the events, so an event received before a
/// wait's deadline still beats the deadline. Decisions are settled by the next commit, together
/// with the checkpoint they produce.
/// </summary>
internal sealed class EventIntake(IServiceProvider services, TimeProvider time)
{
    private const int PageSize = 100;

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Reads and decides the events that arrived since the last read, and records which deadlines the
    /// store found due; true when it decided any event. A due execution deadline is recorded before any
    /// event is decided, and the events stay unread until the run has applied it.
    /// </summary>
    public async Task<bool> TakeAsync(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ExecutionSession host, WorkflowInstance instance)
    {
        var inbox = checkpoint.Inbox;
        if (!inbox.MayHaveUnread)
            return false;
        checkpoint.CloseEndedRequests();
        var decided = false;
        while (true)
        {
            var page = await host.ReadInboxAsync(inbox.ReadThrough ?? 0, PageSize);
            if (page.StoreTimeUtc is { } storeTime)
                checkpoint.Clock.Observe(storeTime, Now);
            checkpoint.ReadDeadlines(page);
            // A cancellation request the host learned of meanwhile goes first; the events stay unread.
            if (ContinuationTransitions.CancellationPending(checkpoint))
                return decided;
            if (checkpoint.Debug.Stepping == null && checkpoint.ExecutionTimerOpen && page.IsDue(checkpoint.ExecutionTimerId!))
            {
                inbox.Due([checkpoint.ExecutionTimerId!]);
                return decided;
            }
            foreach (var stored in page.Events)
            {
                if (inbox.IsDecided(stored.EventId))
                    continue;
                if (checkpoint.Debug.Stepping != null)
                {
                    // Defer the entire event if it addresses any held branch. Do not run its
                    // predicate, reject it, or turn an ambiguous delivery into a single match.
                    var addressed = checkpoint.Waits.Where(w => stored.TargetWaitId != null
                        ? w.Id == stored.TargetWaitId
                        : w.TableKind == WaitKind.Event && (stored.Key == null || w.Key == stored.Key) &&
                          page.OpenWaits.TryGetValue(w.Id, out var opened) && opened < stored.Sequence).ToArray();
                    if (addressed.Length == 0 || addressed.Any(w => !SteppingPolicy.Allows(checkpoint, w.ContinuationId)))
                        continue;
                }
                await DecideAsync(scopes, checkpoint, host, stored, page, instance);
                decided = true;
            }
            inbox.Read(page.ReadThrough);
            // Only a page that reads to the end of the inbox has seen every event received before a
            // deadline it finds due.
            if (page.Events.Count < PageSize)
            {
                inbox.Due(page.Deadlines?.Keys.Where(page.IsDue) ?? []);
                return decided;
            }
        }
    }

    private async Task DecideAsync(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ExecutionSession host, StoredWorkflowEvent stored, InboxPage page, WorkflowInstance instance)
    {
        await host.Reader.ReadAsync(stored.PayloadJson);
        var payload = Read(stored);
        if (payload == null)
        {
            Reject(instance, checkpoint, stored, EventRejection.NotMatched);
            return;
        }
        var eventType = payload.GetType();
        // The waits this event is addressed to and that were open for it. A request's answer is
        // addressed by its token only, and is taken even before its WaitFor is reached; a child's
        // completion only by the wait its parent opened for it.
        var addressed = checkpoint.Waits.Where(wait => wait.Answerable &&
            (stored.TargetWaitId != null
                ? wait.Id == stored.TargetWaitId
                : wait.TableKind == WaitKind.Event && (stored.Key == null || wait.Key == stored.Key) &&
                  page.OpenWaits.TryGetValue(wait.Id, out var opened) && opened < stored.Sequence) &&
            checkpoint.Continuations.Any(item => item.Id == wait.ContinuationId &&
                (item.Status == ContinuationStatus.Waiting || wait.IsReply &&
                 item.Status is not (ContinuationStatus.Completed or ContinuationStatus.Cancelled)))).ToList();
        // A wait the store found due takes only an event received before its deadline; both are the
        // store's times (D-35).
        var onTime = addressed.Where(wait => !page.IsDue(wait.Id) ||
                                              stored.ReceivedAtUtc < page.Deadlines![wait.Id]).ToList();
        var typed = onTime.Where(wait =>
            WorkflowTypeIdentity.Resolve(wait.EventType)?.IsAssignableFrom(eventType) == true).ToList();
        // An answer can confirm only what its request was about.
        var aboutIt = typed.Where(wait => wait.Subject == null ||
                                          string.Equals(wait.Subject, stored.Subject, StringComparison.Ordinal))
            .ToList();
        foreach (var wait in aboutIt)
            await host.PrepareReadAsync(checkpoint, checkpoint.ContinuationById(wait.ContinuationId)!, scopes, restoreInputs: false);
        var matching = aboutIt.Where(wait => Accepts(scopes, checkpoint, wait, payload)).ToList();
        if (matching.Count == 1)
        {
            if (payload is ChildCompleted)
            {
                var invocation = checkpoint.Continuations.Single(item => item.Id == matching[0].ContinuationId).PendingActivity!;
                var step = scopes.Step(invocation.StepId);
                using var childLease = invocation.ChildBundleDigest == null ? null
                    : await ((IWorkflowDefinitionSource)services.GetService(typeof(IWorkflowDefinitionSource))!)
                        .AcquireWorkflowDefinitionAsync(step.WorkflowName!, step.WorkflowVersion!.Value, invocation.ChildBundleDigest,
                            checkpoint.Signals?.AcquisitionToken ?? default);
                using var childTypes = WorkflowTypeIdentity.Enter(childLease?.Definition.BundleContext);
                if (WorkflowTypeIdentity.Resolve(invocation.ChildDataType) == null)
                    throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                        $"Child data type '{invocation.ChildDataType}' is unavailable");
            }
            Consume(scopes, checkpoint, stored, matching[0], payload);
            Record(instance, checkpoint, stored, matching[0], null);
            return;
        }
        var rejection = matching.Count > 1 ? EventRejection.Ambiguous
            : typed.Count > 0 && aboutIt.Count == 0 ? EventRejection.SubjectMismatch
            : typed.Count > 0 || onTime.Count > 0 ? EventRejection.NotMatched
            : addressed.Count > 0 ? EventRejection.WaitTimedOut
            : stored.TargetWaitId != null ? await ClosedTargetAsync(checkpoint, host, stored.TargetWaitId)
            : EventRejection.NoOpenWait;
        Reject(instance, checkpoint, stored, rejection);
    }

    private object? Read(StoredWorkflowEvent stored)
    {
        if (WorkflowTypeIdentity.Resolve(stored.EventType) is not { } type)
            return null;
        try
        {
            return JsonSerializer.Deserialize(stored.PayloadJson.InlineText, type);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // A child's completion has no condition. A condition that throws does not accept the event; the
    // instance keeps waiting.
    private bool Accepts(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint, WaitState wait,
        object payload)
    {
        if (wait.TableKind == WaitKind.Child)
            return payload is ChildCompleted completed && completed.ChildInstanceId == wait.ChildWorkflowId;
        var continuation = checkpoint.Continuations.Single(item => item.Id == wait.ContinuationId);
        try
        {
            return WorkflowValueBinding.Matches(scopes.Step(wait.StepId), payload, checkpoint.DataOf(continuation),
                continuation.GetPrevious(services), ForEachTransitions.ItemsOf(scopes, checkpoint, continuation));
        }
        catch (Exception error) when (error is not OutOfMemoryException &&
                                      error is not WorkflowEngineException { Kind: WorkflowErrorKind.PayloadUnavailable })
        {
            return false;
        }
    }

    private static void Consume(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        StoredWorkflowEvent stored, WaitState wait, object payload)
    {
        var continuation = checkpoint.Continuations.Single(item => item.Id == wait.ContinuationId);
        checkpoint.Touch(continuation);
        // A saga request's compensation, and a retry that keeps the request, read the answer it got (D-36).
        if (wait.IsReply)
            SagaScopeTransitions.NoteReply(continuation, wait.InvocationId, SerializedValue.From(payload)!,
                stored.EventId, wait.Id);
        if (wait.IsReply && !IsAt(scopes, continuation, wait))
        {
            // The request's WaitFor is not reached yet: the answer is kept for it.
            checkpoint.RemoveWaits(item => item.Id == wait.Id, WaitCloseReason.Answered);
            checkpoint.BoundReplies.Add(new BoundReplyState
            {
                WaitId = wait.Id,
                EventId = stored.EventId,
                ContinuationId = wait.ContinuationId,
                StepId = wait.StepId,
                ScopeActivationId = wait.ScopeActivationId ?? "",
                InvocationId = wait.InvocationId,
                Subject = wait.Subject,
                Event = SerializedValue.From(payload)!
            });
            checkpoint.Inbox.Settle(new EventSettlement(stored.EventId, EventOutcome.Consumed, wait.Id));
            return;
        }
        continuation.AcceptedWait = new AcceptedWaitState
        {
            StepId = wait.StepId,
            Event = SerializedValue.From(payload)!,
            EventId = stored.EventId,
            WaitId = wait.Id
        };
        continuation.Status = ContinuationStatus.Active;
        checkpoint.RemoveWaits(item => item.Id == wait.Id, WaitCloseReason.Answered);
        checkpoint.Inbox.Settle(new EventSettlement(stored.EventId, EventOutcome.Consumed, wait.Id));
    }

    // A continuation waits at a request's WaitFor when its current step is that WaitFor.
    private static bool IsAt(WorkflowScopeCatalog scopes, ContinuationState continuation, WaitState wait)
    {
        if (continuation.Status != ContinuationStatus.Waiting)
            return false;
        var position = continuation.Stack[^1];
        var steps = scopes.Steps(position.ScopeId);
        return position.NextStepIndex < steps.Count && scopes.Id(steps[position.NextStepIndex]) == wait.StepId;
    }

    private void Reject(WorkflowInstance instance, ExecutionCheckpoint checkpoint, StoredWorkflowEvent stored,
        EventRejection rejection)
    {
        checkpoint.Inbox.Settle(new EventSettlement(stored.EventId, EventOutcome.Rejected, Rejection: rejection));
        Record(instance, checkpoint, stored, null, rejection);
    }

    private void Record(WorkflowInstance instance, ExecutionCheckpoint checkpoint, StoredWorkflowEvent stored,
        WaitState? wait, EventRejection? refusal)
    {
        if (checkpoint.Trace.Accepts(refusal == null ? TraceEntryType.EventReceived : TraceEntryType.EventRefused, Now))
            checkpoint.Trace.Add(new ExecutionTraceEntry
            {
                Timestamp = Now,
                EntryType = refusal == null ? TraceEntryType.EventReceived : TraceEntryType.EventRefused,
                Payload = new ReceivedEventTrace(stored.EventId, stored.Key ?? wait?.Key, stored.EventType,
                    (wait?.Id ?? stored.TargetWaitId) is { } waitId
                        ? WaitReference.Of(instance.InstanceId, waitId) : null, wait?.StepId, refusal)
            });
    }

    // The named wait is gone: closed by this run, or before the event was read.
    private static async Task<EventRejection> ClosedTargetAsync(ExecutionCheckpoint checkpoint,
        ExecutionSession host, string waitId)
    {
        if (checkpoint.Inbox.ClosureOf(waitId) is { } reason)
            return reason.Rejection();
        return await host.ReadWaitAsync(waitId) is { IsOpen: false } closed ? closed.Rejection
            : EventRejection.UnknownWait;
    }
}
