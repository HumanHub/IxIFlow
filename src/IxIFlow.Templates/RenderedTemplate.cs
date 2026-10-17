namespace IxIFlow.Templates;

/// <summary>A rendered template: its text, and which version and culture variant produced it.</summary>
/// <param name="Key">The template's key.</param>
/// <param name="Version">The version rendered.</param>
/// <param name="Culture">The culture of the variant rendered; empty when the base variant was.</param>
/// <param name="Format">Whether <paramref name="Content"/> is text or HTML.</param>
/// <param name="Content">The rendered text.</param>
public sealed record RenderedTemplate(string Key, int Version, string Culture, TemplateFormat Format, string Content);
