namespace IxIFlow.Templates;

/// <summary>
/// The text an engine validates or renders: one variant of one template version, with what the engine needs to know
/// about it. Its digest is computed from its content, so engines can key what they parse or compile by it.
/// </summary>
public sealed class TemplateSource
{
    public TemplateSource(string key, int version, string culture, TemplateFormat format, string content)
        : this(key, version, culture, format, content, TemplateVariant.DigestOf(content ?? ""))
    {
    }

    // A variant computed its digest from the same content when it was created.
    private TemplateSource(string key, int version, string culture, TemplateFormat format, string content,
        string digest)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(culture);
        ArgumentNullException.ThrowIfNull(content);
        Key = key;
        Version = version;
        Culture = culture;
        Format = format;
        Content = content;
        Digest = digest;
    }

    public static TemplateSource Of(TemplateVersion version, TemplateVariant variant)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(variant);
        return new(version.Key, version.Version, variant.Culture, version.Format, variant.Content, variant.Digest);
    }

    public string Key { get; }

    public int Version { get; }

    public string Culture { get; }

    public TemplateFormat Format { get; }

    public string Content { get; }

    /// <summary>The SHA-256 of <see cref="Content"/>.</summary>
    public string Digest { get; }

    /// <summary>The variant's name in messages, such as <c>order-confirmation v3 (de)</c>.</summary>
    public string Name => Culture.Length == 0 ? $"{Key} v{Version}" : $"{Key} v{Version} ({Culture})";

    public override string ToString() => Name;
}
