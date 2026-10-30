namespace IxIFlow.Core.Runtime;

internal enum StepFailureAction
{
    Propagate,
    Retry,
    Ignore
}

internal sealed record StepFailureDecision(StepFailureAction Action, int? PolicyIndex = null, int RetryNumber = 0);

internal static class StepErrorPolicies
{
    public static StepFailureDecision Handle(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState continuation, Exception error, DateTime now, KeptActivityInputs? failedInputs)
    {
        var position = continuation.Stack[^1];
        var step = scopes.Steps(position.ScopeId).ElementAtOrDefault(position.NextStepIndex);
        var record = InvocationRecords.Active(checkpoint, continuation);

        var eligible = step?.StepType == WorkflowStepType.WorkflowInvocation ||
            step?.StepType == WorkflowStepType.Activity && SagaScopeTransitions.ForwardOwner(scopes, continuation, step) != null;

        var handlers = eligible ? step!.StepMetadata.GetValueOrDefault("StepErrorHandlers") as List<StepErrorHandlerInfo> : null;
        var index = handlers?.FindIndex(candidate => candidate.ExceptionType.IsAssignableFrom(SerializedException.CatchType(error))) ?? -1;
        var handler = index < 0 ? null : handlers![index];
        var retries = record?.ScheduledRetries ?? position.StepRetryCounts.GetValueOrDefault(position.NextStepIndex);

        var action = handler?.HandlerAction switch
        {
            StepErrorAction.Ignore => StepFailureAction.Ignore,
            StepErrorAction.Retry when retries < (handler.RetryPolicy?.MaximumAttempts ?? 0) => StepFailureAction.Retry,
            _ => StepFailureAction.Propagate
        };

        if (record != null)
        {
            checkpoint.TouchInvocation(record.Id);
            record.SelectedPolicyIndex = index < 0 ? null : index;
            var attempt = record.Attempts[^1];
            attempt.SelectedPolicyIndex = record.SelectedPolicyIndex;
            attempt.Action = handler?.HandlerAction;

            attempt.Disposition = action == StepFailureAction.Propagate && handler?.HandlerAction == StepErrorAction.Retry
                ? "Exhausted"
                : action.ToString();
        }

        if (action == StepFailureAction.Retry)
        {
            continuation.RetryAfterUtc = RetrySchedule.NextAttemptUtc(handler!.RetryPolicy, retries + 1, now);

            if (record != null)
            {
                record.ScheduledRetries = retries + 1;
                record.RetryAfterUtc = continuation.RetryAfterUtc;
                record.Attempts[^1].RetryAfterUtc = continuation.RetryAfterUtc;
            }
            else
            {
                position.StepRetryCounts[position.NextStepIndex] = retries + 1;
                continuation.RetryInputs = failedInputs;
            }
        }
        else
        {
            if (record != null)
                InvocationRecords.Close(checkpoint, continuation, record.Attempts[^1].Disposition!, now);

            if (action == StepFailureAction.Ignore)
            {
                continuation.RetryAfterUtc = null;
                position.StepRetryCounts.Remove(position.NextStepIndex);
                position.NextStepIndex++;
                continuation.SetPrevious(null);
            }
        }

        if (record != null && checkpoint.Trace.Accepts(TraceEntryType.InvocationPolicyApplied, now))
            checkpoint.Trace.Add(new ExecutionTraceEntry
            {
                Timestamp = now, Clock = TraceClock.Store, EntryType = TraceEntryType.InvocationPolicyApplied,
                StepId = record.StepId, ContinuationId = record.ContinuationId, FrameActivationId = record.FrameActivationId,
                Payload = InvocationRecords.Facts(record, record.Attempts[^1])
            });

        return new(action, index < 0 ? null : index, action == StepFailureAction.Retry ? retries + 1 : 0);
    }
}