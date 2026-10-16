using System.Text.Json;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// Runs a sequential <c>ForEach</c> (D-34). When the step starts, the list is read once and saved, in
/// item order, on the frame the step belongs to; each item then runs the body in a frame of its own, a
/// new scope activation, one after another. The results are stored when the last item completes, and
/// the saved list is dropped with the step.
/// </summary>
internal static class ForEachTransitions
{
    /// <summary>
    /// Saves the list when the step starts, runs the next item, or completes the step when every item
    /// has run. A list over <paramref name="maxItems"/> throws <see cref="ForEachLimitExceededException"/>
    /// before anything is saved.
    /// </summary>
    public static void Advance(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState continuation, ScopePosition position, WorkflowStep step, object workflowData,
        IServiceProvider services, int maxItems)
    {
        var handle = step.ForEachItem ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
            $"ForEach step '{step.Name}' has no item handle");
        var state = position.ForEach;
        if (state == null)
        {
            var list = step.ForEachItems!(Context(scopes, checkpoint, continuation, step, workflowData, services))
                ?? throw new InvalidOperationException($"ForEach step '{step.Name}' was given no list");
            var items = new List<string>();
            foreach (var item in list)
            {
                // The list is read up to one item past the limit, so a longer one is never read whole.
                if (items.Count == maxItems)
                    throw new ForEachLimitExceededException(maxItems);
                items.Add(Serialize(item, handle.ItemType, "item"));
            }
            state = position.ForEach = new ForEachState { Items = items };
        }
        if (state.Next < state.Items.Count)
        {
            continuation.Stack.Add(new ScopePosition
            {
                ScopeId = scopes.ForEachScope(step),
                ForEachIndex = state.Next,
                EntryPrevious = continuation.Previous,
                RuntimeEntryPrevious = continuation.RuntimePrevious,
                RestorePreviousOnExit = true
            });
            state.Next++;
            return;
        }
        if (step.ForEachInto != null)
            step.ForEachInto(Context(scopes, checkpoint, continuation, step, workflowData, services),
                Enumerable.Range(0, state.Items.Count)
                    .Select(index => (ItemStatus.Completed,
                        state.Results.ElementAtOrDefault(index) is { } result
                            ? Deserialize(result, step.ResultType!) : null))
                    .ToList());
        position.ForEach = null;
        position.NextStepIndex++;
    }

    /// <summary>Records the value a body's result step gives for its item, on the item's frame.</summary>
    public static void TakeResult(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState continuation, ScopePosition position, WorkflowStep step, object workflowData,
        IServiceProvider services)
    {
        var value = step.ResultSelector!(Context(scopes, checkpoint, continuation, step, workflowData, services));
        position.ForEachResult = Serialize(value, step.ResultType!, "result");
        position.NextStepIndex++;
    }

    /// <summary>
    /// True when <paramref name="finished"/> is an item's frame of the <c>ForEach</c> its parent is at; its
    /// result, if any, is kept for the step, which then runs the next item.
    /// </summary>
    public static bool ItemFinished(WorkflowScopeCatalog scopes, ScopePosition parent, ScopePosition finished,
        WorkflowStep current)
    {
        if (current.StepType != WorkflowStepType.ForEach || finished.ScopeId != scopes.ForEachScope(current) ||
            finished.ForEachIndex is not { } index || parent.ForEach is not { } state)
            return false;
        while (state.Results.Count <= index)
            state.Results.Add(null);
        state.Results[index] = finished.ForEachResult;
        return true;
    }

    /// <summary>
    /// The items in force for <paramref name="continuation"/>: the current item of every <c>ForEach</c>
    /// body on its stack and, through its joins, on the stacks of the continuations that forked it.
    /// </summary>
    public static ItemValues ItemsOf(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState continuation)
    {
        Dictionary<ItemScope, (int, object?)>? values = null;
        for (var current = continuation; current != null; current = Parent(checkpoint, current))
            for (var index = current.Stack.Count - 1; index >= 1; index--)
            {
                if (current.Stack[index].ForEachIndex is not { } item)
                    continue;
                var owner = current.Stack[index - 1];
                var step = scopes.Steps(owner.ScopeId)[owner.NextStepIndex];
                var state = owner.ForEach ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                    $"ForEach step '{step.Name}' has an item frame but no saved list");
                values ??= new Dictionary<ItemScope, (int, object?)>(ReferenceEqualityComparer.Instance);
                values.TryAdd(step.ForEachItem!, (item, state.Item(item, step.ForEachItem!.ItemType)));
            }
        return values == null ? ItemValues.None : new ItemValues(values);
    }

    private static ContinuationState? Parent(ExecutionCheckpoint checkpoint, ContinuationState continuation)
    {
        if (continuation.ParentJoinId == null)
            return null;
        var join = checkpoint.Joins.Single(item => item.Id == continuation.ParentJoinId);
        return checkpoint.Continuations.Single(item => item.Id == join.ParentContinuationId);
    }

    private static object Context(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState continuation, WorkflowStep step, object workflowData, IServiceProvider services) =>
        WorkflowValueBinding.EvaluationContext(workflowData, step.PreviousStepDataType,
            continuation.GetPrevious(services), ItemsOf(scopes, checkpoint, continuation));

    private static string Serialize(object? value, Type type, string what)
    {
        try
        {
            return JsonSerializer.Serialize(value, type);
        }
        catch (Exception error) when (error is NotSupportedException or JsonException or ArgumentException)
        {
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"A ForEach {what} of type '{type.Name}' cannot be saved in a checkpoint: {error.Message}", error);
        }
    }

    public static object? Deserialize(string json, Type type) => JsonSerializer.Deserialize(json, type);
}

/// <summary>A <c>ForEach</c> list is longer than the configured limit; nothing was saved (D-34).</summary>
internal sealed class ForEachLimitExceededException(int limit) : ResourceLimitExceededException(
    $"A ForEach list has more than the configured limit of {limit} items");
