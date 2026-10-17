namespace IxIFlow.Templates;

/// <summary>
/// The text an engine validates or renders: one variant of one template version, with what the engine needs to know
/// about it.
/// </summary>
public sealed record TemplateSource(
    string Key,
    int Version,
    string Culture,
    TemplateFormat Format,
    string Content,
    string Digest)
{
    public static TemplateSource Of(TemplateVersion version, TemplateVariant variant)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(variant);
        return new(version.Key, version.Version, variant.Culture, version.Format, variant.Content, variant.Digest);
    }

    /// <summary>The variant's name in messages, such as <c>order-confirmation v3 (de)</c>.</summary>
    public string Name => Culture.Length == 0 ? $"{Key} v{Version}" : $"{Key} v{Version} ({Culture})";
}
