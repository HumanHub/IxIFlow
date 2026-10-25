using System.Globalization;
using System.Text.Json;

namespace IxIFlow.Core;

public sealed record CatalogWorkflowQuery(CatalogScope Scope, string? Text = null, string? Tag = null,
    string? Category = null, WorkflowVersionState? State = null, string? After = null, int Limit = 100)
{
    public string? Text { get; init => field = WorkflowCatalogRules.ValidateText(value); } = WorkflowCatalogRules.ValidateText(Text);
    public string? Tag { get; init => field = WorkflowCatalogRules.ValidateText(value); } = WorkflowCatalogRules.ValidateText(Tag);
    public string? Category { get; init => field = WorkflowCatalogRules.ValidateText(value); } = WorkflowCatalogRules.ValidateText(Category);
}
public sealed record CatalogWorkflow(string Name, string DisplayName, string? Category, IReadOnlyList<string> Tags,
    string? Icon, int? LatestPublishedVersion, int VersionCount);
public sealed record CatalogWorkflowVersion(string Name, int Version, WorkflowVersionState State, string StructureHash,
    ArtifactDigest? ArtifactDigest, string ContractHash, WorkflowDescriptor Descriptor, long LifecycleRevision);
public sealed record CatalogWorkflowVersionDetail(CatalogWorkflowVersion Summary, WorkflowContract Contract,
    IReadOnlyList<WorkflowVersionDependency> Dependencies, IReadOnlyList<WorkflowPackageReference> ActivityPackages,
    bool ArtifactPresent, WorkflowDraftId? PublishedDraft, DateTime? PublishedAtUtc, string? PublishedBy);

internal static class CatalogWorkflowRules
{
    internal static CatalogWorkflowVersion Summary(WorkflowVersion row) => new(row.Name, row.Version, row.State, row.StructureHash,
        row.ArtifactDigest, row.ContractHash, WorkflowDescriptor.Parse(row.Descriptor.ToJson()), row.LifecycleRevision);
    internal static CatalogWorkflow Summarize(IEnumerable<CatalogWorkflowVersion> versions)
    {
        var rows = versions.OrderByDescending(row => row.Version).ToArray();
        var published = rows.FirstOrDefault(row => row.State == WorkflowVersionState.Published);
        var chosen = published ?? rows[0];
        return new(chosen.Name, chosen.Descriptor.DisplayName, chosen.Descriptor.Category, chosen.Descriptor.Tags,
            chosen.Descriptor.Icon, published?.Version, rows.Length);
    }
    internal static string Filter(CatalogWorkflowQuery query)
    {
        ArgumentNullException.ThrowIfNull(query.Scope);
        if (query.State is { } state && !Enum.IsDefined(state)) throw new ArgumentException("Invalid workflow state");
        if (query.Tag is { } tag && !System.Text.RegularExpressions.Regex.IsMatch(tag, @"\A[\w.-]{1,50}\z"))
            throw new ArgumentException("Invalid workflow tag");
        if (query.Category is { } category) { WorkflowCatalogRules.ValidateText(category); ArgumentOutOfRangeException.ThrowIfGreaterThan(category.Length, 100); }
        return CatalogPaging.Hash(new { query.Scope.Hash, Text = CatalogActivityRules.Text(query.Text), query.Tag, query.Category, query.State });
    }
    internal static int? AfterVersion(string[]? after) => after == null ? null :
        int.TryParse(after[0], NumberStyles.None, CultureInfo.InvariantCulture, out var version) && version > 0 ? version :
        throw new ArgumentException("Invalid workflow version cursor");
    internal static async Task<CatalogWorkflowVersionDetail> DetailAsync(WorkflowVersion row, CatalogScope scope, IArtifactStore artifacts)
    {
        var packages = row.ManifestJson == null ? [] : (JsonSerializer.Deserialize<WorkflowBundleManifest>(row.ManifestJson)
            ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, "Invalid workflow manifest")).ActivityPackages
            .Where(package => scope.Allows("package", package.Id)).Select(package => new WorkflowPackageReference(package.Id, package.Version, package.Digest)).ToArray();
        return new(Summary(row), WorkflowContract.Parse(row.ContractJson),
            (row.Dependencies ?? []).Where(dependency => scope.Allows(WorkflowDraftKinds.Workflow, dependency.Name)).ToArray(), packages,
            row.ArtifactDigest is { } digest && await artifacts.ExistsAsync(digest), row.PublishedDraft, row.PublishedAtUtc, row.PublishedBy);
    }
}
