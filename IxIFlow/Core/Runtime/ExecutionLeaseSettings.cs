namespace IxIFlow.Core.Runtime;

internal sealed record ExecutionLeaseSettings(TimeSpan Duration, TimeSpan RenewalInterval)
{
    public static ExecutionLeaseSettings Default { get; } = new(
        TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10));
}
