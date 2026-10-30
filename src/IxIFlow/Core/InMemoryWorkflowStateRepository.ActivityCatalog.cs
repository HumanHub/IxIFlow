namespace IxIFlow.Core;

public partial class InMemoryWorkflowStateRepository
{
    /// <summary>The artifact source used for current catalog availability, when files replace database artifacts.</summary>
    public IArtifactStore? CatalogArtifacts { get; set; }

    private (CatalogActivity[] Activities, long Revision) ActivitySnapshot(CatalogScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        lock (_claimLock)
        {
            var rows = new List<CatalogActivity>();

            foreach (var package in _packages.Values.Where(item => scope.Allows("package", item.Id)))
            foreach (var descriptor in _packageActivities[(package.Id, package.Version)].Activities
                         .Where(item => scope.Allows(WorkflowDraftKinds.Activity, item.Key)))
                rows.Add(new(ActivityDescriptor.Parse(descriptor.ToJson()),
                    new(package.Id, package.Version, (ActivityOrigin)package.Origin, package.Digest.Value), package.Withdrawn, false));

            foreach (var package in _installedPackages.Values.Where(item => scope.Allows("package", item.Id)))
            foreach (var descriptor in package.Activities.Where(item => scope.Allows(WorkflowDraftKinds.Activity, item.Key)))
                rows.Add(new(ActivityDescriptor.Parse(descriptor.ToJson()),
                    new(package.Id, package.Version, ActivityOrigin.Local, package.Fingerprint), false, false));

            return (rows.ToArray(), _catalogRevision);
        }
    }

    public Task<CatalogPage<CatalogActivity>> ListActivitiesAsync(CatalogActivityQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ActivityPageAsync(query, null, null, cancellationToken: cancellationToken);
    }

    public Task<CatalogPage<CatalogActivity>> ListActivityVersionsAsync(string key, CatalogVersionQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ActivityPageAsync(new(query.Scope, After: query.After, Limit: query.Limit), key, null, cancellationToken: cancellationToken);
    }

    public Task<CatalogPage<CatalogActivity>> GetPackageActivitiesAsync(string id, string version, CatalogVersionQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ActivityPageAsync(new(query.Scope, PackageId: WorkflowPackageRules.NormalizeId(id), After: query.After, Limit: query.Limit),
            null, version, cancellationToken: cancellationToken);
    }

    private async Task<CatalogPage<CatalogActivity>> ActivityPageAsync(CatalogActivityQuery query, string? activityKey,
        string? packageVersion, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowCatalogRules.ValidateIdentifier(activityKey);
        WorkflowCatalogRules.ValidateIdentifier(packageVersion);
        var text = CatalogActivityRules.Text(query.Text);
        var id = query.PackageId == null ? null : WorkflowPackageRules.NormalizeId(query.PackageId);

        if (query.Origin is { } origin && !Enum.IsDefined(origin))
            throw new ArgumentException("Invalid activity origin");

        var filter = CatalogPaging.Hash(new
        {
            query.Scope.Hash, Text = text, query.Category, PackageId = id, query.Origin, query.Withdrawn, query.ArtifactPresent,
            activityKey, packageVersion
        });

        var after = CatalogPaging.Decode(query.After, filter, 5, query.Limit);
        var snapshot = ActivitySnapshot(query.Scope);

        var rows = snapshot.Activities.Where(item => (activityKey == null || item.Descriptor.Key == activityKey) &&
                (packageVersion == null || item.Provider.PackageVersion == packageVersion) &&
                (id == null || item.Provider.PackageId == id) &&
                (text == null || CatalogActivityRules.Search(item.Descriptor.Key, item.Descriptor.Name)
                    .Contains(text, StringComparison.Ordinal)) &&
                (query.Category == null || item.Descriptor.Category == query.Category) &&
                (query.Origin == null || item.Provider.Origin == query.Origin) &&
                (query.Withdrawn == null || item.Withdrawn == query.Withdrawn) &&
                (after == null || CatalogActivityRules.Compare(CatalogActivityRules.Key(item), after) > 0))
            .OrderBy(CatalogActivityRules.Key, CatalogActivityRules.Keys);

        var page = new List<CatalogActivity>();

        foreach (var row in rows)
        {
            var present = row.Provider.Origin != ActivityOrigin.Local &&
                await (CatalogArtifacts ?? this).ExistsAsync(new(row.Provider.Build), cancellationToken);

            if (query.ArtifactPresent is { } required && present != required)
                continue;

            page.Add(row with { ArtifactPresent = present });

            if (page.Count > query.Limit)
                break;
        }

        return new(page.Take(query.Limit).ToArray(),
            page.Count > query.Limit ? CatalogPaging.Encode(filter, CatalogActivityRules.Key(page[query.Limit - 1])) : null,
            snapshot.Revision);
    }

    public async Task<CatalogActivityDetail?> GetActivityAsync(string key, string version, ActivityProviderSelector provider,
        CatalogScope scope, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowCatalogRules.ValidateIdentifier(key);
        WorkflowCatalogRules.ValidateIdentifier(version);
        provider = provider with { PackageId = WorkflowPackageRules.NormalizeId(provider.PackageId) };

        if (!scope.Allows(WorkflowDraftKinds.Activity, key) || !scope.Allows("package", provider.PackageId))
            return null;

        var row = ActivitySnapshot(scope).Activities.SingleOrDefault(item => item.Descriptor.Key == key &&
            item.Descriptor.Version == version &&
            item.Provider.PackageId == provider.PackageId && item.Provider.PackageVersion == provider.PackageVersion &&
            item.Provider.Build == provider.Build);

        if (row == null)
            return null;

        var present = row.Provider.Origin != ActivityOrigin.Local &&
            await (CatalogArtifacts ?? this).ExistsAsync(new(row.Provider.Build), cancellationToken);

        var hosts = row.Provider.Origin == ActivityOrigin.Local
            ? await CatalogLocalHosts.ReadAsync(this, this, provider.PackageId, provider.PackageVersion, provider.Build, scope,
                cancellationToken: cancellationToken)
            : [];

        return new(row with { ArtifactPresent = present }, hosts);
    }

