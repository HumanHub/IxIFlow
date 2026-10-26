using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace IxIFlow.Authoring;

public static class PublishingBuilderExtensions
{
    /// <summary>Adds publishing with the selected catalog/artifacts and bounded pending-package maintenance.</summary>
    public static IIxIFlowBuilder AddPublishing(this IIxIFlowBuilder builder, WorkflowPublisherOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.TryAddSingleton(options ?? new WorkflowPublisherOptions());
        services.TryAddSingleton<IWorkflowCatalogStore>(provider => provider.GetRequiredService<IWorkflowCatalog>());
        services.TryAddSingleton<WorkflowPublisher>();
        services.TryAddSingleton<PublicationService>();
        services.TryAddSingleton<PackageIndexingService>();
        services.AddHostedService(provider => provider.GetRequiredService<PublicationService>());
        services.AddHostedService(provider => provider.GetRequiredService<PackageIndexingService>());
        return builder;
    }
}
