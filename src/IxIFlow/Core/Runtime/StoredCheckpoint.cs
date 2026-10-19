using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// A checkpoint as it is stored: a trunk with what belongs to the instance and its root, and parts that each
/// change on their own. A branch's part holds the branch with its joins, waits and kept answers; a <c>ForEach</c>'s
/// list and each of its stored results are parts; the workflow data is a part. A commit writes only the parts that
/// changed, so what one branch does never rewrites the others.
/// </summary>
internal sealed class StoredCheckpoint
{
    private StoredCheckpoint(string trunk, Dictionary<string, StoredJson> parts, PartMarks marks)
    {
        Trunk = trunk;
        Parts = parts;
        Marks = marks;
    }

    /// <summary>
    /// Set by the engine's test assemblies: every split is checked against one that serializes every part, so a
    /// missed mark fails the test that made it.
    /// </summary>
    internal static bool VerifyAlways { get; set; }

    /// <summary>The marks the split wrote by; a commit that is not saved gives them back.</summary>
    public PartMarks Marks { get; }

    /// <summary>The validated execution checkpoint's trunk, as JSON.</summary>
    public string Trunk { get; }

    /// <summary>Every part by key, the workflow data's included.</summary>
    public Dictionary<string, StoredJson> Parts { get; }

    /// <summary>The size a checkpoint limit applies to: the trunk and every part, as UTF-8.</summary>
    public long Bytes => Encoding.UTF8.GetByteCount(Trunk) +
                         Parts.Values.Sum(part => part.Bytes);

    public static string BranchKey(string continuationId) => $"c/{continuationId}";
    public static string ListKey(string forEachId) => $"f/{forEachId}";
    public static string ResultKey(string forEachId, int index) => $"r/{forEachId}/{index}";
    public static string FinishedKey(string forEachId, int chunk) => $"b/{forEachId}/{chunk}";

    /// <summary>
    /// Splits a checkpoint and its workflow data into the trunk and parts it is stored as. The trunk and the data are
    /// serialized every time. A branch is serialized when it is marked or <paramref name="saved"/> lacks it, and
    /// otherwise keeps the JSON it was saved with; a list or a result never changes once saved, so it is serialized
    /// only when <paramref name="saved"/> lacks it. With <paramref name="verify"/>, the result is checked against a
    /// split that serializes everything.
    /// </summary>
    public static StoredCheckpoint Split(ExecutionCheckpoint checkpoint, StoredJson dataJson,
        IReadOnlyDictionary<string, StoredJson>? saved = null, PartMarks? marks = null, bool verify = false)
    {
        marks ??= PartMarks.All;
        var stored = Build(checkpoint, dataJson, saved, marks);
        if (saved != null && (verify || VerifyAlways))
        {
            var whole = Build(checkpoint, dataJson, null, PartMarks.All);
            foreach (var key in whole.Parts.Keys.Union(stored.Parts.Keys))
                if (whole.Parts.GetValueOrDefault(key) != stored.Parts.GetValueOrDefault(key))
                    throw new CheckpointVerificationException(
                        $"the stored part '{key}' of the checkpoint changed without being marked");
        }
        return stored;
    }

