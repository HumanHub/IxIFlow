using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// A checkpoint as it is stored (A-08): a trunk with what belongs to the instance and its root, and parts that each
/// change on their own. A branch's part holds the branch with its joins, waits and kept answers; a <c>ForEach</c>'s
/// list and each of its stored results are parts; the workflow data is a part. A commit writes only the parts that
/// changed, so what one branch does never rewrites the others.
/// </summary>
internal sealed class StoredCheckpoint
{
    private StoredCheckpoint(string trunk, SortedDictionary<string, string> parts)
    {
        Trunk = trunk;
        Parts = parts;
    }

    /// <summary>The execution state's trunk, as JSON; for a state that is not structured, the state itself.</summary>
    public string Trunk { get; }

    /// <summary>Every part by key, the workflow data's included.</summary>
    public SortedDictionary<string, string> Parts { get; }

    /// <summary>The size a checkpoint limit applies to: the trunk and every part, as UTF-8 (D-28).</summary>
    public long Bytes => Encoding.UTF8.GetByteCount(Trunk) +
                         Parts.Values.Sum(part => (long)Encoding.UTF8.GetByteCount(part));

    public static string BranchKey(string continuationId) => $"c/{continuationId}";
    public static string ListKey(string activationId) => $"f/{activationId}";
    public static string ResultKey(string activationId, int index) => $"r/{activationId}/{index}";

