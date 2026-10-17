using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IxIFlow.Templates.Liquid;

public static class LiquidTemplatesBuilderExtensions
{
    /// <summary>Adds the Liquid engine, named <c>liquid</c>.</summary>
    public static TemplatesBuilder AddLiquid(this TemplatesBuilder builder, Action<LiquidEngineOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var settings = new LiquidEngineOptions();
        configure?.Invoke(settings);
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ITemplateEngine, LiquidTemplateEngine>(_ => new LiquidTemplateEngine(settings)));
        return builder;
    }
}
