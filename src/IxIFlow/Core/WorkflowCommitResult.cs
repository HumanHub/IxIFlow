using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IxIFlow.Core;

public enum WorkflowCommitStatus
{
    /// <summary>The checkpoint was saved.</summary>
    Applied,

    /// <summary>The same commit was saved before; this is its original receipt.</summary>
    AlreadyApplied,

    /// <summary>The stored revision is not the expected one.</summary>
    Conflict,

    /// <summary>The commit's execution epoch is no longer the instance's current owner.</summary>
    StaleEpoch,

    /// <summary>The store did not apply the commit and it can be sent again unchanged.</summary>
    Retry
}

/// <summary>
/// A durable commit receipt. On conflict or a stale epoch, Revision is the current stored revision.
/// </summary>
public sealed record WorkflowCommitResult(WorkflowCommitStatus Status, long Revision);

/// <summary>
/// One checkpoint write: the serialized instance, fenced by the owner's execution epoch and the
/// revision it was based on. A commit is immutable, so sending the same commit again is safe.
/// </summary>
public sealed class CheckpointCommit
{
    private CheckpointCommit(string instanceId, string payload, long epoch, string commitId,
        long expectedRevision)
    {
        InstanceId = instanceId;
        Payload = payload;
        Epoch = epoch;
        CommitId = commitId;
        ExpectedRevision = expectedRevision;
        _payloadHash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
    }

    private readonly byte[] _payloadHash;

    public string InstanceId { get; }

    /// <summary>The instance as JSON, to be stored at <see cref="ExpectedRevision"/> + 1.</summary>
    public string Payload { get; }

    /// <summary>The execution epoch granted with the owner's lease; zero when the instance was never leased.</summary>
    public long Epoch { get; }

    public string CommitId { get; }

    public long ExpectedRevision { get; }

    /// <summary>SHA-256 of <see cref="Payload"/>; a reused commit ID must carry the same hash. Returns a copy.</summary>
    public byte[] PayloadHash => (byte[])_payloadHash.Clone();

    public static CheckpointCommit Create(WorkflowInstance instance, long epoch, long expectedRevision,
        string? commitId = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(instance.InstanceId);
        ArgumentOutOfRangeException.ThrowIfNegative(epoch);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        commitId ??= Guid.NewGuid().ToString("N");
        ArgumentException.ThrowIfNullOrWhiteSpace(commitId);
        if (commitId.Length > 100)
            throw new ArgumentException("Commit IDs must be at most 100 characters", nameof(commitId));
        return new CheckpointCommit(instance.InstanceId, JsonSerializer.Serialize(instance), epoch, commitId,
            expectedRevision);
    }

    /// <summary>A fresh copy of the committed instance.</summary>
    public WorkflowInstance ReadInstance() =>
        JsonSerializer.Deserialize<WorkflowInstance>(Payload)
        ?? throw new InvalidOperationException("A checkpoint payload could not be read");
}
