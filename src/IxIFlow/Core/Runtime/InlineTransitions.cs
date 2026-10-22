namespace IxIFlow.Core.Runtime;

internal static class InlineTransitions
{
    public static void Enter(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState continuation, WorkflowStep step, IServiceProvider services)
    {
        var context = WorkflowValueBinding.EvaluationContext(step, checkpoint.DataOf(continuation),
            continuation.GetPrevious(services), ForEachTransitions.ItemsOf(scopes, checkpoint, continuation));
        // The exact contract can include declarations absent from the data type.
        var data = ChildInputs.NewData(step.InvokedDataType!, step.InvokedContract!, step, context);
        var snapshot = BranchData.Snapshot(data);
        var frame = new ScopePosition
        {
            Kind = FrameKind.Inline, ScopeId = scopes.InlineScope(step),
            EntryPrevious = continuation.Previous, RuntimeEntryPrevious = continuation.RuntimePrevious
        };
        checkpoint.InlineFrames.Add(frame.ActivationId, new InlineFrameState
        {
            OwnerContinuationId = continuation.Id, DataType = WorkflowTypeIdentity.SavedName(step.InvokedDataType!),
            Data = StoredJson.Inline(snapshot), Runtime = BranchData.Copy(snapshot, step.InvokedDataType!), Changed = true
        });
        checkpoint.TouchFrame(frame.ActivationId);
        continuation.Stack.Add(frame);
        continuation.SetPrevious(null);
        checkpoint.Trace.Inline(TraceEntryType.InlineEntered, step, continuation, frame);
    }

    public static void Complete(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState continuation, ScopePosition frame)
    {
        var state = checkpoint.InlineFrames[frame.ActivationId];
        var data = state.Value;
        var snapshot = BranchData.Snapshot(data);
        var step = scopes.Step(frame.ScopeId[..^"/inline".Length]);
        var caller = checkpoint.DataOutside(continuation, frame.ActivationId);
        // Save the previous value before any caller mapping can alias a mutable output.
        continuation.Previous = new SerializedValue { Type = state.DataType, Json = StoredJson.Inline(snapshot) };
        continuation.RuntimePrevious = null;
        checkpoint.MarkDataChanged(continuation, frame.ActivationId);
        WorkflowValueBinding.ApplyActivityOutputs(step, data, caller,
            ForEachTransitions.ItemsOf(scopes, checkpoint, continuation));
    }
}
