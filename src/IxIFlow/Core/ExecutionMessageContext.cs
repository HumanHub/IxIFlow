using System.Text.Json;
using System.Text.Json.Serialization;

namespace IxIFlow.Core;

/// <summary>JSON metadata for every host/executor message and its typed payload.</summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(FrozenRetry))]
[JsonSerializable(typeof(TimerFreeze))]
[JsonSerializable(typeof(TimerCommitResult))]
[JsonSerializable(typeof(TimersTrace))]
[JsonSerializable(typeof(InstanceInspection))]
[JsonSerializable(typeof(InspectionSummary))]
[JsonSerializable(typeof(InspectionDebugState))]
[JsonSerializable(typeof(InspectionThread))]
[JsonSerializable(typeof(InspectionFrame))]
[JsonSerializable(typeof(InspectionAttempt))]
[JsonSerializable(typeof(InspectionWait))]
[JsonSerializable(typeof(InspectionData))]
[JsonSerializable(typeof(ReplayOriginTrace))]
[JsonSerializable(typeof(ThreadPositionTrace))]
[JsonSerializable(typeof(ReplayPosition))]
[JsonSerializable(typeof(Assign))]
[JsonSerializable(typeof(Ready))]
[JsonSerializable(typeof(Cancel))]
[JsonSerializable(typeof(Arrivals))]
[JsonSerializable(typeof(Recycle))]
[JsonSerializable(typeof(DebugCommands))]
[JsonSerializable(typeof(Checkpoint))]
[JsonSerializable(typeof(CheckpointReply))]
[JsonSerializable(typeof(StartChild))]
[JsonSerializable(typeof(StartChildReply))]
[JsonSerializable(typeof(ReadInbox))]
[JsonSerializable(typeof(ReadInboxReply))]
[JsonSerializable(typeof(ReadWait))]
[JsonSerializable(typeof(ReadWaitReply))]
[JsonSerializable(typeof(Breakpoint))]
[JsonSerializable(typeof(BreakpointSource))]
[JsonSerializable(typeof(BreakpointStop))]
[JsonSerializable(typeof(BreakpointHitTrace))]
[JsonSerializable(typeof(DataEditTarget))]
[JsonSerializable(typeof(DataPatch))]
[JsonSerializable(typeof(DataEditRequest))]
[JsonSerializable(typeof(DataEditResult))]
[JsonSerializable(typeof(DataEditDigest))]
[JsonSerializable(typeof(DataEditedTrace))]
[JsonSerializable(typeof(DebugStepPosition))]
[JsonSerializable(typeof(DebugStepping))]
[JsonSerializable(typeof(BreakpointEvaluation))]
[JsonSerializable(typeof(BreakpointConditionDiagnostic))]
[JsonSerializable(typeof(RecoveryPreparation))]
[JsonSerializable(typeof(AcknowledgedCommit))]
[JsonSerializable(typeof(AssignmentOutcome))]
public partial class ExecutionMessageContext : JsonSerializerContext;

[JsonSerializable(typeof(CheckpointCommitFields))]
internal partial class CheckpointCommitContext : JsonSerializerContext;

internal sealed record CheckpointCommitFields(CommittedInstance Instance, string Payload,
    IReadOnlyList<CheckpointPart> Parts, IReadOnlyList<string> DeletedParts, bool ReplacesParts,
    Guid Incarnation, long Epoch, string CommitId, long ExpectedRevision, bool ClearsCrashCount,
    bool Parks, IReadOnlyList<EventSettlement> Settlements, long? InboxReadThrough,
    IReadOnlyList<OpenedWait> OpenedWaits, IReadOnlyList<ArmedWait> ArmedWaits,
    IReadOnlyList<ClosedWait> ClosedWaits, TimeSpan? WakeAfter, bool Stopped,
    IReadOnlyList<ChildStart> ChildStarts, IReadOnlyList<ChildCancellation> ChildCancellations,
    ParentNotice? ParentNotice, IReadOnlyList<string> TraceEntries, byte[] ContentHash);

internal sealed class CheckpointCommitJsonConverter : JsonConverter<CheckpointCommit>
{
    public override CheckpointCommit Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var fields = JsonSerializer.Deserialize(ref reader, CheckpointCommitContext.Default.CheckpointCommitFields)
            ?? throw new JsonException("Missing checkpoint commit");
        try
        {
            var commit = CheckpointCommit.FromFields(fields.Instance, fields.Payload, fields.Parts, fields.DeletedParts,
                fields.ReplacesParts, fields.Incarnation, fields.Epoch, fields.CommitId, fields.ExpectedRevision,
                fields.ClearsCrashCount, fields.Parks, fields.Settlements, fields.InboxReadThrough, fields.OpenedWaits,
                fields.ArmedWaits, fields.ClosedWaits, fields.WakeAfter, fields.Stopped, fields.ChildStarts,
                fields.ChildCancellations, fields.ParentNotice, fields.TraceEntries);
            if (fields.ContentHash == null || !commit.ContentHash.AsSpan().SequenceEqual(fields.ContentHash))
                throw new JsonException("Checkpoint content hash differs from its fields");
            return commit;
        }
        catch (ArgumentException error) { throw new JsonException("Invalid checkpoint commit", error); }
    }

    public override void Write(Utf8JsonWriter writer, CheckpointCommit value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, new CheckpointCommitFields(value.Instance, value.Payload, value.Parts,
            value.DeletedParts, value.ReplacesParts, value.Incarnation, value.Epoch, value.CommitId,
            value.ExpectedRevision, value.ClearsCrashCount, value.Parks, value.Settlements, value.InboxReadThrough,
            value.OpenedWaits, value.ArmedWaits, value.ClosedWaits, value.WakeAfter, value.Stopped, value.ChildStarts,
            value.ChildCancellations, value.ParentNotice, value.TraceEntries, value.ContentHash), CheckpointCommitContext.Default.CheckpointCommitFields);
}
