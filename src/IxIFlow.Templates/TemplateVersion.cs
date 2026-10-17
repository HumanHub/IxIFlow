using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace IxIFlow.Templates;

/// <summary>
/// One published version of a template (H-01): its engine, output format and model type, and its text in one or more
/// cultures. A version is immutable once published, and its cultures are published with it, so the variant a culture
/// resolves to never changes either. Every version has a base variant, the one without a culture, which every culture
/// falls back to.
/// </summary>
public sealed class TemplateVersion
{
    /// <summary>The culture name of the base variant.</summary>
    public const string BaseCulture = "";

    private readonly Dictionary<string, TemplateVariant> _variants;

    /// <param name="key">The template's key, such as <c>order-confirmation</c>.</param>
    /// <param name="version">The version number, from 1.</param>
    /// <param name="engine">The name of the engine that renders it, such as <c>liquid</c>.</param>
    /// <param name="format">Whether the output is plain text or HTML, which decides how values are encoded.</param>
    /// <param name="modelType">The model type it is written against, as <see cref="ModelTypeName"/> names it.</param>
    /// <param name="variants">Its text per culture, the base variant included.</param>
    public TemplateVersion(string key, int version, string engine, TemplateFormat format, string modelType,
        IEnumerable<TemplateVariant> variants)
    {
        CheckKey(key);
        if (version < 1)
            throw new ArgumentOutOfRangeException(nameof(version), "Template versions start at 1");
        ArgumentException.ThrowIfNullOrWhiteSpace(engine);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelType);
        if (!Enum.IsDefined(format))
            throw new ArgumentOutOfRangeException(nameof(format));
        ArgumentNullException.ThrowIfNull(variants);
        Key = key;
        Version = version;
        Engine = engine;
        Format = format;
        ModelType = modelType;
        _variants = new Dictionary<string, TemplateVariant>(StringComparer.OrdinalIgnoreCase);
        foreach (var variant in variants)
        {
            ArgumentNullException.ThrowIfNull(variant, nameof(variants));
            if (!_variants.TryAdd(variant.Culture, variant))
                throw new ArgumentException(
                    $"Template '{key}' v{version} has two variants for culture '{variant.Culture}'", nameof(variants));
        }
        if (!_variants.ContainsKey(BaseCulture))
            throw new ArgumentException(
                $"Template '{key}' v{version} has no base variant, the one without a culture that every culture falls back to",
                nameof(variants));
        Variants = _variants.Values.OrderBy(variant => variant.Culture, StringComparer.OrdinalIgnoreCase).ToList()
            .AsReadOnly();
    }

    /// <summary>A version written against <typeparamref name="TModel"/>.</summary>
    public static TemplateVersion Create<TModel>(string key, int version, string engine, TemplateFormat format,
        params TemplateVariant[] variants) =>
        new(key, version, engine, format, ModelTypeName(typeof(TModel)), variants);

    public string Key { get; }

    public int Version { get; }

    public string Engine { get; }

    public TemplateFormat Format { get; }

    /// <summary>The model type the version is written against, as <see cref="ModelTypeName"/> names it.</summary>
    public string ModelType { get; }

    /// <summary>The variants, ordered by culture name, the base variant first.</summary>
    public IReadOnlyList<TemplateVariant> Variants { get; }

    public TemplateReference Reference => new(Key, Version);

    /// <summary>
    /// The variant for <paramref name="culture"/>: that culture's own, else its nearest parent's (<c>de-AT</c>, then
    /// <c>de</c>), else the base variant.
    /// </summary>
    public TemplateVariant Resolve(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        for (var current = culture; !Equals(current, CultureInfo.InvariantCulture); current = current.Parent)
        {
            if (_variants.TryGetValue(current.Name, out var variant))
                return variant;
        }
        return _variants[BaseCulture];
    }

    /// <summary>
    /// Whether <paramref name="other"/> is the same version with the same content, so publishing it again changes
    /// nothing.
    /// </summary>
    public bool IsSameAs(TemplateVersion other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Key == other.Key && Version == other.Version &&
               string.Equals(Engine, other.Engine, StringComparison.OrdinalIgnoreCase) &&
               Format == other.Format && ModelType == other.ModelType &&
               Variants.Count == other.Variants.Count &&
               Variants.Zip(other.Variants).All(pair =>
                   string.Equals(pair.First.Culture, pair.Second.Culture, StringComparison.OrdinalIgnoreCase) &&
                   pair.First.Content == pair.Second.Content);
    }

    /// <summary>
    /// The name a version records for its model type: the type's full name with its generic arguments and without
    /// assembly versions, so the same type loaded from another copy of its assembly has the same name.
    /// </summary>
    public static string ModelTypeName(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.ToString();
    }

    internal static void CheckKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Trim() != key)
            throw new ArgumentException("A template key has no leading or trailing white space", nameof(key));
    }
}

/// <summary>A template version's text in one culture.</summary>
public sealed class TemplateVariant
{
    /// <param name="culture">A culture name such as <c>de</c> or <c>de-AT</c>; empty for the base variant.</param>
    /// <param name="content">The template text.</param>
    public TemplateVariant(string culture, string content)
    {
        ArgumentNullException.ThrowIfNull(culture);
        ArgumentNullException.ThrowIfNull(content);
        Culture = culture.Length == 0 ? TemplateVersion.BaseCulture : CultureNamed(culture).Name;
        Content = content;
        Digest = DigestOf(content);
    }

    public string Culture { get; }

    public string Content { get; }

    /// <summary>The SHA-256 of the content, which engines key their parsed or compiled templates by.</summary>
    public string Digest { get; }

    internal static string DigestOf(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    internal static CultureInfo CultureNamed(string name)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(name, predefinedOnly: true);
            if (Equals(culture, CultureInfo.InvariantCulture))
                throw new CultureNotFoundException(nameof(name), name, "The invariant culture is the base variant");
            return culture;
        }
        catch (CultureNotFoundException error)
        {
            throw new ArgumentException($"'{name}' is not a known culture", nameof(name), error);
        }
    }
}