    /// <summary>Splits a checkpoint and its workflow data into the trunk and parts it is stored as.</summary>
    public static StoredCheckpoint Split(ExecutionCheckpoint checkpoint, string dataJson)
    {
        var parts = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            [CheckpointParts.DataKey] = dataJson
        };
        var trunk = JsonSerializer.Serialize(checkpoint, Stored);
        var branchIds = BranchIds(checkpoint);
        if (branchIds.Count > 0)
        {
            var joins = checkpoint.Joins.Where(join => branchIds.Contains(join.ParentContinuationId))
                .ToLookup(join => join.ParentContinuationId, StringComparer.Ordinal);
            var waits = checkpoint.Waits.Where(wait => branchIds.Contains(wait.ContinuationId))
                .ToLookup(wait => wait.ContinuationId, StringComparer.Ordinal);
            var replies = checkpoint.BoundReplies.Where(reply => branchIds.Contains(reply.ContinuationId))
                .ToLookup(reply => reply.ContinuationId, StringComparer.Ordinal);
            foreach (var branch in checkpoint.Continuations.Where(item => item.ParentJoinId != null))
                parts[BranchKey(branch.Id)] = JsonSerializer.Serialize(new BranchPart
                {
                    Continuation = branch,
                    Joins = joins[branch.Id].ToList() is { Count: > 0 } ownJoins ? ownJoins : null,
                    Waits = waits[branch.Id].ToList() is { Count: > 0 } ownWaits ? ownWaits : null,
                    BoundReplies = replies[branch.Id].ToList() is { Count: > 0 } ownReplies ? ownReplies : null
                }, Stored);
        }
        foreach (var frame in checkpoint.Continuations.SelectMany(continuation => continuation.Stack))
        {
            if (frame.ForEach is not { } state)
                continue;
            parts[ListKey(frame.ActivationId)] = JsonSerializer.Serialize(state.Items);
            for (var index = 0; index < (state.Results?.Count ?? 0); index++)
                if (state.Results![index] is { } result)
                    parts[ResultKey(frame.ActivationId, index)] = result;
        }
        return new StoredCheckpoint(trunk, parts);
    }

    /// <summary>
    /// The stored form of <paramref name="instance"/>'s state and data: the one its last preparation made, or else
    /// split from its state now.
    /// </summary>
    public static StoredCheckpoint Of(WorkflowInstance instance)
    {
        if (instance.Stored is { } stored && ReferenceEquals(stored.Trunk, instance.ExecutionStateJson) &&
            ReferenceEquals(stored.Parts[CheckpointParts.DataKey], instance.WorkflowDataJson))
            return stored;
        var state = instance.ExecutionStateJson;
        if (!ExecutionCheckpoint.IsStructured(state))
            return new StoredCheckpoint(state, new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                [CheckpointParts.DataKey] = instance.WorkflowDataJson
            });
        if (IsTrunk(state))
            state = AssembleState(state, instance.SavedParts ?? throw new InvalidOperationException(
                "The instance holds the trunk of its state without the parts it was saved with"));
        return Split(ExecutionCheckpoint.Read(state), instance.WorkflowDataJson);
    }

    /// <summary>
    /// The whole execution state from its trunk and parts. A state that was not split is returned as it is.
    /// </summary>
    public static string AssembleState(string trunkJson, IReadOnlyDictionary<string, string> parts)
    {
        if (!ExecutionCheckpoint.IsStructured(trunkJson) || !IsTrunk(trunkJson))
            return trunkJson;
        var checkpoint = JsonSerializer.Deserialize<ExecutionCheckpoint>(trunkJson, Stored)!;
        // Branches take their places in the order they started, which is the order they were saved in.
        var branches = parts.Where(part => part.Key.StartsWith("c/", StringComparison.Ordinal))
            .Select(part => JsonSerializer.Deserialize<BranchPart>(part.Value, Stored)!)
            .OrderBy(branch => branch.Continuation.Sequence);
        foreach (var branch in branches)
        {
            checkpoint.Continuations.Add(branch.Continuation);
            checkpoint.Joins.AddRange(branch.Joins ?? []);
            checkpoint.Waits.AddRange(branch.Waits ?? []);
            checkpoint.BoundReplies.AddRange(branch.BoundReplies ?? []);
        }
        foreach (var frame in checkpoint.Continuations.SelectMany(continuation => continuation.Stack))
        {
            if (frame.ForEach is not { } state)
                continue;
            state.Items = parts.TryGetValue(ListKey(frame.ActivationId), out var items)
                ? JsonSerializer.Deserialize<List<string>>(items)!
                : throw new InvalidOperationException($"The saved list of ForEach frame '{frame.ActivationId}' is missing");
            for (var index = 0; index < (state.Results?.Count ?? 0); index++)
                state.Results![index] = parts.GetValueOrDefault(ResultKey(frame.ActivationId, index));
        }
        checkpoint.StoredTrunk = false;
        return JsonSerializer.Serialize(checkpoint);
    }

    private static bool IsTrunk(string structuredJson)
    {
        using var document = JsonDocument.Parse(structuredJson);
        return document.RootElement.TryGetProperty(nameof(ExecutionCheckpoint.StoredTrunk), out var trunk) &&
               trunk.ValueKind == JsonValueKind.True;
    }

    private static HashSet<string> BranchIds(ExecutionCheckpoint checkpoint) => checkpoint.Continuations
        .Where(item => item.ParentJoinId != null).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);

    // What the trunk keeps: whatever no branch owns.
    private static Func<T, bool> NotOf<T>(ExecutionCheckpoint checkpoint, Func<T, string> owner)
    {
        var branches = BranchIds(checkpoint);
        return item => !branches.Contains(owner(item));
    }

    /// <summary>
    /// How a checkpoint's trunk and parts are written: the trunk keeps only what is not a branch's, and a
    /// <c>ForEach</c> keeps its list and results in parts of their own, recording how many results it holds.
    /// </summary>
    private static readonly JsonSerializerOptions Stored = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { StoredForm } }
    };

    private static void StoredForm(JsonTypeInfo type)
    {
        if (type.Type == typeof(ExecutionCheckpoint))
        {
            foreach (var property in type.Properties)
                switch (property.Name)
                {
                    case nameof(ExecutionCheckpoint.Continuations):
                        property.Get = value => ((ExecutionCheckpoint)value).Continuations
                            .Where(item => item.ParentJoinId == null).ToList();
                        break;
                    case nameof(ExecutionCheckpoint.Joins):
                        property.Get = value => ((ExecutionCheckpoint)value) is var checkpoint
                            ? checkpoint.Joins.Where(NotOf<ParallelJoinState>(checkpoint, join => join.ParentContinuationId)).ToList()
                            : null;
                        break;
                    case nameof(ExecutionCheckpoint.Waits):
                        property.Get = value => ((ExecutionCheckpoint)value) is var checkpoint
                            ? checkpoint.Waits.Where(NotOf<WaitState>(checkpoint, wait => wait.ContinuationId)).ToList()
                            : null;
                        break;
                    case nameof(ExecutionCheckpoint.BoundReplies):
                        property.Get = value => ((ExecutionCheckpoint)value) is var checkpoint
                            ? checkpoint.BoundReplies.Where(NotOf<BoundReplyState>(checkpoint, reply => reply.ContinuationId)).ToList()
                            : null;
                        break;
                    case nameof(ExecutionCheckpoint.StoredTrunk):
                        property.Get = _ => true;
                        break;
                }
        }
        else if (type.Type == typeof(ForEachState))
        {
            foreach (var property in type.Properties
                         .Where(property => property.Name is nameof(ForEachState.Items) or nameof(ForEachState.Results))
                         .ToArray())
                type.Properties.Remove(property);
            var count = type.CreateJsonPropertyInfo(typeof(int?), "ResultCount");
            count.Get = value => ((ForEachState)value).Results?.Count;
            count.Set = (value, results) => ((ForEachState)value).Results =
                results is int length ? Enumerable.Repeat<string?>(null, length).ToList() : null;
            count.ShouldSerialize = (_, results) => results != null;
            type.Properties.Add(count);
        }
    }

    /// <summary>A branch as it is stored: the continuation, and the joins, waits and kept answers that are its own.</summary>
    private sealed class BranchPart
    {
        public ContinuationState Continuation { get; set; } = null!;

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ParallelJoinState>? Joins { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<WaitState>? Waits { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<BoundReplyState>? BoundReplies { get; set; }
    }
}
