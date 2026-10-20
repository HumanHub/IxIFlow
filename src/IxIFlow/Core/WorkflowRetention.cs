namespace IxIFlow.Core;

/// <summary>An ID reserved after retention removed its instance. Times use the store clock.</summary>
public sealed record WorkflowTombstone(string InstanceId, DateTime RemovedAtUtc, DateTime ExpiresAtUtc);

public sealed record RetentionResult(int InstancesRemoved, int TombstonesRemoved)
{
    /// <summary>Finished candidates kept by never-attempted pending deliveries, counted per publisher.</summary>
    public IReadOnlyDictionary<string, int> SkippedUnattemptedByPublisher { get; init; } =
        new Dictionary<string, int>();
}

internal sealed record RetentionSettings(TimeSpan FinishedRetention, TimeSpan TombstoneRetention,
    TimeSpan ScanInterval, int BatchSize)
{
    public TimeSpan ArtifactRetention { get; init; } = TimeSpan.FromDays(90);

    public static RetentionSettings Default { get; } = new(TimeSpan.FromDays(30), TimeSpan.FromDays(90),
        TimeSpan.FromHours(1), 100);

    public static void Validate(TimeSpan finishedRetention, TimeSpan tombstoneRetention, int batchSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(finishedRetention, TimeSpan.FromMinutes(1));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(tombstoneRetention, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
    }
}
