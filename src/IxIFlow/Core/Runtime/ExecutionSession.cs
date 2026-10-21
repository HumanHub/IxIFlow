namespace IxIFlow.Core.Runtime;

/// <summary>Executor-local state for one assignment, including its last acknowledged commit.</summary>
internal sealed class ExecutionSession(IExecutionHost host, Guid assignmentId, string instanceId,
    long epoch, ExecutionLimits limits, bool canPark)
{
    private readonly PayloadBudget _budget = new(limits.MaxHydratedPayloadBytes);
    private CheckpointPayloads? _payloads;
    private PayloadReader? _reader;
    public CheckpointPayloads Payloads
    {
        get
        {
            if (_payloads != null) return _payloads;
            _payloads = new(Limits, _budget);
            if (Limits.PayloadThresholdBytes != 0 && SavedInstance?.SavedParts is { } parts)
                _payloads.Known(CheckpointPayloads.References(SavedInstance.ExecutionStateJson, parts));
            return _payloads;
        }
    }
    public PayloadReader Reader => _reader ??= new(ReadPayloadAsync, _budget);
    public Guid AssignmentId { get; } = assignmentId;
    public string InstanceId { get; } = instanceId;
    public long Epoch { get; } = epoch;
    public ExecutionLimits Limits { get; } = limits;
    public bool CanPark { get; } = canPark;
    public AcknowledgedCommit? LastAcknowledgedCommit { get; private set; }
    public WorkflowInstance? SavedInstance { get; set; }
    public bool NeedsHydration { get; set; }

    public async Task<CheckpointReply> CheckpointAsync(CheckpointCommit commit)
    {
        var payloads = (commit.PreservesPayloadPins ? [] : Payloads.Pending)
            .Select(value => new PayloadBody(value.Identity, value.Encoded.ToArray(), value.Dependencies)).ToArray();
        var reply = await host.CheckpointAsync(new(AssignmentId, commit, payloads));
        Check(reply.AssignmentId, reply.Conflict);
        if (reply.Kind == CheckpointReplyKind.Ack)
        {
            LastAcknowledgedCommit = new(commit.CommitId, reply.Revision);
            var parts = commit.ApplyParts(SavedInstance?.SavedParts);
            SavedInstance = commit.ReadTrunk();
            SavedInstance.Incarnation = commit.Incarnation;
            SavedInstance.WorkflowDataJson = parts.GetValueOrDefault(CheckpointParts.DataKey) ?? StoredJson.Inline("");
            SavedInstance.Saved(parts, reply.Revision);
            Payloads.Acknowledged();
            // The immutable commit already carries the complete owner set. Reusing it also avoids
            // reparsing the trunk and inline parts on every acknowledgement when claim check is off.
            Payloads.Known(commit.PayloadReferences);
        }
        return reply;
    }

    public async Task PrepareReadAsync(ExecutionCheckpoint checkpoint, ContinuationState continuation, WorkflowScopeCatalog scopes,
        bool restoreInputs = true)
    {
        if (!NeedsHydration) return;
        await PrepareDataReadAsync(checkpoint, continuation);
        if (continuation.RuntimePrevious == null) await Reader.HydrateAsync(continuation.Previous);
        if (restoreInputs)
        {
            await Reader.HydrateAsync(continuation.PendingActivity);
            await Reader.HydrateAsync(continuation.RetryInputs);
            await Reader.HydrateAsync(continuation.AcceptedWait);
        }
        await Reader.HydrateAsync(continuation.InheritedItems);
        for (var index = 0; index < continuation.Stack.Count; index++)
        {
            var frame = continuation.Stack[index];
            if (frame.ForEachIndex is { } item && index > 0 && continuation.Stack[index - 1].ForEach is { } owner)
                await Reader.ReadAsync(owner.Items[item]);
            if (restoreInputs && index == continuation.Stack.Count - 1 && frame.ForEach is { } list &&
                (list.Joined || list.Next >= list.Items.Count))
                await Reader.HydrateAsync(list.Results);
            // Compensation and error paths read the saved saga/try state, never queued list items.
            if (restoreInputs && frame.SagaState?.Phase == SagaPhase.Compensating)
                await Reader.HydrateAsync(frame.SagaState);
            if (restoreInputs && frame.TryState?.Phase is TryPhase.Catch or TryPhase.Finally)
                await Reader.HydrateAsync(frame.TryState);
            if (restoreInputs && frame.SagaState is { Phase: SagaPhase.Forward, RetryCount: > 0 } saga &&
                saga.AcceptedWaitCursor < saga.AcceptedWaits.Count)
            {
                var current = continuation.Stack[^1];
                var next = scopes.Steps(current.ScopeId).ElementAtOrDefault(current.NextStepIndex);
                if (next != null && saga.AcceptedWaits[saga.AcceptedWaitCursor].StepId == scopes.Id(next))
                    await Reader.HydrateAsync(saga.AcceptedWaits[saga.AcceptedWaitCursor].Event);
            }
        }
        if (continuation.ParentJoinId is { } joinId && continuation.Stack.Count == 1 &&
            continuation.Stack[0].NextStepIndex == scopes.Steps(continuation.Stack[0].ScopeId).Count &&
            checkpoint.Joins.Single(j => j.Id == joinId) is { Mode: ParallelJoinMode.WaitConditionally } join)
        {
            await PrepareDataReadAsync(checkpoint, checkpoint.ContinuationById(join.ParentContinuationId)!);
            foreach (var sibling in checkpoint.BranchesOf(join).Where(c => c.Status == ContinuationStatus.Completed))
                await PrepareDataReadAsync(checkpoint, sibling);
            var parent = checkpoint.ContinuationById(join.ParentContinuationId)!;
            foreach (var frame in parent.Stack) await Reader.HydrateAsync(frame.ForEach?.Results);
            await Reader.HydrateAsync(continuation.Stack[0].ForEachResult);
        }
    }

    public async Task PrepareDataReadAsync(ExecutionCheckpoint checkpoint, ContinuationState branch)
    {
        if (!NeedsHydration || branch.ParentJoinId == null || branch.Data != null) return;
        await PrepareDataReadAsync(checkpoint, checkpoint.ForkParent(branch));
        await Reader.HydrateAsync(branch.DataChanges);
    }

    private async Task<ReadOnlyMemory<byte>> ReadPayloadAsync(PayloadReference reference)
    {
        if (reference.Bytes > Limits.MaxPayloadBytes || reference.Bytes > int.MaxValue)
            throw new PayloadHydrationLimitException(Limits.MaxPayloadBytes);
        var reply = await host.ReadPayloadAsync(new(AssignmentId, InstanceId, reference));
        Check(reply.AssignmentId, reply.Conflict);
        if (reply.Refusal is { } refusal)
            throw new WorkflowEngineException(refusal, $"Payload {reference.Digest} could not be read");
        return reply.Body ?? throw new InvalidDataException("Missing payload body");
    }

    public async Task<StartChildReply> StartChildAsync(string stepId, ChildStart start, string hash, string contractHash)
    {
        var reply = await host.StartChildAsync(new(AssignmentId, InstanceId, Epoch, stepId, start, hash, contractHash));
        Check(reply.AssignmentId, reply.Conflict);
        return reply;
    }

    public async Task<InboxPage> ReadInboxAsync(long afterSequence, int limit)
    {
        var reply = await host.ReadInboxAsync(new(AssignmentId, InstanceId, afterSequence, limit));
        Check(reply.AssignmentId, reply.Conflict);
        return reply.Page ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "Missing inbox page");
    }

    public async Task<StoredWait?> ReadWaitAsync(string waitId)
    {
        var reply = await host.ReadWaitAsync(new(AssignmentId, InstanceId, waitId));
        Check(reply.AssignmentId, reply.Conflict);
        return reply.Wait;
    }

    private void Check(Guid identity, ConflictReason? conflict)
    {
        if (identity != AssignmentId || conflict == ConflictReason.NotAssigned)
            throw new ExecutionOwnershipLostException(new InvalidOperationException("The request is not assigned"));
    }
}

internal sealed class CheckpointInterruptedException(Exception? error) : Exception(
    "The checkpoint was not acknowledged; recovery must reread the stored instance", error);