    public Task<CatalogPage<CatalogPackage>> ListPackagesAsync(CatalogPackageQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return PackagePageAsync(query, null, cancellationToken: cancellationToken);
    }

    public Task<CatalogPage<CatalogPackage>> ListPackageVersionsAsync(string id, CatalogVersionQuery query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return PackagePageAsync(new(query.Scope, After: query.After, Limit: query.Limit), WorkflowPackageRules.NormalizeId(id),
            cancellationToken: cancellationToken);
    }

    private async Task<CatalogPage<CatalogPackage>> PackagePageAsync(CatalogPackageQuery query, string? id,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = CatalogActivityRules.Text(query.Text);

        if (query.Origin is { } origin && !Enum.IsDefined(origin))
            throw new ArgumentException("Invalid package origin");

        var filter = CatalogPaging.Hash(new { query.Scope.Hash, Text = text, query.Origin, query.Withdrawn, query.ArtifactPresent, id });
        var after = CatalogPaging.Decode(query.After, filter, 2, query.Limit);
        (WorkflowPackage Package, PackageActivities Activities)[] rows;
        long revision;

        lock (_claimLock)
        {
            revision = _catalogRevision;

            rows = _packages.Values.Where(row => query.Scope.Allows("package", row.Id) && (id == null || row.Id == id) &&
                    (text == null || row.Id.Contains(text, StringComparison.Ordinal)) &&
                    (query.Origin == null || row.Origin == query.Origin) &&
                    (query.Withdrawn == null || row.Withdrawn == query.Withdrawn) &&
                    (after == null || CatalogActivityRules.Compare([row.Id, row.Version], after) > 0))
                .OrderBy(row => row.Id, CatalogPaging.DraftNames).ThenBy(row => row.Version, CatalogPaging.DraftNames)
                .Select(row => (WorkflowPackageRules.Copy(row), _packageActivities[(row.Id, row.Version)])).ToArray();
        }

        var page = new List<CatalogPackage>();

        foreach (var (package, activities) in rows)
        {
            var present = await (CatalogArtifacts ?? this).ExistsAsync(package.Digest, cancellationToken);

            if (query.ArtifactPresent is { } required && present != required)
                continue;

            page.Add(Summary(package, activities, query.Scope, present));

            if (page.Count > query.Limit)
                break;
        }

        return new(page.Take(query.Limit).ToArray(),
            page.Count > query.Limit ? CatalogPaging.Encode(filter, page[query.Limit - 1].Id, page[query.Limit - 1].Version) : null,
            revision);
    }

    private static CatalogPackage Summary(WorkflowPackage package, PackageActivities activities, CatalogScope scope, bool present) =>
        new(package.Id, package.Version, package.Digest, package.Origin, activities.Extraction, activities.Detail,
            CatalogActivityRules.Scoped(activities, scope).Activities.Count, package.Withdrawn, present, package.LifecycleRevision);

    public async Task<CatalogPackageDetail?> GetPackageDetailAsync(string id, string version, CatalogScope scope,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowCatalogRules.ValidateIdentifier(version);
        id = WorkflowPackageRules.NormalizeId(id);

        if (!scope.Allows("package", id))
            return null;

        WorkflowPackage? package;
        PackageActivities activities;

        lock (_claimLock)
        {
            if (!_packages.TryGetValue((id, version), out package))
                return null;

            package = WorkflowPackageRules.Copy(package);
            activities = PackageIndexRules.Snapshot(package, _packageActivities[(id, version)]);
        }

        var present = await (CatalogArtifacts ?? this).ExistsAsync(package.Digest, cancellationToken);

        return new(Summary(package, activities, scope, present), CatalogActivityRules.Scoped(activities, scope), package.PublishedDraft,
            package.RegisteredAtUtc, package.RegisteredBy, package.WithdrawnAtUtc, package.WithdrawnBy);
    }
}

internal static class CatalogLocalHosts
{
    internal static async Task<IReadOnlyList<CatalogHost>> ReadAsync(IWorkflowCatalogStore catalog, IWorkflowStateRepository registry,
        string id, string version, string? build, CatalogScope scope, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowCatalogRules.ValidateIdentifier(id);
        WorkflowCatalogRules.ValidateIdentifier(version);
        WorkflowCatalogRules.ValidateIdentifier(build);

        if (!scope.Allows("package", id))
            return [];

        var packages = await catalog.GetInstalledPackagesAsync(id, cancellationToken: cancellationToken);

        var hosts = packages.Where(item => item.Version == version && (build == null || item.Fingerprint == build) &&
                item.Activities.Any(activity => scope.Allows(WorkflowDraftKinds.Activity, activity.Key))).SelectMany(item => item.Hosts)
            .ToHashSet();

        return (await registry.GetLiveMembersAsync(cancellationToken))
            .Where(member => hosts.Contains(member.Id) && member.Kind == MemberKind.Host)
            .Select(member => new CatalogHost(member.Id, member.Name, member.HeartbeatAtUtc, member.EngineVersion))
            .OrderBy(member => member.Id).ToArray();
    }
}