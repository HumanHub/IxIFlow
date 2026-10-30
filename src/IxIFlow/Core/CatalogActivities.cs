namespace IxIFlow.Core;

public enum ActivityOrigin
{
    Resolved,
    Uploaded,
    Published,
    Local
}

public sealed record ActivityProvider(string PackageId, string PackageVersion, ActivityOrigin Origin, string Build);

public sealed record ActivityProviderSelector(string PackageId, string PackageVersion, string Build)
{
    public string PackageId
    {
        get;
        init => field = WorkflowCatalogRules.ValidateIdentifier(value)!;
    } = WorkflowCatalogRules.ValidateIdentifier(PackageId)!;

    public string PackageVersion
    {
        get;
        init => field = WorkflowCatalogRules.ValidateIdentifier(value)!;
    } = WorkflowCatalogRules.ValidateIdentifier(PackageVersion)!;

    public string Build
    {
        get;
        init => field = WorkflowCatalogRules.ValidateIdentifier(value)!;
    } = WorkflowCatalogRules.ValidateIdentifier(Build)!;
}

public sealed record CatalogActivityQuery(
    CatalogScope Scope,
    string? Text = null,
    string? Category = null,
    string? PackageId = null,
    ActivityOrigin? Origin = null,
    bool? Withdrawn = null,
    bool? ArtifactPresent = null,
    string? After = null,
    int Limit = 100)
{
    public string? Text
    {
        get;
        init => field = WorkflowCatalogRules.ValidateText(value);
    } = WorkflowCatalogRules.ValidateText(Text);

    public string? Category
    {
        get;
        init => field = WorkflowCatalogRules.ValidateText(value);
    } = WorkflowCatalogRules.ValidateText(Category);

    public string? PackageId
    {
        get;
        init => field = WorkflowCatalogRules.ValidateText(value);
    } = WorkflowCatalogRules.ValidateText(PackageId);
}

public sealed record CatalogPackageQuery(
    CatalogScope Scope,
    string? Text = null,
    WorkflowPackageOrigin? Origin = null,
    bool? Withdrawn = null,
    bool? ArtifactPresent = null,
    string? After = null,
    int Limit = 100)
{
    public string? Text
    {
        get;
        init => field = WorkflowCatalogRules.ValidateText(value);
    } = WorkflowCatalogRules.ValidateText(Text);
}

public sealed record CatalogActivity(ActivityDescriptor Descriptor, ActivityProvider Provider, bool Withdrawn, bool ArtifactPresent);

public sealed record CatalogActivityDetail(CatalogActivity Activity, IReadOnlyList<CatalogHost> Hosts);

public sealed record CatalogPackage(
    string Id,
    string Version,
    ArtifactDigest Digest,
    WorkflowPackageOrigin Origin,
    PackageExtraction Extraction,
    string? ExtractionDetail,
    int ActivityCount,
    bool Withdrawn,
    bool ArtifactPresent,
    long LifecycleRevision);

public sealed record CatalogPackageDetail(
    CatalogPackage Summary,
    PackageActivities Activities,
    WorkflowDraftId? PublishedDraft,
    DateTime RegisteredAtUtc,
    string? RegisteredBy,
    DateTime? WithdrawnAtUtc,
    string? WithdrawnBy);

public sealed record CatalogHost(Guid Id, string Name, DateTime HeartbeatAtUtc, string EngineVersion);

internal static class CatalogActivityRules
{
    internal static string Search(string key, string name)
    {
        WorkflowCatalogRules.ValidateText(key);
        WorkflowCatalogRules.ValidateText(name);
        var value = (key + " " + name).ToLowerInvariant();

        if (value.Length <= 400)
            return value;

        var length = char.IsHighSurrogate(value[399]) && char.IsLowSurrogate(value[400]) ? 399 : 400;

        return value[..length];
    }

    internal static string? Text(string? text)
    {
        if (text == null)
            return null;

        WorkflowCatalogRules.ValidateText(text);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(text.Length, 400);

        return text.Trim().ToLowerInvariant();
    }

    internal static string[] Key(CatalogActivity item) =>
        [item.Descriptor.Key, item.Descriptor.Version, item.Provider.PackageId, item.Provider.PackageVersion, item.Provider.Build];

    internal static int Compare(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        for (var index = 0; index < left.Count; index++)
            if (CatalogPaging.CompareDraftNames(left[index], right[index]) is var value && value != 0)
                return value;

        return 0;
    }

    internal static readonly IComparer<string[]> Keys = Comparer<string[]>.Create((left, right) => Compare(left, right));

    internal static PackageActivities Scoped(PackageActivities activities, CatalogScope scope) => activities with
    {
        Activities = Array.AsReadOnly(activities.Activities
            .Where(item => scope.Allows(WorkflowDraftKinds.Activity, item.Key) && scope.Allows("package", item.PackageId)).ToArray()),
        Closure = activities.Closure == null
            ? null
            : Array.AsReadOnly(activities.Closure.Where(item => scope.Allows("package", item.Id)).ToArray()),
        MissingPackageIds = Array.AsReadOnly(activities.MissingPackageIds.Where(item => scope.Allows("package", item)).ToArray())
    };
}