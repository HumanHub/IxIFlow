using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IxIFlow.Templates;

public static class TemplateServiceCollectionExtensions
{
    /// <summary>
    /// Adds template publishing and rendering, with an <see cref="InMemoryTemplateStore"/> unless another
    /// <see cref="ITemplateStore"/> is registered. Add engines on the returned builder, such as <c>AddLiquid()</c>.
    /// </summary>
    public static TemplatesBuilder AddIxIFlowTemplates(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ITemplateStore, InMemoryTemplateStore>();
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
