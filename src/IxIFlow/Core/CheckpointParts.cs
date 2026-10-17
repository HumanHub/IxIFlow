using System.Text.Json;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

/// <summary>One stored part of an instance's state (A-08): its key and its JSON.</summary>
public sealed record CheckpointPart(string Key, string Json);

/// <summary>
/// How a store turns what it holds of an instance back into the instance (A-08). A commit's payload is the
/// instance's trunk: its fields and the part of its execution state that belongs to no branch. The rest, the
/// workflow data included, is in parts the store keeps by key beside it, all at the instance's revision.
/// </summary>
public static class CheckpointParts
{
    /// <summary>The key of the part that holds the workflow data.</summary>
    public const string DataKey = "d";

    /// <summary>
    /// The whole instance at <paramref name="revision"/> from a stored payload and every part stored with it at that
    /// revision. A store reads both under one revision; the runnable time is its own to set.
    /// </summary>
    public static WorkflowInstance Assemble(string payload, IEnumerable<CheckpointPart> parts, long revision)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(parts);
        var stored = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in parts)
            stored.Add(part.Key, part.Json);
        var instance = Read(payload);
        if (stored.TryGetValue(DataKey, out var data))
            instance.WorkflowDataJson = data;
        instance.ExecutionStateJson = StoredCheckpoint.AssembleState(instance.ExecutionStateJson, stored);
        instance.Revision = revision;
        instance.Saved(stored.AsReadOnly(), revision);
        return instance;
    }

    /// <summary>
    /// The instance at <paramref name="revision"/> from a stored payload and its data part alone: its fields, its
    /// workflow data and the trunk of its execution state. Enough for a status, a result or a stop check, never to run
    /// it.
    /// </summary>
    public static WorkflowInstance Summarize(string payload, string? data, long revision)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var instance = Read(payload);
        if (data != null)
            instance.WorkflowDataJson = data;
        instance.Revision = revision;
        return instance;
    }

    private static WorkflowInstance Read(string payload) =>
        JsonSerializer.Deserialize<WorkflowInstance>(payload)
        ?? throw new InvalidOperationException("A stored instance could not be read");
}
