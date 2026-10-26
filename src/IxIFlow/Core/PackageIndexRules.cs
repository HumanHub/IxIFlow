using System.Text.Json;

namespace IxIFlow.Core;

internal static class PackageIndexRules
{
    internal static PackageActivities Snapshot(WorkflowPackage package, PackageActivities activities)
    {
        ArgumentNullException.ThrowIfNull(activities);
        WorkflowPackageRules.Validate(package);
        if (!Enum.IsDefined(package.Origin) || !Enum.IsDefined(activities.Extraction) ||
            activities.Activities == null || activities.MissingPackageIds == null || activities.Detail?.Length > 400 ||
            activities.Activities.Any(item => item == null || item.PackageId != package.Id || item.PackageVersion != package.Version) ||
            activities.Activities.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() != activities.Activities.Count ||
            activities.Extraction is PackageExtraction.Indexed or PackageExtraction.Unloadable && (activities.Closure == null || activities.MissingPackageIds.Count != 0) ||
            activities.Extraction != PackageExtraction.Indexed && activities.Activities.Count != 0 ||
            activities.Extraction == PackageExtraction.Pending && activities.Closure != null ||
            activities.MissingPackageIds.Any(id => !WorkflowPackageRules.ValidId(id) || id != id.ToLowerInvariant()))
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Invalid package extraction result");
        if (activities.Closure?.Any(item => item == null) == true)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Invalid package closure");
        var closure = activities.Closure?.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
        if (closure != null && (closure.Any(item => item == null || item.Id == package.Id ||
            WorkflowPackageRules.NormalizeId(item.Id) != item.Id || WorkflowPackageRules.NormalizeVersion(item.Version) != item.Version) ||
            closure.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != closure.Length))
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Invalid package closure");
        foreach (var item in closure ?? []) _ = new ArtifactDigest(item.Digest.Value);
        return activities with
        {
            Activities = Array.AsReadOnly(activities.Activities.OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => ActivityDescriptor.Parse(item.ToJson())).ToArray()),
            Closure = closure == null ? null : Array.AsReadOnly(closure),
            MissingPackageIds = Array.AsReadOnly(activities.MissingPackageIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())
        };
    }

    internal static bool Equal(PackageActivities left, PackageActivities right) =>
        JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);

    internal static PackageActivities Merge(PackageActivities stored, PackageActivities incoming)
    {
        if (stored.Closure != null && incoming.Closure != null && !stored.Closure.SequenceEqual(incoming.Closure))
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "A resolved package closure is immutable");
        if (stored.Extraction == PackageExtraction.Indexed)
        {
            if (incoming.Extraction == PackageExtraction.Indexed && !Equal(stored, incoming))
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "Indexed package activities and closure are immutable");
            return stored;
        }
        if (stored.Extraction == PackageExtraction.Pending && incoming.Extraction == PackageExtraction.Pending) return incoming;
        return incoming.Extraction == PackageExtraction.Indexed ? incoming : stored;
    }

    internal static async Task CheckArtifactsAsync(WorkflowPackage package, PackageActivities activities, IArtifactStore artifacts)
    {
        foreach (var digest in (activities.Closure ?? []).Select(item => item.Digest).Append(package.Digest).Distinct())
            if (!await artifacts.ExistsAsync(digest))
                throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, $"Package artifact {digest} is unavailable");
    }

    internal static WorkflowPackage Stamp(WorkflowPackage package, WorkflowPackage? stored, PackageActivities activities,
        DateTime now, string? actor, long revision) => package with
    {
        Origin = stored == null || package.Origin > stored.Origin ? package.Origin : stored.Origin,
        Closure = activities.Closure, RegisteredAtUtc = stored?.RegisteredAtUtc ?? now,
        RegisteredBy = stored == null ? actor : stored.RegisteredBy,
        PublishedDraft = stored?.PublishedDraft, PublishedAtUtc = stored?.PublishedAtUtc, PublishedBy = stored?.PublishedBy,
        Withdrawn = stored?.Withdrawn ?? false, WithdrawnAtUtc = stored?.WithdrawnAtUtc, WithdrawnBy = stored?.WithdrawnBy,
        LifecycleRevision = revision
    };

    internal static void Registration(WorkflowPackage package, string? actor)
    {
        WorkflowPublicationRules.Actor(actor);
        if (package.Origin is not (WorkflowPackageOrigin.Resolved or WorkflowPackageOrigin.Uploaded) || package.PublishedDraft != null)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Only resolved or uploaded packages can be registered");
    }

    internal static string PublishedVersion(WorkflowDraftId draft) =>
        draft.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".0.0";

    internal static void Publication(WorkflowDraftId draft, WorkflowPackage package, string? actor)
    {
        WorkflowPublicationRules.Id(draft);
        WorkflowPublicationRules.Actor(actor);
        if (draft.Kind != WorkflowDraftKinds.Activity || package.Id != draft.Name || package.Version != PublishedVersion(draft) ||
            package.PublishedDraft != draft || package.Origin != WorkflowPackageOrigin.Published)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Package and activity draft identities differ");
    }

    internal static bool PublicationWinner(WorkflowDraftId draft, WorkflowPackage? existing, WorkflowDraft? current)
    {
        if (existing != null)
        {
            if (existing.PublishedDraft == draft) return true;
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "The package publication identity is occupied");
        }
        if (current?.Id != draft) throw new CatalogPreconditionException("The definition identity changed");
        return false;
    }

    internal static void PublicationActivities(WorkflowDraftId draft, PackageActivities activities)
    {
        if (activities.Extraction != PackageExtraction.Indexed || activities.Activities.Count != 1 ||
            activities.Activities[0].Key != draft.Name ||
            activities.Activities[0].Version != draft.Version.ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Publication requires exactly the matching activity descriptor");
    }

    internal static bool Restoring(WorkflowPackage? existing, WorkflowPackage incoming, bool completion) =>
        existing?.Withdrawn == true && incoming.Origin == WorkflowPackageOrigin.Uploaded && !completion;

    internal static void Token(WorkflowPackage row, CatalogMutationToken expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (row.LifecycleRevision != expected.Revision) throw new CatalogPreconditionException("The package lifecycle revision changed");
    }

    internal static void Withdrawal(bool withdrawn)
    {
        if (!withdrawn) throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "Restore a package by uploading identical verified bytes");
    }
}
