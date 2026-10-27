using System.Globalization;
using IxIFlow.Templates;

namespace IxIFlow.Core;

public sealed record CatalogTemplateQuery(CatalogScope Scope, string? Text = null, string? Engine = null,
    string? After = null, int Limit = 100)
{
    public string? Text { get; init => field = WorkflowCatalogRules.ValidateText(value); } = WorkflowCatalogRules.ValidateText(Text);
    public string? Engine { get; init => field = WorkflowCatalogRules.ValidateText(value); } = WorkflowCatalogRules.ValidateText(Engine);
}
public sealed record CatalogTemplate(string Key, int Version, string Engine, string Format,
    string ModelType, string ModelTypeId, IReadOnlyList<WorkflowPackageReference> ModelPackages,
    IReadOnlyList<string> Cultures, string? DisplayName, string? Description);
public sealed record CatalogTemplateDetail(CatalogTemplate Summary, WorkflowDraftId? PublishedDraft,
    DateTime PublishedAtUtc, string? PublishedBy);

internal static class CatalogTemplateRules
{
    internal static string? Engine(string? value)
    {
        WorkflowCatalogRules.ValidateText(value);
        if (value != null) ArgumentOutOfRangeException.ThrowIfGreaterThan(value.Length, 50);
        return value?.Trim().ToLowerInvariant();
    }

    internal static string Filter(CatalogTemplateQuery query, string? key)
    {
        ArgumentNullException.ThrowIfNull(query.Scope);
        return CatalogPaging.Hash(new { query.Scope.Hash, Text = CatalogActivityRules.Text(query.Text), Engine = Engine(query.Engine),
            Key = key, Kind = key == null ? "templates" : "template-versions" });
    }

    internal static (string? Key, int? Version) After(CatalogTemplateQuery query, string filter, string? key)
    {
        var parts = CatalogPaging.Decode(query.After, filter, key == null ? 2 : 1, query.Limit);
        if (parts == null) return (null, null);
        var versionText = parts[^1];
        if (!int.TryParse(versionText, NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 1)
            throw new ArgumentException("Invalid template version cursor", nameof(query));
        TemplateCatalogRules.Key(key ?? parts[0], version);
        return (key ?? parts[0], version);
    }

    internal static CatalogTemplate Summary(TemplateVersion row, CatalogScope scope) => new(row.Key, row.Version,
        row.Engine, row.Format.ToString(), row.ModelType, row.ModelTypeId,
        Array.AsReadOnly(row.ModelPackages.Where(package => scope.Allows("package", package.Id)).ToArray()),
        Array.AsReadOnly(row.Variants.Select(variant => variant.Culture).ToArray()), row.DisplayName, row.Description);

    internal static string Cursor(string filter, CatalogTemplate row, bool versions) => versions
        ? CatalogPaging.Encode(filter, row.Version.ToString(CultureInfo.InvariantCulture))
        : CatalogPaging.Encode(filter, row.Key, row.Version.ToString(CultureInfo.InvariantCulture));

    internal static IReadOnlyList<CatalogStepDescriptor> Steps(CatalogScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return Array.AsReadOnly(new[] { TemplateStepDescriptors.RenderTemplate(), ScopeStepDescriptors.Scope() });
    }
}
