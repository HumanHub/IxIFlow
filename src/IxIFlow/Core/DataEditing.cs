using System.Text.Json;

namespace IxIFlow.Core;

/// <summary>A paused data view. A null continuation selects the root fork baseline.</summary>
public sealed record DataEditTarget(long PauseGeneration, string? ContinuationId = null);

/// <summary>A path of serialized property names and its JSON value, or a property removal.</summary>
public sealed record DataPatch(IReadOnlyList<string> Path, JsonElement Value, bool Remove = false);

/// <summary>The immutable request retained until its ordered edit is committed.</summary>
public sealed record DataEditRequest(long ExpectedRevision, DataEditTarget Target, IReadOnlyList<DataPatch> Patches);

/// <summary>A durable receipt. Retrying the edit ID returns this original result.</summary>
public sealed record DataEditResult(string InstanceId, string EditId, long Revision, DataEditTarget Target);

/// <summary>Keyed digests of a property's old and new serialized values, without either value.</summary>
public sealed record DataEditDigest(IReadOnlyList<string> Path, string BeforeDigest, string AfterDigest);

/// <summary>Mandatory audit facts, committed atomically with the edit and its receipt.</summary>
public sealed record DataEditedTrace(string Actor, string EditId, DataEditTarget Target, long Revision,
    IReadOnlyList<DataEditDigest> Changes) : TracePayload;
