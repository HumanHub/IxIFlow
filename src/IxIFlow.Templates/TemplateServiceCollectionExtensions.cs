using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using IxIFlow.Extensions;
using IxIFlow.Core;

namespace IxIFlow.Templates;

public static class TemplateServiceCollectionExtensions
{
    /// <summary>
    /// Adds durable template publishing and rendering using the builder's catalog and artifact store.
    /// Add engines on the returned builder, such as <c>AddLiquid()</c>.
    /// </summary>
    public static TemplatesBuilder AddIxIFlowTemplates(this IIxIFlowBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        builder.AddActivity<SendTemplateMessage>();
        services.TryAddSingleton(TemplateStepDescriptors.RenderTemplate());
        services.TryAddSingleton<IWorkflowCatalogStore>(provider => provider.GetRequiredService<IWorkflowCatalog>());
        services.TryAddSingleton<TemplateEngines>();
        services.TryAddSingleton<TemplatePublisher>();
        services.TryAddSingleton<TemplateRenderer>();
        return new TemplatesBuilder(services);
    }
}

/// <summary>Adds template engines to the services <see cref="TemplateServiceCollectionExtensions.AddIxIFlowTemplates"/> set up.</summary>
public sealed class TemplatesBuilder(IServiceCollection services)
{
    public IServiceCollection Services { get; } = services;

    /// <summary>Adds an engine; engines are singletons and keep what they parse or compile.</summary>
    public TemplatesBuilder AddEngine<TEngine>() where TEngine : class, ITemplateEngine
    {
        Services.TryAddEnumerable(ServiceDescriptor.Singleton<ITemplateEngine, TEngine>());
        return this;
    }
}
