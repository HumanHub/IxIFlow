namespace IxIFlow.Core;

/// <summary>An ID reserved after retention removed its instance. Times use the store clock.</summary>
public sealed record WorkflowTombstone(string InstanceId, DateTime RemovedAtUtc, DateTime ExpiresAtUtc);

public sealed record RetentionResult(int InstancesRemoved, int TombstonesRemoved);

internal sealed record RetentionSettings(TimeSpan FinishedRetention, TimeSpan TombstoneRetention,
    TimeSpan ScanInterval, int BatchSize)
{
    public static RetentionSettings Default { get; } = new(TimeSpan.FromDays(30), TimeSpan.FromDays(90),
        TimeSpan.FromHours(1), 100);

    public static void Validate(TimeSpan finishedRetention, TimeSpan tombstoneRetention, int batchSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(finishedRetention, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(tombstoneRetention, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
    }
}
