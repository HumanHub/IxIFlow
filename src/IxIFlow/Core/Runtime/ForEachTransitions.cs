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
                items.Add(RoundTrip(item, handle.ItemType, "item"));
            }
            state = position.ForEach = new ForEachState
            {
                Items = items,
                Results = step.ForEachInto != null ? [] : null
            };
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
        {
            var results = state.Results ?? throw NoResult(step);
            if (results.Count != state.Items.Count || results.Any(result => result == null))
                throw NoResult(step);
            step.ForEachInto(Context(scopes, checkpoint, continuation, step, workflowData, services),
                results.Select(result => (ItemStatus.Completed, Deserialize(result!, step.ResultType!, "result")))
                    .ToList());
        }
        position.ForEach = null;
        position.NextStepIndex++;
    }

    /// <summary>
    /// Records the value a body's result step gives on its item's frame: the frame the step ends, or the
    /// one below the timeout branch it ends. The value is kept only when the <c>ForEach</c> stores results.
    /// </summary>
    public static void TakeResult(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState continuation, ScopePosition position, WorkflowStep step, object workflowData,
        IServiceProvider services)
    {
        var value = step.ResultSelector!(Context(scopes, checkpoint, continuation, step, workflowData, services));
        var frame = continuation.Stack.FindLastIndex(item => item.ForEachIndex != null);
        if (frame < 1)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                $"ForEach result step '{step.Name}' runs outside an item's frame");
        var owner = continuation.Stack[frame - 1];
        if (scopes.Steps(owner.ScopeId)[owner.NextStepIndex].ForEachInto != null)
            continuation.Stack[frame].ForEachResult = RoundTrip(value, step.ResultType!, "result");
        position.NextStepIndex++;
    }

    /// <summary>
    /// True when <paramref name="finished"/> is an item's frame of the <c>ForEach</c> its parent is at; its
    /// result is kept when the step stores results, and the step then runs the next item.
    /// </summary>
    public static bool ItemFinished(WorkflowScopeCatalog scopes, ScopePosition parent, ScopePosition finished,
        WorkflowStep current)
    {
        if (current.StepType != WorkflowStepType.ForEach || finished.ScopeId != scopes.ForEachScope(current) ||
            finished.ForEachIndex is not { } index)
            return false;
        var state = parent.ForEach ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
            $"ForEach step '{current.Name}' finished an item but has no saved list");
        if (current.ForEachInto == null)
            return true;
        var results = state.Results ?? throw NoResult(current);
        if (finished.ForEachResult == null || index != results.Count)
            throw NoResult(current);
        results.Add(finished.ForEachResult);
        return true;
    }

    /// <summary>
    /// The items in force for <paramref name="continuation"/>: those it inherited when it was forked, and
    /// the current item of every <c>ForEach</c> body on its own stack.
    /// </summary>
    public static ItemValues ItemsOf(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState continuation)
    {
        var bound = Bound(scopes, continuation);
        if (bound.Count == 0)
            return ItemValues.None;
        var values = new Dictionary<ItemScope, (int, string)>(ReferenceEqualityComparer.Instance);
        foreach (var item in bound)
            values[scopes.Step(item.StepId).ForEachItem!] = (item.Index, item.Item);
        return new ItemValues(values);
    }

    /// <summary>
    /// The items a branch forked from <paramref name="parent"/> keeps, whatever then happens to the
    /// parent's frames.
    /// </summary>
    public static List<InheritedItem>? Inherited(WorkflowScopeCatalog scopes, ContinuationState parent) =>
        Bound(scopes, parent) is { Count: > 0 } bound ? bound : null;

    private static List<InheritedItem> Bound(WorkflowScopeCatalog scopes, ContinuationState continuation)
    {
        var bound = new List<InheritedItem>(continuation.InheritedItems ?? []);
        for (var index = 1; index < continuation.Stack.Count; index++)
        {
            if (continuation.Stack[index].ForEachIndex is not { } item)
                continue;
            var owner = continuation.Stack[index - 1];
            var step = scopes.Steps(owner.ScopeId)[owner.NextStepIndex];
            var state = owner.ForEach ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                $"ForEach step '{step.Name}' has an item frame but no saved list");
            bound.Add(new InheritedItem { StepId = scopes.Id(step), Index = item, Item = state.Items[item] });
        }
        return bound;
    }

    private static object Context(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState continuation, WorkflowStep step, object workflowData, IServiceProvider services) =>
        WorkflowValueBinding.EvaluationContext(workflowData, step.PreviousStepDataType,
            continuation.GetPrevious(services), ItemsOf(scopes, checkpoint, continuation));

    private static WorkflowEngineException NoResult(WorkflowStep step) =>
        new(WorkflowErrorKind.InvalidState, $"ForEach step '{step.Name}' has an item that gave no result");

    // Saves a value as JSON of its declared type and reads it back once, so a value that cannot be
    // restored is refused when it is saved, not when a later step reads it.
    private static string RoundTrip(object? value, Type type, string what)
    {
        string json;
        try
        {
            json = JsonSerializer.Serialize(value, type);
        }
        catch (Exception error) when (error is NotSupportedException or JsonException or ArgumentException)
        {
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"A ForEach {what} of type '{type.Name}' cannot be saved in a checkpoint: {error.Message}", error);
        }
        Deserialize(json, type, what);
        return json;
    }

    /// <summary>Reads a saved item or result; one that cannot be read back is an invalid definition.</summary>
    public static object? Deserialize(string json, Type type, string what)
    {
        try
        {
            return JsonSerializer.Deserialize(json, type);
        }
        catch (Exception error) when (error is NotSupportedException or JsonException or ArgumentException or
                                          InvalidOperationException)
        {
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"A ForEach {what} of type '{type.Name}' cannot be read back from a checkpoint: {error.Message}",
                error);
        }
    }
}

/// <summary>
/// An item a forked branch keeps from the <c>ForEach</c> bodies it was forked in: the step's id, the
/// item's position and its saved JSON (D-34).
/// </summary>
internal sealed class InheritedItem
{
    public string StepId { get; set; } = "";
    public int Index { get; set; }
    public string Item { get; set; } = "";
}

/// <summary>A <c>ForEach</c> list is longer than the configured limit; nothing was saved (D-34).</summary>
internal sealed class ForEachLimitExceededException(int limit) : ResourceLimitExceededException(
    $"A ForEach list has more than the configured limit of {limit} items");
