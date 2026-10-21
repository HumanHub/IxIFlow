namespace IxIFlow.Core.Runtime;

/// <summary>Limits the host applies to every instance it runs.</summary>
public sealed record ExecutionLimits
{
    public const long DefaultMaxCheckpointBytes = 4 * 1024 * 1024;
    public const int DefaultMaxTraceEntriesPerCommit = 1_000;
    public const int DefaultMaxActiveBranches = 1_000;
    public const int DefaultMaxActivitiesPerRun = 100;
    public const int DefaultMaxForEachItems = 1_000;
    /// <summary>The default number of graph entries between prepared commits.</summary>
    public const int DefaultMaxGraphTransitionsPerPass = 1_000;
    public static readonly TimeSpan DefaultParkRetriesLongerThan = TimeSpan.FromSeconds(1);

    public static ExecutionLimits Default { get; } = new();

    /// <summary>The largest checkpoint, counting inline JSON and encoded reference metadata.</summary>
    public long MaxCheckpointBytes { get; init; } = DefaultMaxCheckpointBytes;
    public long PayloadThresholdBytes { get; init; } = PayloadStoreSettings.DefaultPayloadThresholdBytes;
    public long MaxPayloadBytes { get; init; } = PayloadStoreSettings.DefaultMaxPayloadBytes;
    /// <summary>Aggregate bytes hydrated or being transferred in one run.</summary>
    public long MaxHydratedPayloadBytes { get; init; } = 128L * 1024 * 1024;

    /// <summary>The maximum trace entries per commit, including a truncation marker when needed.</summary>
    public int MaxTraceEntriesPerCommit { get; init; } = DefaultMaxTraceEntriesPerCommit;
    public TraceDetail TraceDetail { get; init; } = TraceDetail.Full;
    public long? MaxTraceEntriesPerInstance { get; init; }

    /// <summary>
    /// Maximum live branches of one instance, excluding the root, parents waiting at joins, and
    /// completed, cancelled or cancelling branches.
    /// </summary>
    public int MaxActiveBranches { get; init; } = DefaultMaxActiveBranches;

    /// <summary>How many activities one run has in flight at once; further activities wait.</summary>
    public int MaxActivitiesPerRun { get; init; } = DefaultMaxActivitiesPerRun;

    /// <summary>The longest list a <c>ForEach</c> takes; a longer one stops the instance for an operator.</summary>
    public int MaxForEachItems { get; init; } = DefaultMaxForEachItems;

    /// <summary>Maximum entries of one graph activation between prepared commits; stops for an operator.</summary>
    public int MaxGraphTransitionsPerPass { get; init; } = DefaultMaxGraphTransitionsPerPass;

    /// <summary>
    /// A run with nothing in flight whose next retry is further away than this gives its slot and
    /// lease back and is resumed on time; a shorter backoff is slept through in the slot.
    /// </summary>
    public TimeSpan ParkRetriesLongerThan { get; init; } = DefaultParkRetriesLongerThan;

    /// <summary>Serialize every part at each commit and fail it when one changed without being marked.</summary>
    public bool VerifyCheckpointParts { get; init; }
}

/// <summary>A configured limit would be exceeded; nothing over it was committed.</summary>
internal abstract class ResourceLimitExceededException(string message) : Exception(message);

/// <summary>A parallel step would make more branches live than the limit allows. Nothing was forked.</summary>
internal sealed class BranchLimitExceededException(int live, int forking, int limit)
    : ResourceLimitExceededException(
        $"A parallel step would make {live + forking} branches live, over the configured limit of {limit}");

/// <summary>A graph would enter another node beyond its per-pass budget; its pending edge is retained.</summary>
internal sealed class GraphTransitionLimitExceededException(int limit)
    : ResourceLimitExceededException($"A graph exceeded the configured limit of {limit} entries per pass");
