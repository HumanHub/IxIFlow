using System.Globalization;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Templates;

/// <summary>
/// Renders a pinned template version: finds the version, resolves the variant for the culture, falling back to
/// its parent cultures and then to the base variant, and renders it through the version's engine. Values are
/// formatted for the culture asked for, even when a parent's variant or the base variant is the one rendered.
/// </summary>
public sealed class TemplateRenderer(IWorkflowCatalogStore store, TemplateEngines engines)
{
    /// <param name="template">The version to render.</param>
    /// <param name="model">The model, of the type the version was written against.</param>
    /// <param name="culture">A culture name such as <c>de-AT</c>; null or empty for the base variant, formatted
    /// with the invariant culture.</param>
    /// <param name="cancellationToken">Stops the render.</param>
    public async Task<RenderedTemplate> RenderAsync<TModel>(TemplateReference template, TModel model,
        string? culture, CancellationToken cancellationToken = default)
    {
        var bundled = WorkflowDefinitionScope.Resolve(template, typeof(TModel), model is not null);
        ArgumentNullException.ThrowIfNull(template);

        if (model is null)
            throw new ArgumentNullException(nameof(model), $"Template {template} needs a model to render");

        cancellationToken.ThrowIfCancellationRequested();

        var version = bundled ?? (await store.GetTemplateVersionAsync(template.Key, template.Version, cancellationToken: cancellationToken))
            ?.Version
            ?? throw new TemplateNotFoundException($"Template {template} is not published");

        var modelType = TemplateVersion.ModelTypeName(typeof(TModel));

        if (version.ModelType != modelType || version.ModelTypeId != WorkflowTypeIdentity.SavedName(typeof(TModel)))
            throw new TemplateNotFoundException(
                $"Template {template} is written against '{version.ModelType}', and the step renders it with " +
                $"'{modelType}'");

        var formatting = string.IsNullOrEmpty(culture)
            ? CultureInfo.InvariantCulture
            : TemplateVariant.CultureNamed(culture);

        var variant = version.Resolve(formatting);
        var source = TemplateSource.Of(version, variant);
        var content = await engines.Get(version.Engine).RenderAsync(source, model, typeof(TModel), formatting, cancellationToken);

        return new RenderedTemplate(version.Key, version.Version, variant.Culture, version.Format, content);
    }
}