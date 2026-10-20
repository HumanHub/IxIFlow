using IxIFlow.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IxIFlow.Extensions;

public static class ArtifactStoreExtensions
{
    /// <summary>Enables verified published bundles. A zero capacity unloads each bundle after its last operation.</summary>
    public static IIxIFlowBuilder UseWorkflowBundles(this IIxIFlowBuilder builder, string cacheDirectory, int maxCachedBundles = 16)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        ArgumentOutOfRangeException.ThrowIfNegative(maxCachedBundles);
        builder.Services.Replace(ServiceDescriptor.Singleton(new WorkflowBundleOptions(Path.GetFullPath(cacheDirectory), maxCachedBundles)));
        return builder;
    }
    /// <summary>Chooses immutable files for artifacts on a single host.</summary>
    public static IIxIFlowBuilder UseFileSystemArtifacts(this IIxIFlowBuilder builder, string directory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        builder.Services.Replace(ServiceDescriptor.Singleton<IArtifactStore>(_ => new FileSystemArtifactStore(directory)));
        return builder;
    }
}
