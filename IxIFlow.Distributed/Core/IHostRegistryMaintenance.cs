namespace IxIFlow.Core;

/// <summary>Optional retention support for persistent host metrics.</summary>
public interface IHostRegistryMaintenance
{
    Task<int> PruneMetricsAsync(TimeSpan retention, int batchSize,
        CancellationToken cancellationToken = default);
}
