using IxIFlow.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IxIFlow.Extensions;

public static class ArtifactStoreExtensions
{
    /// <summary>Chooses immutable files for artifacts on a single host.</summary>
    public static IIxIFlowBuilder UseFileSystemArtifacts(this IIxIFlowBuilder builder, string directory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        builder.Services.Replace(ServiceDescriptor.Singleton<IArtifactStore>(_ => new FileSystemArtifactStore(directory)));
        return builder;
    }
}
