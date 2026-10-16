namespace IxIFlow.Core.Runtime;

/// <summary>Limits the host applies to every instance it runs.</summary>
internal sealed record ExecutionLimits(long MaxCheckpointBytes)
{
    public const long DefaultMaxCheckpointBytes = 4 * 1024 * 1024;

    public static ExecutionLimits Default { get; } = new(DefaultMaxCheckpointBytes);
}