    private static StoredCheckpoint Build(ExecutionCheckpoint checkpoint, StoredJson dataJson,
        IReadOnlyDictionary<string, StoredJson>? saved, PartMarks marks)
    {
        var parts = new Dictionary<string, StoredJson>(StringComparer.Ordinal)
        {
            [CheckpointParts.DataKey] = dataJson
        };
        var branchIds = BranchIds(checkpoint);
        string trunk;
        try
        {
            checkpoint.SplitBranches = branchIds;
            trunk = JsonSerializer.Serialize(checkpoint, Stored);
        }
        finally
        {
            checkpoint.SplitBranches = null;
        }
        if (branchIds.Count > 0)
        {
            var joins = checkpoint.Joins.Where(join => branchIds.Contains(join.ParentContinuationId))
                .ToLookup(join => join.ParentContinuationId, StringComparer.Ordinal);
            var waits = checkpoint.Waits.Where(wait => branchIds.Contains(wait.ContinuationId))
                .ToLookup(wait => wait.ContinuationId, StringComparer.Ordinal);
            var replies = checkpoint.BoundReplies.Where(reply => branchIds.Contains(reply.ContinuationId))
                .ToLookup(reply => reply.ContinuationId, StringComparer.Ordinal);
            foreach (var branch in checkpoint.Continuations.Where(item => item.ParentJoinId != null))
                parts[BranchKey(branch.Id)] = saved != null && !marks.Has(branch.Id) &&
                                              saved.TryGetValue(BranchKey(branch.Id), out var kept)
                    ? kept
                    : StoredJson.Inline(JsonSerializer.Serialize(new BranchPart
                {
                    Continuation = branch,
                    Joins = joins[branch.Id].ToList() is { Count: > 0 } ownJoins ? ownJoins : null,
                    Waits = waits[branch.Id].ToList() is { Count: > 0 } ownWaits ? ownWaits : null,
                    BoundReplies = replies[branch.Id].ToList() is { Count: > 0 } ownReplies ? ownReplies : null
                }, Stored));
        }
        foreach (var frame in checkpoint.Continuations.SelectMany(continuation => continuation.Stack))
        {
            if (frame.ForEach is not { } state)
                continue;
            parts[ListKey(state.Id)] = saved?.GetValueOrDefault(ListKey(state.Id)) ??
                                       StoredJson.Inline(JsonSerializer.Serialize(state.Items));
            for (var index = 0; index < (state.Results?.Count ?? 0); index++)
                if (state.Results![index] is { } result)
                    parts[ResultKey(state.Id, index)] = saved?.GetValueOrDefault(ResultKey(state.Id, index)) ?? result;
            // The finished items are written in chunks every time: each is small, and a commit stores only those
            // that changed.
            foreach (var (chunk, bits) in state.Finished?.Chunks() ?? [])
                parts[FinishedKey(state.Id, chunk)] = StoredJson.Inline(JsonSerializer.Serialize(bits));
        }
        return new StoredCheckpoint(trunk, parts, marks);
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
        return Split(ExecutionCheckpoint.Read(WholeState(instance)), instance.WorkflowDataJson);
    }

    /// <summary>
    /// <paramref name="instance"/>'s whole structured state: as it holds it, or assembled from the trunk it holds
    /// after a commit and the parts that commit stored.
    /// </summary>
    public static string WholeState(WorkflowInstance instance) =>
        !IsTrunk(instance.ExecutionStateJson)
            ? instance.ExecutionStateJson
        : AssembleState(instance.ExecutionStateJson, instance.PartsAt(instance.Revision) ??
            throw new InvalidOperationException(
                "The instance holds the trunk of its state without the parts it was saved with"));

