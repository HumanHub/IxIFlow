namespace IxIFlow.Templates.Razor;

public static class RazorTemplatesBuilderExtensions
{
    /// <summary>Adds the Razor engine, named <c>razor</c>.</summary>
    public static TemplatesBuilder AddRazor(this TemplatesBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddEngine<RazorTemplateEngine>();
    }
}
