namespace IxIFlow.Dsl.Compilation;

public interface IActivityRegistry
{
    ValueTask<ActivityDescriptor?> FindAsync(string activityKey, CancellationToken cancellationToken = default);
}

public sealed record ActivityDescriptor
{
    public string Key { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string PackageName { get; init; } = string.Empty;
    public string PackageVersion { get; init; } = string.Empty;
    public Type ActivityType { get; init; } = typeof(object);
}
