using IxIFlow.Authoring;
using IxIFlow.Core;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "authoring")]
[Trait("Spec", "F-16")]
[Trait("Spec", "F-19")]
public sealed class ActivityPublisherScenarios
{
    private const string Source = """
                                  activity: {key: conformance.published, version: 1}
                                  input:
                                    Value: {type: int, default: 3}
                                  output:
                                    Result: {type: int}
                                  body: |
                                    throw new Exception("Publication must not execute the body");
                                  declarations: |
                                    public Published() => throw new Exception("Publication must not construct the activity");
                                  """;

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task FullActivityDryRunAndAtomicReceiptUseTheSamePortableContract(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var draft = await store.SaveDefinitionAsync(new("conformance.published", 1, "activity", Source), null, "author");
        var revision = await store.GetCatalogRevisionAsync();
        var readOnly = TypedPublishingScenarios.CatalogProbe.Wrap(store, out var probe);
        probe.ReadOnly = true;
        var report = await new WorkflowPublisher(readOnly, new ReadOnlyArtifacts(store)).ValidateAsync(draft);
        Assert.True(report.IsValid, string.Join("; ", report.Diagnostics));
        Assert.Equal(0, probe.Writes);
        Assert.Equal(revision, await store.GetCatalogRevisionAsync());
        Assert.Empty(await store.GetPackagesAsync());
        var publisher = new WorkflowPublisher(store, store);
        var published = await publisher.PublishAsync(draft.Id, "publisher");
        Assert.True(published.Success, string.Join("; ", published.Diagnostics));
        Assert.Equal(draft.Id, published.Receipt!.Draft);
        Assert.Equal(draft.Id, published.Package!.PublishedDraft);
        Assert.Equal("publisher", published.Package.PublishedBy);
        Assert.Equal(WorkflowPackageOrigin.Published, published.Package.Origin);
        Assert.Null(await store.GetDefinitionAsync("activity", draft.Name, 1));
        var detail = await store.GetPackageDetailAsync(draft.Name, "1.0.0", CatalogScope.Unrestricted);
        Assert.Equal(PackageExtraction.Indexed, detail!.Activities.Extraction);
        var activity = Assert.Single(detail.Activities.Activities);
        Assert.Null(activity.ActivityType);
        Assert.Equal("3", Assert.Single(activity.Inputs).DefaultJson);
        Assert.Equal(published.Receipt, (await publisher.PublishAsync(draft.Id, "retry")).Receipt);
        Assert.Equal(revision + 1, await store.GetCatalogRevisionAsync());
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task OccupiedUploadedIdentityIsPub022BeforeInvalidBodyIsCompiled(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var publisher = new WorkflowPublisher(store, store);

        var draft = await store.SaveDefinitionAsync(
            new("conformance.published", 1, "activity",
                Source.Replace("throw new Exception(\"Publication must not execute the body\");", "invalid C#")), null);

        await publisher.UploadPackageAsync(
            new MemoryStream(PublishingFeed.Package("conformance.published", new() { ["content.txt"] = [1] })));

        var revision = await store.GetCatalogRevisionAsync();
        var result = await publisher.PublishAsync(draft.Id);
        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("PUB022", diagnostic.Code);
        Assert.Equal(WorkflowPublishStage.Validate, diagnostic.Stage);
        Assert.Equal(WorkflowPublishDiagnosticClass.Conflict, diagnostic.Class);
        Assert.Equal(draft, await store.GetDefinitionAsync("activity", draft.Name, 1));
        Assert.Equal(revision, await store.GetCatalogRevisionAsync());
    }

    private sealed class ReadOnlyArtifacts(IArtifactStore inner) : IArtifactStore
    {
        public ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken token = default) =>
            throw new InvalidOperationException("Unexpected put or age refresh");

        public ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken token = default) => inner.OpenAsync(digest, token);
        public ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken token = default) => inner.ExistsAsync(digest, token);

        public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken token = default) =>
            throw new InvalidOperationException("Unexpected delete");

        public ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime before, int limit,
            CancellationToken token = default) => throw new InvalidOperationException("Unexpected purge");
    }
}