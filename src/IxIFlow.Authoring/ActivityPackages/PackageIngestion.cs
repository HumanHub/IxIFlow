using IxIFlow.Core;

namespace IxIFlow.Authoring;

/// <summary>A verified archive proposed for ingestion. Storage revalidates caller-created values.</summary>
public sealed record PackageUploadCandidate(WorkflowPackage Package, ReadOnlyMemory<byte> Bytes);

/// <summary>Bounded package verification and descriptor indexing, independent of document publishing.</summary>
public sealed class PackageIngestion(IWorkflowCatalogStore catalog, IArtifactStore artifacts, WorkflowPublisherOptions? options = null)
{
    private readonly WorkflowPublisherOptions _options = options ?? new();

    public Task<PackageUploadCandidate> ReadPackageAsync(Stream package, CancellationToken cancellationToken = default) =>
        WorkflowPackageResolver.ReadAsync(package, _options.PackageReadLimits, cancellationToken);

    public async Task<PackageRegistration> UploadPackageAsync(Stream package, string? actor = null, CancellationToken cancellationToken = default) =>
        await StorePackageAsync(await ReadPackageAsync(package, cancellationToken), actor, cancellationToken);

    public async Task<PackageRegistration> StorePackageAsync(PackageUploadCandidate candidate, string? actor = null, CancellationToken cancellationToken = default)
    {
        var receipt = await StoreAsync(candidate, WorkflowPackageOrigin.Uploaded, actor, cancellationToken);
        using var indexing = new PackageIndexingService(catalog, artifacts, _options);
        await indexing.RevisitAsync([receipt.Stored.Id, .. (receipt.Stored.Closure ?? []).Select(item => item.Id)], cancellationToken);
        await indexing.SweepAsync(cancellationToken);
        return receipt;
    }

    internal async Task<PackageRegistration> RetryAsync(WorkflowPackage stored, CancellationToken token)
    {
        await using var bytes = await artifacts.OpenAsync(stored.Digest, token)
            ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, "Pending package artifact is unavailable");
        var candidate = await ReadPackageAsync(bytes, token);
        if (candidate.Package.Id != stored.Id || candidate.Package.Version != stored.Version)
            throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, "Pending package archive identity differs from its stored identity");
        WorkflowPackageRules.CheckRegistration(stored, candidate.Package);
        return await StoreAsync(candidate, stored.Origin, null, token, new(stored.Id, stored.Version, stored.Digest));
    }

    private async Task<PackageRegistration> StoreAsync(PackageUploadCandidate candidate, WorkflowPackageOrigin origin, string? actor, CancellationToken token,
        WorkflowPackageReference? completion = null)
    {
        WorkflowPublicationRules.Actor(actor);
        var verified = await WorkflowPackageResolver.VerifyCandidateAsync(candidate, _options.PackageReadLimits, token);
        var existing = await catalog.GetPackageAsync(verified.Package.Id, verified.Package.Version);
        WorkflowPackageRules.CheckRegistration(existing, verified.Package);
        if (existing != null)
        {
            var indexed = await catalog.GetPackageDetailAsync(existing.Id, existing.Version, CatalogScope.Unrestricted);
            // A verified upload restores the recorded extraction/closure without resolving a
            // withdrawn root as a new-authoring dependency. The store owns the atomic restore.
            if (existing.Withdrawn)
            {
                var restored = await PersistAsync(catalog, artifacts,
                    [new(new(verified.Package with { Origin = origin }, verified.Bytes), indexed!.Activities)], actor, token, completion);
                return restored[0];
            }
            if (indexed?.Activities.Extraction == PackageExtraction.Indexed && await ClosureAvailableAsync(indexed.Activities))
            {
                var replayed = await PersistAsync(catalog, artifacts,
                    [new(new(verified.Package with { Origin = origin }, verified.Bytes), indexed.Activities)], actor, token, completion);
                return replayed[0];
            }
        }
        using var resolverResult = await PrepareAsync();
        var prepared = resolverResult.Prepared;
        var root = prepared.Single(item => item.Candidate.Package.Id == verified.Package.Id);
        var toStore = prepared.Where(item => item != root).Append(root with
        { Candidate = root.Candidate with { Package = root.Candidate.Package with { Origin = origin } } }).ToArray();
        var receipts = await PersistAsync(catalog, artifacts, toStore, actor, token, completion);
        return receipts[^1];

        async Task<bool> ClosureAvailableAsync(PackageActivities activities)
        {
            foreach (var reference in activities.Closure ?? [])
                if (!await artifacts.ExistsAsync(reference.Digest, token)) return false;
            return true;
        }

        async Task<ResolvedWorkflowPackages> PrepareAsync()
        {
            try
            {
                return await new WorkflowPackageResolver(catalog, artifacts, _options).ResolveAsync(
                    new List<string> { verified.Package.Id + "@" + verified.Package.Version }, PackageAcquisition.Scratch, [verified], token);
            }
            catch (PackageClosureUnavailableException unavailable)
            {
                var pending = new ResolvedWorkflowPackages();
                pending.Prepared.Add(new(verified, new([], PackageExtraction.Pending, "Package dependency closure is unavailable", null, unavailable.MissingPackageIds)));
                return pending;
            }
        }
    }

    internal static async Task<IReadOnlyList<PackageRegistration>> PersistAsync(IWorkflowCatalogStore catalog, IArtifactStore artifacts,
        IReadOnlyList<PreparedPackage> prepared, string? actor, CancellationToken token, WorkflowPackageReference? completion = null)
    {
        // Complete verification/extraction precedes all writes. Put the whole closure before accepting rows.
        foreach (var item in prepared)
        {
            if (ArtifactDigest.Of(item.Candidate.Bytes.Span) != item.Candidate.Package.Digest)
                throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, "Prepared package bytes changed");
            if (await artifacts.PutAsync(ArtifactKind.Package, new MemoryStream(item.Candidate.Bytes.ToArray(), writable: false), token) != item.Candidate.Package.Digest)
                throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, "Artifact store returned a different package digest");
        }
        var receipts = new List<PackageRegistration>();
        foreach (var item in prepared)
        {
            token.ThrowIfCancellationRequested();
            var prior = await catalog.GetPackageDetailAsync(item.Candidate.Package.Id, item.Candidate.Package.Version, CatalogScope.Unrestricted);
            var accepted = prior?.Activities.Extraction == PackageExtraction.Indexed ? prior.Activities : item.Activities;
            receipts.Add(completion?.Id == item.Candidate.Package.Id && completion.Version == item.Candidate.Package.Version
                ? await catalog.CompletePackageExtractionAsync(completion.Id, completion.Version, completion.Digest, accepted, artifacts, actor)
                : await catalog.RegisterPackageAsync(item.Candidate.Package, accepted, artifacts, actor));
        }
        return receipts;
    }
}
