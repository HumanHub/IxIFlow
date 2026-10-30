namespace IxIFlow.Core.Runtime;

/// <summary>Limits the host applies to every instance it runs.</summary>
public sealed record ExecutionLimits
{
    /// <summary>Default maximum checkpoint bytes: four MiB.</summary>
    public const long DefaultMaxCheckpointBytes = 4 * 1024 * 1024;

    /// <summary>Default maximum trace entries in one commit: 1,000.</summary>
    public const int DefaultMaxTraceEntriesPerCommit = 1_000;

    /// <summary>Default maximum active branches per instance: 1,000.</summary>
    public const int DefaultMaxActiveBranches = 1_000;

    /// <summary>Default maximum concurrent activities per run: 100.</summary>
    public const int DefaultMaxActivitiesPerRun = 100;

    /// <summary>Default maximum saved ForEach items: 1,000.</summary>
    public const int DefaultMaxForEachItems = 1_000;

    /// <summary>The default number of graph entries between prepared commits.</summary>
    public const int DefaultMaxGraphTransitionsPerPass = 1_000;

    /// <summary>Default threshold for parking retries: one second.</summary>
    public static readonly TimeSpan DefaultParkRetriesLongerThan = TimeSpan.FromSeconds(1);

    /// <summary>Default execution limits for a workflow instance.</summary>
    public static ExecutionLimits Default { get; } = new();

    /// <summary>The largest checkpoint, counting inline JSON and encoded reference metadata.</summary>
    public long MaxCheckpointBytes { get; init; } = DefaultMaxCheckpointBytes;

    /// <summary>Whole-value claim-check threshold in UTF-8 bytes; zero disables claim checks.</summary>
    public long PayloadThresholdBytes { get; init; } = PayloadStoreSettings.DefaultPayloadThresholdBytes;

    /// <summary>Maximum bytes in one payload put; defaults to 64 MiB.</summary>
    public long MaxPayloadBytes { get; init; } = PayloadStoreSettings.DefaultMaxPayloadBytes;

    /// <summary>Aggregate bytes hydrated or being transferred in one run.</summary>
    public long MaxHydratedPayloadBytes { get; init; } = 128L * 1024 * 1024;

    /// <summary>The maximum trace entries per commit, including a truncation marker when needed.</summary>
    public int MaxTraceEntriesPerCommit { get; init; } = DefaultMaxTraceEntriesPerCommit;

    /// <summary>Amount of control-flow detail retained in the trace.</summary>
    public TraceDetail TraceDetail { get; init; } = TraceDetail.Full;

    /// <summary>Optional total instance trace budget; null leaves the budget unlimited.</summary>
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

    /// <summary>Maximum time allowed for scope resource cleanup; defaults to thirty seconds.</summary>
    public TimeSpan ScopeCloseTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Grace period for releasing resources after cancellation; defaults to five seconds.</summary>
    public TimeSpan ReleaseGrace { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Serialize every part at each commit and fail it when one changed without being marked.</summary>
    public bool VerifyCheckpointParts { get; init; }

    internal void Validate()
    {
        if (MaxCheckpointBytes <= 0 || PayloadThresholdBytes < 0 || MaxPayloadBytes <= 0 ||
            PayloadThresholdBytes > MaxPayloadBytes || MaxHydratedPayloadBytes <= 0 ||
            MaxTraceEntriesPerCommit <= 0 || MaxTraceEntriesPerInstance is < 0 || !Enum.IsDefined(TraceDetail) ||
            MaxActiveBranches <= 0 || MaxActivitiesPerRun <= 0 || MaxForEachItems <= 0 ||
            MaxGraphTransitionsPerPass <= 0 || ParkRetriesLongerThan < TimeSpan.Zero ||
            ScopeCloseTimeout <= TimeSpan.Zero || ReleaseGrace <= TimeSpan.Zero ||
            ScopeCloseTimeout.TotalMilliseconds > uint.MaxValue - 1 || ReleaseGrace.TotalMilliseconds > uint.MaxValue - 1)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Execution limits are invalid");
    }
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