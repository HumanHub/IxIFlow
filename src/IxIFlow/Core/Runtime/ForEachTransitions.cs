using System.Text.Json;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// Runs a <c>ForEach</c> (D-34). When the step starts, the list is read once and saved, in item order, on
/// the frame the step belongs to. Sequentially, each item runs the body in a frame of its own, a new scope
/// activation, one after another. In parallel, each item runs as a branch of a join, from a copy of the
/// data at the fork, at most <c>maxConcurrency</c> at a time; the join merges the completed items' data
/// (D-14). The results are stored when the step completes, and the saved list is dropped with the step.
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
        IServiceProvider services, int maxItems, int maxActiveBranches = int.MaxValue)
    {
        var handle = step.ForEachItem ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
            $"ForEach step '{step.Name}' has no item handle");
        var state = position.ForEach;
        var saved = state != null;
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
            state = new ForEachState
            {
                Items = items,
                Results = !StoresResults(step) ? null
                    : step.ForEachParallel ? Enumerable.Repeat<string?>(null, items.Count).ToList() : [],
                Finished = step.ForEachParallel ? [] : null
            };
        }
        if (step.ForEachParallel)
        {
            if (state.Joined || state.Items.Count == 0)
                FinishParallel(scopes, checkpoint, continuation, position, step, state, workflowData, services);
            else
            {
                // A fork that does not fit saves nothing, the list included.
                ContinuationTransitions.EnsureBranchesFit(checkpoint,
                    Math.Min(state.Items.Count, step.ForEachMaxConcurrency ?? state.Items.Count), maxActiveBranches,
                    leaving: continuation);
                position.ForEach = state;
                Fork(scopes, checkpoint, continuation, step, state);
            }
            return;
        }
        if (!saved)
            position.ForEach = state;
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

    /// <summary>True when the step keeps its items' results: to store them, or to decide its join on them.</summary>
    public static bool StoresResults(WorkflowStep step) =>
        step.ForEachInto != null || step.ForEachCompletionCondition != null;

    // Starts the first items of a parallel ForEach as branches of a new join; the step's frame waits.
    private static void Fork(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint, ContinuationState parent,
        WorkflowStep step, ForEachState state)
    {
        var batch = Math.Min(state.Items.Count, step.ForEachMaxConcurrency ?? state.Items.Count);
        var join = new ParallelJoinState
        {
            StepId = scopes.Id(step),
            ParentContinuationId = parent.Id,
            Mode = step.ParallelJoinMode
        };
        checkpoint.Joins.Add(join);
        parent.Status = ContinuationStatus.Joining;
        var snapshot = BranchData.Snapshot(checkpoint.DataOf(parent));
        for (var started = 0; started < batch; started++)
            StartItem(scopes, checkpoint, parent, join, step, state, snapshot);
    }

    /// <summary>
    /// Starts the next item of a parallel ForEach as a branch of its join, from a copy of the parent's
    /// data, which is the data at the fork while the parent waits (D-34); <paramref name="snapshot"/> is
    /// that data as <see cref="BranchData.Snapshot"/> gives it.
    /// </summary>
    public static void StartItem(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState parent, ParallelJoinState join, WorkflowStep step, ForEachState state, string snapshot)
    {
        var index = state.Next++;
        var inherited = Inherited(scopes, parent) ?? [];
        inherited.Add(new InheritedItem { StepId = scopes.Id(step), Index = index, Item = state.Items[index] });
        var child = new ContinuationState
        {
            ParentJoinId = join.Id,
            Data = BranchData.Copy(snapshot, checkpoint.DataOf(parent).GetType()),
            InheritedItems = inherited,
            Previous = parent.RuntimePrevious != null ? SerializedValue.From(parent.RuntimePrevious) : parent.Previous,
            Stack = [new ScopePosition { ScopeId = scopes.ForEachScope(step), ForEachIndex = index }]
        };
        join.ChildContinuationIds.Add(child.Id);
        checkpoint.Continuations.Add(child);
    }

    /// <summary>
    /// Records, when an item's branch of a parallel ForEach completes, that it completed and its result,
    /// by index, on the step's frame.
    /// </summary>
    public static void BranchFinished(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState branch, ScopePosition finished)
    {
        if (finished.ForEachIndex is not { } index || branch.ParentJoinId == null ||
            checkpoint.Joins.SingleOrDefault(item => item.Id == branch.ParentJoinId) is not { } join ||
            scopes.Step(join.StepId) is not { StepType: WorkflowStepType.ForEach } step)
            return;
        var state = StateOf(checkpoint, join, step);
        state.Finished ??= [];
        if (!state.Finished.Contains(index))
            state.Finished.Add(index);
        if (state.Results != null)
            state.Results[index] = finished.ForEachResult ?? throw NoResult(step);
    }

    /// <summary>
    /// True when <paramref name="branch"/>, at the end of <paramref name="finished"/>, its last frame, is an
    /// item of a parallel ForEach whose join is open with items still queued: completing it may start the
    /// next one.
    /// </summary>
    public static bool AdmitsAfter(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState branch, ScopePosition finished)
    {
        if (finished.ForEachIndex == null || branch.ParentJoinId == null ||
            checkpoint.Joins.SingleOrDefault(item => item.Id == branch.ParentJoinId) is not { IsCompleting: false } join ||
            scopes.Step(join.StepId) is not { StepType: WorkflowStepType.ForEach } step)
            return false;
        var state = StateOf(checkpoint, join, step);
        return state.Next < state.Items.Count;
    }

    /// <summary>The saved list of the parallel ForEach a join belongs to, on its parent's frame.</summary>
    public static ForEachState StateOf(ExecutionCheckpoint checkpoint, ParallelJoinState join, WorkflowStep step)
    {
        var parent = checkpoint.Continuations.Single(item => item.Id == join.ParentContinuationId);
        return parent.Stack[^1].ForEach ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
            $"ForEach step '{step.Name}' has a join but no saved list");
    }

    /// <summary>The completed items' results so far, by index, for a conditional join.</summary>
    public static List<(int Index, object? Value)> ResultsSoFar(WorkflowStep step, ForEachState state) =>
        (state.Finished ?? []).Order()
        .Select(index => (index, state.Results?[index] is { } json ? Deserialize(json, step.ResultType!, "result") : null))
        .ToList();

    // Stores the results once the join is done: completed items with their values, started items that
    // did not complete as cancelled, and items never started as not started.
    private static void FinishParallel(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState continuation, ScopePosition position, WorkflowStep step, ForEachState state,
        object workflowData, IServiceProvider services)
    {
        if (step.ForEachInto != null)
        {
            var finished = state.Finished ?? [];
            step.ForEachInto(Context(scopes, checkpoint, continuation, step, workflowData, services),
                Enumerable.Range(0, state.Items.Count).Select(index =>
                        finished.Contains(index)
                            ? (ItemStatus.Completed, Deserialize(state.Results?[index] ?? throw NoResult(step),
                                step.ResultType!, "result"))
                            : (index < state.Next ? ItemStatus.Cancelled : ItemStatus.NotStarted, (object?)null))
                    .ToList());
        }
        position.ForEach = null;
        position.NextStepIndex++;
    }

    /// <summary>
    /// Records the value a body's result step gives on its item's frame: the frame the step ends, or the
    /// one below the timeout branch it ends. The value is kept only when the <c>ForEach</c> keeps results.
    /// </summary>
    public static void TakeResult(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState continuation, ScopePosition position, WorkflowStep step, object workflowData,
        IServiceProvider services)
    {
        var value = step.ResultSelector!(Context(scopes, checkpoint, continuation, step, workflowData, services));
        var frame = continuation.Stack.FindLastIndex(item => item.ForEachIndex != null);
        // An item of a parallel ForEach is its branch's first frame; the step it belongs to is the join's.
        var owner = frame >= 1
            ? scopes.Steps(continuation.Stack[frame - 1].ScopeId)[continuation.Stack[frame - 1].NextStepIndex]
            : frame == 0 && continuation.ParentJoinId != null
                ? scopes.Step(checkpoint.Joins.Single(join => join.Id == continuation.ParentJoinId).StepId)
                : throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                    $"ForEach result step '{step.Name}' runs outside an item's frame");
        if (StoresResults(owner))
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
