using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Templates;

/// <summary>
/// One published version of a template: its engine, output format and model type, and its text in one or more
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
    /// <param name="modelType">The model type's canonical C# name.</param>
    /// <param name="modelTypeId">The model's saved type identity.</param>
    /// <param name="modelPackages">All model and type-argument packages and their complete closure.</param>
    /// <param name="displayName">Optional display name.</param>
    /// <param name="description">Optional description.</param>
    /// <param name="variants">Its text per culture, the base variant included.</param>
    [JsonConstructor]
    public TemplateVersion(string key, int version, string engine, TemplateFormat format, string modelType,
        string modelTypeId, IReadOnlyList<WorkflowPackageReference> modelPackages, string? displayName,
        string? description, IReadOnlyList<TemplateVariant> variants)
    {
        CheckKey(key);
        if (version < 1)
            throw new ArgumentOutOfRangeException(nameof(version), "Template versions start at 1");
        ArgumentException.ThrowIfNullOrWhiteSpace(engine);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelType);
        WorkflowCatalogRules.ValidateText(modelType);
        WorkflowCatalogRules.ValidateText(modelTypeId);
        if (engine != "liquid") throw new ArgumentException("Only the liquid engine is supported", nameof(engine));
        if (!WorkflowTypeIdentity.IsSavedName(modelTypeId) || WorkflowTypeIdentity.CSharpName(modelTypeId) != modelType)
            throw new ArgumentException("Model type and saved identity must agree", nameof(modelTypeId));
        ArgumentNullException.ThrowIfNull(modelPackages);
        if (displayName?.Length > 200 || displayName != null && string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("Display name must be nonblank and at most 200 characters", nameof(displayName));
        if (displayName != null) WorkflowCatalogRules.ValidateText(displayName);
        if (description != null) WorkflowCatalogRules.ValidateText(description);
        if (modelPackages.Any(item => item == null || !WorkflowPackageRules.ValidId(item.Id) ||
            WorkflowPackageRules.NormalizeId(item.Id) != item.Id || WorkflowPackageRules.NormalizeVersion(item.Version) != item.Version) ||
            modelPackages.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != modelPackages.Count)
            throw new ArgumentException("Model packages require unique canonical package identities", nameof(modelPackages));
        foreach (var package in modelPackages) _ = new ArtifactDigest(package.Digest.Value);
        if (!Enum.IsDefined(format))
            throw new ArgumentOutOfRangeException(nameof(format));
        ArgumentNullException.ThrowIfNull(variants);
        Key = key;
        Version = version;
        Engine = engine;
        Format = format;
        ModelType = modelType;
        ModelTypeId = modelTypeId;
        ModelPackages = Array.AsReadOnly(modelPackages.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray());
        DisplayName = displayName;
        Description = description;
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
        Variants = _variants.Values.OrderBy(variant => variant.Culture, StringComparer.Ordinal).ToList()
            .AsReadOnly();
    }

    /// <summary>A version written against <typeparamref name="TModel"/>.</summary>
    public static TemplateVersion Create<TModel>(string key, int version, string engine, TemplateFormat format,
        params TemplateVariant[] variants)
    {
        TemplateModelTypes.Validate(typeof(TModel));
        return new(key, version, engine, format, ModelTypeName(typeof(TModel)),
            WorkflowTypeIdentity.SavedName(typeof(TModel)), [], null, null, variants);
    }

    public string Key { get; }

    public int Version { get; }

    public string Engine { get; }

    public TemplateFormat Format { get; }

    /// <summary>The model type the version is written against, as <see cref="ModelTypeName"/> names it.</summary>
    public string ModelType { get; }
    public string ModelTypeId { get; }
    public IReadOnlyList<WorkflowPackageReference> ModelPackages { get; }
    public string? DisplayName { get; }
    public string? Description { get; }

    /// <summary>The variants, ordered by culture name, the base variant first.</summary>
    public IReadOnlyList<TemplateVariant> Variants { get; }

    [JsonIgnore] public TemplateReference Reference => new(Key, Version);

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
               Format == other.Format && ModelType == other.ModelType && ModelTypeId == other.ModelTypeId &&
               ModelPackages.SequenceEqual(other.ModelPackages) && DisplayName == other.DisplayName && Description == other.Description &&
               Variants.Count == other.Variants.Count &&
               Variants.Zip(other.Variants).All(pair =>
                   string.Equals(pair.First.Culture, pair.Second.Culture, StringComparison.OrdinalIgnoreCase) &&
                   pair.First.Content == pair.Second.Content);
    }

    /// <summary>
    /// The canonical, fully qualified C# name, including constructed arguments and public nested types.
    /// </summary>
    public static string ModelTypeName(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return WorkflowTypeIdentity.CSharpName(type);
    }

    internal static void CheckKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > 100 || key != key.ToLowerInvariant() || !WorkflowPackageRules.ValidId(key))
            throw new ArgumentException("A template key must use lowercase package-id grammar, 1–100 characters", nameof(key));
    }
}

/// <summary>A template version's text in one culture.</summary>
public sealed class TemplateVariant
{
    /// <param name="culture">A culture name such as <c>de</c> or <c>de-AT</c>; empty for the base variant.</param>
    /// <param name="content">The template text.</param>
    [JsonConstructor]
    public TemplateVariant(string culture, string content)
    {
        ArgumentNullException.ThrowIfNull(culture);
        ArgumentNullException.ThrowIfNull(content);
        WorkflowCatalogRules.ValidateText(content, allowNul: true);
        Culture = culture.Length == 0 ? TemplateVersion.BaseCulture : CultureNamed(culture).Name;
        Content = content;
        Digest = DigestOf(content);
    }

    public string Culture { get; }

    public string Content { get; }

    /// <summary>The SHA-256 of the content, which engines key their parsed or compiled templates by.</summary>
    [JsonIgnore] public string Digest { get; }

    public static string DigestOf(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    public static CultureInfo CultureNamed(string name)
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