    /// <summary>
    /// The whole checkpoint from its trunk and parts. A whole schema-4 checkpoint is returned as it is.
    /// </summary>
    public static string AssembleState(string trunkJson, IReadOnlyDictionary<string, StoredJson> parts)
    {
        if (!IsTrunk(trunkJson))
            return trunkJson;
        var checkpoint = JsonSerializer.Deserialize<ExecutionCheckpoint>(trunkJson, Stored)!;
        // Branches take their places in the order they started, which is the order they were saved in.
        var branches = parts.Where(part => part.Key.StartsWith("c/", StringComparison.Ordinal))
            .Select(part => JsonSerializer.Deserialize<BranchPart>(part.Value.InlineText, Stored)!)
            .OrderBy(branch => branch.Continuation.Sequence);
        foreach (var branch in branches)
        {
            checkpoint.Continuations.Add(branch.Continuation);
            checkpoint.Joins.AddRange(branch.Joins ?? []);
            checkpoint.Waits.AddRange(branch.Waits ?? []);
            checkpoint.BoundReplies.AddRange(branch.BoundReplies ?? []);
        }
        // Finished-item chunks by their ForEach, found in one pass over the parts.
        var chunks = parts.Where(part => part.Key.StartsWith("b/", StringComparison.Ordinal))
            .Select(part => (Key: part.Key.Split('/'), part.Value))
            .ToLookup(part => part.Key[1], part => (Chunk: int.Parse(part.Key[2],
                System.Globalization.CultureInfo.InvariantCulture), Bits: JsonSerializer.Deserialize<string>(part.Value.InlineText)!),
                StringComparer.Ordinal);
        foreach (var frame in checkpoint.Continuations.SelectMany(continuation => continuation.Stack))
        {
            if (frame.ForEach is not { } state)
                continue;
            state.Items = parts.TryGetValue(ListKey(state.Id), out var items)
                ? JsonSerializer.Deserialize<List<StoredJson>>(items.InlineText)!
                : throw new InvalidOperationException($"The saved list of ForEach '{state.Id}' is missing");
            for (var index = 0; index < (state.Results?.Count ?? 0); index++)
                state.Results![index] = parts.GetValueOrDefault(ResultKey(state.Id, index));
            if (state.Finished is { } finished)
                foreach (var (chunk, bits) in chunks[state.Id])
                    finished.AddChunk(chunk, bits);
        }
        checkpoint.StoredTrunk = false;
        return JsonSerializer.Serialize(checkpoint);
    }

    private static bool IsTrunk(string structuredJson)
    {
        _ = ExecutionCheckpoint.ReadTrunk(structuredJson);
        using var document = JsonDocument.Parse(structuredJson);
        return document.RootElement.TryGetProperty(nameof(ExecutionCheckpoint.StoredTrunk), out var trunk) &&
               trunk.ValueKind == JsonValueKind.True;
    }

    private static HashSet<string> BranchIds(ExecutionCheckpoint checkpoint) => checkpoint.Continuations
        .Where(item => item.ParentJoinId != null).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);

    // What the trunk keeps: whatever no branch owns.
    private static Func<T, bool> NotOf<T>(ExecutionCheckpoint checkpoint, Func<T, string> owner)
    {
        var branches = checkpoint.SplitBranches ?? BranchIds(checkpoint);
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
        else if (type.Type == typeof(WaitState))
        {
            // A wait's deadline is the store's (D-35): a run reads it with its inbox before it looks at it, so a
            // stamp that replaces the run's estimate does not rewrite the branch that holds the wait.
            var deadline = type.Properties.Single(property => property.Name == nameof(WaitState.DeadlineUtc));
            type.Properties.Remove(deadline);
        }
        else if (type.Type == typeof(ForEachState))
        {
            foreach (var property in type.Properties
                         .Where(property => property.Name is nameof(ForEachState.Items) or nameof(ForEachState.Results)
                             or nameof(ForEachState.Finished))
                         .ToArray())
                type.Properties.Remove(property);
            // The finished items are in chunk parts; the frame says only whether it keeps them.
            var finished = type.CreateJsonPropertyInfo(typeof(bool?), "FinishedInParts");
            finished.Get = value => ((ForEachState)value).Finished != null ? true : null;
            finished.Set = (value, kept) => ((ForEachState)value).Finished = kept is true ? new ItemSet() : null;
            finished.ShouldSerialize = (_, kept) => kept != null;
            type.Properties.Add(finished);
            var count = type.CreateJsonPropertyInfo(typeof(int?), "ResultCount");
            count.Get = value => ((ForEachState)value).Results?.Count;
            count.Set = (value, results) => ((ForEachState)value).Results =
                results is int length ? Enumerable.Repeat<StoredJson?>(null, length).ToList() : null;
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

/// <summary>
/// A commit's parts differ from those serializing everything gives: a transition changed a branch without marking it.
/// The commit is not saved, and the instance stops for an operator with its last checkpoint.
/// </summary>
internal sealed class CheckpointVerificationException(string reason) : Exception(
    $"The checkpoint could not be verified: {reason}");
