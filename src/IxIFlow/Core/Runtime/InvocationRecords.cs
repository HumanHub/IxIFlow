using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Buffers.Binary;

namespace IxIFlow.Core.Runtime;

internal sealed class InvocationRecordState : IJsonOnDeserialized
{
    [JsonPropertyName("i"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Id { get; set; } = "";
    [JsonPropertyName("c"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string ContinuationId { get; set; } = "";
    [JsonPropertyName("f"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string FrameActivationId { get; set; } = "";
    [JsonPropertyName("s"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string StepId { get; set; } = "";
    [JsonPropertyName("w"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string WorkflowName { get; set; } = "";
    [JsonPropertyName("v"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int WorkflowVersion { get; set; }
    [JsonPropertyName("n"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int StepNumber { get; set; }
    [JsonPropertyName("t"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? DataType { get; set; }
    [JsonPropertyName("b"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ArtifactDigest? BundleDigest { get; set; }
    [JsonPropertyName("h"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? StructureHash { get; set; }
    [JsonPropertyName("k"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? ContractHash { get; set; }
    [JsonPropertyName("p"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public DateTime StartedAtUtc { get; set; }
    [JsonPropertyName("d"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Disposition { get; set; }
    [JsonPropertyName("r"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ScheduledRetries { get; set; }
    [JsonIgnore]
    public int? SelectedPolicyIndex { get; set; }
    [JsonPropertyName("u"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public DateTime? RetryAfterUtc { get; set; }
    [JsonPropertyName("a"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<InvocationAttemptState> Attempts { get; set; } = [];

    void IJsonOnDeserialized.OnDeserialized()
    {
        foreach (var attempt in Attempts)
        {
            var ordinal = attempt.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            attempt.Id = InvocationRecords.Identity("policy-attempt", Id, ordinal);
            attempt.ProposedChildInstanceId = InvocationRecords.Identity("child-instance", Id, ordinal);
        }
        SelectedPolicyIndex = Attempts.LastOrDefault()?.SelectedPolicyIndex;
    }
}

internal sealed class InvocationAttemptState
{
    [JsonIgnore]
    public string Id { get; set; } = "";
    [JsonPropertyName("n"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Number { get; set; }
    [JsonPropertyName("p"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public DateTime PreparedAtUtc { get; set; }
    [JsonPropertyName("s"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public DateTime? StartedAtUtc { get; set; }
    [JsonPropertyName("e"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public DateTime? EndedAtUtc { get; set; }
    [JsonPropertyName("h"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Host { get; set; } = "";
    [JsonPropertyName("f"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string Phase { get; set; } = "Preparing";
    [JsonIgnore]
    public string ProposedChildInstanceId { get; set; } = "";
    [JsonIgnore]
    public string? ChildInstanceId => StartedAtUtc == null ? null : ProposedChildInstanceId;
    [JsonPropertyName("g"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Guid? ChildIncarnation { get; set; }
    [JsonPropertyName("t"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public WorkflowStatus? ChildStatus { get; set; }
    [JsonPropertyName("z"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SerializedException? Failure { get; set; }
    [JsonPropertyName("d"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public InspectionError? Diagnostic { get; set; }
    [JsonPropertyName("o"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ActivityResolutionState? Decision { get; set; }
    [JsonPropertyName("v"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Disposition { get; set; }
    [JsonPropertyName("x"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? SelectedPolicyIndex { get; set; }
    [JsonPropertyName("a"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public StepErrorAction? Action { get; set; }
    [JsonPropertyName("u"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public DateTime? RetryAfterUtc { get; set; }
}

// The trunk keeps only safe identity and closure metadata. Termination never needs record payloads.
internal sealed record InvocationIndexEntry(
    [property: JsonPropertyName("i")] string Id,
    [property: JsonPropertyName("s")] string StepId,
    [property: JsonPropertyName("c")] string ContinuationId,
    [property: JsonPropertyName("n")] int AttemptNumber,
    [property: JsonPropertyName("h")] bool Started,
    [property: JsonPropertyName("o")] bool Open)
{
    [JsonIgnore]
    public string? PolicyAttemptId => AttemptNumber == 0 ? null : InvocationRecords.Identity("policy-attempt", Id,
        AttemptNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
    [JsonIgnore]
    public string? ChildInstanceId => !Started ? null : InvocationRecords.Identity("child-instance", Id,
        AttemptNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
internal sealed record InvocationClosure(DateTime AtUtc, string Disposition);

internal static class InvocationRecords
{
    public static string Identity(string domain, params string[] values)
    {
        // Preserve UTF-16 code units, including arbitrary user IDs, without delimiter or encoding collisions.
        var members = values.Prepend(domain).ToArray();
        var tuple = new byte[members.Sum(value => checked(4 + value.Length * 2))];
        var remaining = tuple.AsSpan();
        foreach (var value in members)
        {
            BinaryPrimitives.WriteInt32LittleEndian(remaining, value.Length);
            remaining = remaining[4..];
            foreach (var character in value)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(remaining, character);
                remaining = remaining[2..];
            }
        }
        // A 128-bit identity in URL-safe form keeps the same entropy in 22 persisted characters.
        return Convert.ToBase64String(SHA256.HashData(tuple).AsSpan(0, 16))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static string LogicalId(WorkflowInstance instance, ContinuationState continuation, string stepId) =>
        Identity("workflow-invocation", instance.InstanceId, instance.Incarnation.ToString("N"), stepId,
            string.Join("/", continuation.Stack.Select(frame => frame.ActivationId + ":" +
                frame.LoopIterationCount.ToString(System.Globalization.CultureInfo.InvariantCulture))));

    public static InvocationRecordState? Active(ExecutionCheckpoint checkpoint, ContinuationState continuation) =>
        continuation.InvocationRecordId is { } id ? checkpoint.InvocationRecords.GetValueOrDefault(id) : null;

    public static InvocationTrace Facts(InvocationRecordState record, InvocationAttemptState attempt) =>
        new(record.Id, record.StepId, record.ContinuationId, attempt.Id, attempt.Number,
            attempt.ChildInstanceId, attempt.ChildStatus, attempt.Disposition ?? attempt.Phase,
            attempt.SelectedPolicyIndex, attempt.Action, attempt.RetryAfterUtc);

    public static void Close(ExecutionCheckpoint checkpoint, ContinuationState continuation, string disposition, DateTime now)
    {
        if (Active(checkpoint, continuation) is not { } record) return;
        checkpoint.TouchInvocation(record.Id);
        record.Disposition = disposition;
        record.RetryAfterUtc = null;
        continuation.RetryAfterUtc = null;
        if (record.Attempts.LastOrDefault() is { } attempt && attempt.Phase != "Ended")
        {
            attempt.Phase = "Ended";
            attempt.EndedAtUtc = now;
            attempt.Disposition = disposition;
        }
        continuation.InvocationRecordId = null;
    }

    public static void Abandon(ExecutionCheckpoint checkpoint, ContinuationState continuation, string disposition)
    {
        if (Active(checkpoint, continuation) is not { } record) return;
        checkpoint.TouchInvocation(record.Id);
        record.Disposition = disposition;
        record.RetryAfterUtc = null;
        continuation.RetryAfterUtc = null;
        continuation.InvocationRecordId = null;
    }

    public static IReadOnlyList<InspectionInvocation> Inspect(ExecutionCheckpoint checkpoint) =>
        checkpoint.InvocationRecords.Values.Select(record =>
        {
            var closure = record.Disposition == null ? checkpoint.InvocationClosure : null;
            return new InspectionInvocation(record.Id, record.StepId, record.ContinuationId,
                record.WorkflowName, record.WorkflowVersion, record.DataType, record.BundleDigest,
                record.StructureHash, record.ContractHash, record.Disposition ?? closure?.Disposition,
                record.ScheduledRetries, closure == null ? record.RetryAfterUtc : null,
                record.Attempts.Select(attempt => new InspectionInvocationAttempt(attempt.Id, attempt.Number,
                    closure != null && attempt == record.Attempts[^1] ? "Ended" : attempt.Phase,
                    attempt.PreparedAtUtc, attempt.StartedAtUtc,
                    attempt.EndedAtUtc ?? (attempt == record.Attempts[^1] ? closure?.AtUtc : null), attempt.Host,
                    attempt.ChildInstanceId, attempt.ChildIncarnation, attempt.ChildStatus,
                    closure != null && attempt == record.Attempts[^1] ? closure.Disposition : attempt.Disposition,
                    attempt.Diagnostic, attempt.SelectedPolicyIndex, attempt.Action,
                    closure != null && attempt == record.Attempts[^1] ? null : attempt.RetryAfterUtc,
                    attempt.Decision?.Kind)).ToArray());
        }).ToArray();

    public static void PrepareCommit(ExecutionCheckpoint checkpoint, WorkflowInstance instance, DateTime now)
    {
        foreach (var record in checkpoint.InvocationRecords.Values)
        {
            if (record.Attempts.LastOrDefault() is not { } attempt) continue;
            var previousPhase = attempt.Phase;
            var previousDisposition = attempt.Disposition;
            var previousDeadline = attempt.RetryAfterUtc;
            var continuation = checkpoint.ContinuationById(record.ContinuationId);
            if (attempt.Phase == "WaitingChild" && attempt.ChildStatus == null &&
                attempt.StartedAtUtc == null)
            {
                // A sibling can remove a staged start before this commit. Never record a child that won't exist.
                var staged = checkpoint.ChildStarts.Any(start => start.ChildInstanceId == attempt.ProposedChildInstanceId);
                checkpoint.TouchInvocation(record.Id);
                if (staged)
                {
                    attempt.StartedAtUtc = now;
                    if (checkpoint.Trace.Accepts(TraceEntryType.ActivityStarted, now))
                        checkpoint.Trace.Add(new ExecutionTraceEntry { Timestamp = now,
                            EntryType = TraceEntryType.ActivityStarted, StepId = record.StepId,
                            ContinuationId = record.ContinuationId, FrameActivationId = record.FrameActivationId,
                            ActivityName = record.WorkflowName, StepNumber = record.StepNumber, Payload = Facts(record, attempt) });
                }
                else
                {
                    attempt.Phase = "Ended";
                    attempt.EndedAtUtc = now;
                    attempt.Disposition = "AbandonedBeforeStart";
                    record.Disposition = attempt.Disposition;
                }
            }
            if (record.Disposition == null && (WorkflowStoreRules.IsTerminal(instance.Status) ||
                    continuation == null || continuation.CancellationUnwind && continuation.PendingActivity == null ||
                    continuation.Status is ContinuationStatus.Cancelled or ContinuationStatus.Completed))
            {
                checkpoint.TouchInvocation(record.Id);
                record.Disposition = continuation?.CancellationUnwind == true ? "Cancelled" :
                    WorkflowStoreRules.IsTerminal(instance.Status) ? instance.Status.ToString() : "Abandoned";
                record.RetryAfterUtc = null;
                if (attempt.Phase != "Ended")
                {
                    attempt.Phase = "Ended";
                    attempt.EndedAtUtc = now;
                    attempt.Disposition = record.Disposition;
                }
            }
            if (record.Disposition != null && (attempt.Phase != "Ended" ||
                    attempt.Disposition != record.Disposition || attempt.RetryAfterUtc != null))
            {
                checkpoint.TouchInvocation(record.Id);
                attempt.Phase = "Ended";
                attempt.EndedAtUtc ??= now;
                attempt.Disposition = record.Disposition;
                attempt.RetryAfterUtc = null;
            }
            if (record.Disposition != null &&
                (previousPhase != attempt.Phase || previousDisposition != attempt.Disposition || previousDeadline != attempt.RetryAfterUtc) &&
                checkpoint.Trace.Accepts(TraceEntryType.ActivityCompleted, now))
                checkpoint.Trace.Add(new ExecutionTraceEntry { Timestamp = now,
                    EntryType = TraceEntryType.ActivityCompleted, StepId = record.StepId,
                    ContinuationId = record.ContinuationId, FrameActivationId = record.FrameActivationId,
                    ActivityName = record.WorkflowName, StepNumber = record.StepNumber, Payload = Facts(record, attempt) });
        }
        checkpoint.InvocationIndex = checkpoint.InvocationRecords.Values.Select(record =>
        {
            var attempt = record.Attempts.LastOrDefault();
            return new InvocationIndexEntry(record.Id, record.StepId, record.ContinuationId,
                attempt?.Number ?? 0, attempt?.ChildInstanceId != null, record.Disposition == null);
        }).ToList();
    }
}
