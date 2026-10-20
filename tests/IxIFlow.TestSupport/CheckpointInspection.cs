using System.Reflection;
using System.Text;
using System.Text.Json;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Tests.Infrastructure;

/// <summary>Inspection and snapshot copying for checkpoint and repository contract tests.</summary>
public static class CheckpointInspection
{
    // Preserve all identity and part-provenance fields, including the private revision/instance pair.
    // Like a store read, this copy is edited as a snapshot; the saved parts themselves are immutable.
    private static readonly MethodInfo Clone = typeof(object).GetMethod("MemberwiseClone",
        BindingFlags.Instance | BindingFlags.NonPublic)!;

    public static WorkflowInstance CopyForResume(this WorkflowInstance instance) =>
        (WorkflowInstance)Clone.Invoke(instance, null)!;

    public static long StateBytes(this CheckpointCommit commit) =>
        Encoding.UTF8.GetByteCount(commit.Payload) +
        commit.Parts.Sum(part => (long)part.Json.Bytes);

    public static long CheckpointBytes(WorkflowInstance instance) => StoredCheckpoint.Of(instance).Bytes;

    public static IReadOnlyList<ExecutionTraceEntry> ReadTraceEntries(this CheckpointCommit commit) =>
        commit.TraceEntries.Select(entry => JsonSerializer.Deserialize<ExecutionTraceEntry>(entry)!).ToArray();
}
