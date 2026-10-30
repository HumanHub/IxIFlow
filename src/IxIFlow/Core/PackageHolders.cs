using IxIFlow.Templates;

namespace IxIFlow.Core;

public sealed record TemplateVersionReference(string Key, int Version);

public sealed record PackageHolders(
    IReadOnlyList<WorkflowVersionReference> Workflows,
    IReadOnlyList<TemplateVersionReference> Templates,
    IReadOnlyList<WorkflowPackageReference> Packages,
    bool InstancePins,
    bool UnreadableInstancePins,
    bool OutsideScope);

internal static class PackageHolderRules
{
    internal static PackageHolders Read(string id, string version, CatalogScope scope, IReadOnlyList<WorkflowVersion> workflows,
        IReadOnlyList<TemplateVersionEntry> templates, IReadOnlyList<WorkflowPackage> packages, IReadOnlyList<ArtifactDigest> pins,
        IReadOnlyDictionary<ArtifactDigest, WorkflowCatalogRules.OrphanPin> orphans)
    {
        var package = packages.SingleOrDefault(item => item.Id == id && item.Version == version);

        if (!scope.Allows("package", id))
            return new([], [], [], false, false, true);

        if (package == null)
            return new([], [], [], false, false, false);

        var digest = package.Digest;

        var activeOrphans = orphans.Where(item => pins.Contains(item.Key) && !workflows.Any(row => row.ArtifactDigest == item.Key))
            .Select(item => item.Value).ToArray();

        var workflowHolders = workflows.Where(item => WorkflowCatalogRules.ArtifactReferences([item]).Contains(digest)).ToArray();

        var templateHolders = templates
            .Where(item => item.Version.ModelPackages.Any(reference => reference.Id == id && reference.Version == version)).ToArray();

        var packageHolders = packages
            .Where(item => item.Closure?.Any(reference => reference.Id == id && reference.Version == version) == true).ToArray();

        var unreadable = activeOrphans.Any(item => item.Error != null);

        return new(
            workflowHolders.Where(item => scope.Allows(WorkflowDraftKinds.Workflow, item.Name))
                .Select(item => new WorkflowVersionReference(item.Name, item.Version)).ToArray(),
            templateHolders.Where(item => scope.Allows(WorkflowDraftKinds.Template, item.Version.Key))
                .Select(item => new TemplateVersionReference(item.Version.Key, item.Version.Version)).ToArray(),
            packageHolders.Where(item => scope.Allows("package", item.Id))
                .Select(item => new WorkflowPackageReference(item.Id, item.Version, item.Digest)).ToArray(),
            workflowHolders.Any(item => item.ArtifactDigest is { } bundle && pins.Contains(bundle)) ||
            activeOrphans.Any(item => item.Packages.Contains(digest)) || unreadable,
            unreadable, workflowHolders.Any(item => !scope.Allows(WorkflowDraftKinds.Workflow, item.Name)) ||
            templateHolders.Any(item => !scope.Allows(WorkflowDraftKinds.Template, item.Version.Key)) ||
            packageHolders.Any(item => !scope.Allows("package", item.Id)));
    }
}