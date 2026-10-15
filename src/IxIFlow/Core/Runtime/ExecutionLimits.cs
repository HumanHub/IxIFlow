namespace IxIFlow.Core.Runtime;

/// <summary>Limits the host applies to every instance it runs.</summary>
internal sealed record ExecutionLimits(long MaxCheckpointBytes, int MaxCrashes = ExecutionLimits.DefaultMaxCrashes)
{
    public const long DefaultMaxCheckpointBytes = 4 * 1024 * 1024;
    public const int DefaultMaxCrashes = 3;

    public static ExecutionLimits Default { get; } = new(DefaultMaxCheckpointBytes);
}
