using System.Globalization;

namespace IxIFlow.Templates;

/// <summary>
/// Renders templates of one language against a typed model. An engine validates a template against its model
/// type when the template is published, and renders it with no effects beyond producing its text: the same template,
/// model and culture give the same text on any host and on any attempt.
/// </summary>
public interface ITemplateEngine
{
    /// <summary>The name template versions use to choose this engine, such as <c>liquid</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Checks that <paramref name="template"/> is valid against <paramref name="modelType"/>; empty when it is.
    /// </summary>
    ValueTask<IReadOnlyList<TemplateDiagnostic>> ValidateAsync(TemplateSource template, Type modelType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renders <paramref name="template"/> against <paramref name="model"/>, formatting values for
    /// <paramref name="culture"/>. Throws <see cref="TemplateRenderException"/> when the template cannot be rendered.
    /// </summary>
    ValueTask<string> RenderAsync<TModel>(TemplateSource template, TModel model, CultureInfo culture,
        CancellationToken cancellationToken = default);
}
