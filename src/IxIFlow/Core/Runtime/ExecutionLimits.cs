namespace IxIFlow.Core.Runtime;

/// <summary>Limits the host applies to every instance it runs (A-08, B-08, B-09).</summary>
internal sealed record ExecutionLimits
{
    public const long DefaultMaxCheckpointBytes = 4 * 1024 * 1024;
    public const int DefaultMaxCrashes = 3;
    public const int DefaultMaxActiveBranches = 1_000;
    public const int DefaultMaxActivitiesPerRun = 100;

    public static ExecutionLimits Default { get; } = new();

    /// <summary>The largest checkpoint, in bytes of workflow data and execution state.</summary>
    public long MaxCheckpointBytes { get; init; } = DefaultMaxCheckpointBytes;

    /// <summary>How many owners in a row may lose a running instance before it is quarantined.</summary>
    public int MaxCrashes { get; init; } = DefaultMaxCrashes;

    /// <summary>
    /// How many continuations of one instance may be live at once: the root, every branch, and
    /// every parent waiting at its join. Branches that are cancelling do not count.
    /// </summary>
    public int MaxActiveBranches { get; init; } = DefaultMaxActiveBranches;

    /// <summary>How many activities one run has in flight at once; further activities wait.</summary>
    public int MaxActivitiesPerRun { get; init; } = DefaultMaxActivitiesPerRun;
}

/// <summary>A configured limit would be exceeded; nothing over it was committed (D-28).</summary>
internal abstract class ResourceLimitExceededException(string message) : Exception(message);

/// <summary>A parallel step would make more branches live than the limit allows. Nothing was forked.</summary>
internal sealed class BranchLimitExceededException(int live, int forking, int limit)
    : ResourceLimitExceededException(
        $"A parallel step would make {live + forking} branches live, over the configured limit of {limit}");
