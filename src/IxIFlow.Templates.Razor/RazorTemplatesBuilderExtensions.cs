using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IxIFlow.Templates.Razor;

public static class RazorTemplatesBuilderExtensions
{
    /// <summary>Adds the Razor engine, named <c>razor</c>.</summary>
    public static TemplatesBuilder AddRazor(this TemplatesBuilder builder, Action<RazorEngineOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var settings = new RazorEngineOptions();
        configure?.Invoke(settings);
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ITemplateEngine, RazorTemplateEngine>(_ => new RazorTemplateEngine(settings)));
        return builder;
    }
}
