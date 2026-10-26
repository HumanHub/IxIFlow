using System.Text.Json;
using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Templates;
using IxIFlow.Templates.Liquid;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "authoring")]
[Trait("Spec", "F-19")]
[Trait("Spec", "H-03")]
public sealed class PublishingReviewFixScenarios
{
    public static IEnumerable<object[]> WithdrawalCases => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { "direct", "transitive", "copied" }.Select(use => new object[] { profile, use }));

    [Theory, MemberData(nameof(WithdrawalCases))]
    public async Task WithdrawalAfterPreparationRechecksAuthoringPinsSharedWithCallees(string profile, string use)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var publisher = new WorkflowPublisher(store, store);
        var (activityBytes, dependencyBytes) = PublishingFeed.Packages();
        var dependency = (await publisher.UploadPackageAsync(new MemoryStream(dependencyBytes))).Stored;
        var activity = (await publisher.UploadPackageAsync(new MemoryStream(activityBytes))).Stored;
        const string direct = """
              - kind: activity
                id: direct
                activity: fixture.echo
            """;
        var callee = await store.SaveDefinitionAsync(new("ReviewCallee", 1, "workflow", """
            workflow: {name: ReviewCallee, version: 1, dataType: System.Object}
            imports: {catalogs: [fixture.activities@1.0.0]}
            definitions:
            """ + "\n" + direct), null);
        var publishedCallee = await publisher.PublishAsync(callee.Id);
        Assert.True(publishedCallee.Success, string.Join("; ", publishedCallee.Diagnostics));
        var source = """
            workflow: {name: ReviewCaller, version: 1, dataType: System.Object}
            imports:
              workflows: [ReviewCallee@1]
            """ + (use == "copied" ? "" : "\n  catalogs: [fixture.activities@1.0.0]") + "\ndefinitions:\n" +
            (use == "copied" ? "" : direct + "\n") + """
              - kind: invokeWorkflow
                id: child
                workflow: {name: ReviewCallee, version: 1}
            """;
        var caller = await store.SaveDefinitionAsync(new("ReviewCaller", 1, "workflow", source), null);
        var withdrawn = use == "transitive" ? dependency : activity;
        var artifacts = new PublicationArtifacts(store) { BeforeBundle = async () =>
            await store.SetPackageWithdrawnAsync(withdrawn.Id, withdrawn.Version, true, new(withdrawn.LifecycleRevision)) };
        var result = await new WorkflowPublisher(store, artifacts).PublishAsync(caller.Id);
        Assert.True(artifacts.BundleWritten);
        Assert.True((await store.GetPackageAsync(withdrawn.Id, withdrawn.Version))!.Withdrawn);
        if (use == "copied")
        {
            Assert.True(result.Success, string.Join("; ", result.Diagnostics));
            Assert.Null(await store.GetDefinitionAsync("workflow", caller.Name, 1));
            var manifest = JsonSerializer.Deserialize<WorkflowBundleManifest>(result.Workflow!.ManifestJson!)!;
            Assert.Equal(new[] { activity.Digest, dependency.Digest }.OrderBy(pin => pin.Value),
                manifest.ActivityPackages.Select(pin => pin.Digest).OrderBy(pin => pin.Value));
            Assert.Equal(manifest.ActivityPackages.Select(pin => pin.Digest), result.Workflow.ArtifactReferences!);
            Assert.Empty(result.Workflow.AuthoringPackageReferences);
            var holders = await store.GetPackageHoldersAsync(withdrawn.Id, withdrawn.Version, CatalogScope.Unrestricted);
            Assert.Contains(holders.Workflows, row => row.Name == caller.Name);
        }
        else
        {
            Assert.False(result.Success, "New authoring cannot acquire a withdrawn pin just because a callee also holds it");
            Assert.Equal("PUB040", Assert.Single(result.Diagnostics).Code);
            Assert.Equal(WorkflowPublishDiagnosticClass.Conflict, result.FailureClass);
            Assert.Equal(caller.Id, (await store.GetDefinitionAsync("workflow", caller.Name, 1))!.Id);
            Assert.Null(await store.GetWorkflowVersionAsync(caller.Name, 1));
        }
        Assert.Equal(publishedCallee.Workflow, await store.GetWorkflowVersionAsync(callee.Name, 1));
    }

    [Theory]
    [InlineData("resolved-count")]
    [InlineData("scheduled-candidates")]
    [InlineData("catalog-versions")]
    [InlineData("feed-versions")]
    [InlineData("metadata-bytes")]
    [InlineData("feed-download-bytes")]
    [InlineData("expanded-bytes")]
    public async Task ResolverBudgetsHaveLimitDiagnosticsAndDurableFailureClass(string bound)
    {
        var store = new InMemoryWorkflowStateRepository();
        using var feed = new PublishingFeed();
        var upload = new WorkflowPublisher(store, store);
        var bytes = PublishingFeed.Package("review.one", new() { ["payload.txt"] = new byte[4096] });
        await upload.UploadPackageAsync(new MemoryStream(bytes));
        await upload.UploadPackageAsync(new MemoryStream(PublishingFeed.Package("review.two", new())));
        var imports = "review.one@1.0.0, review.two@1.0.0";
        WorkflowPublisherOptions options;
        switch (bound)
        {
            case "resolved-count": options = new() { MaxResolvedPackages = 1 }; break;
            case "scheduled-candidates": options = new() { MaxPackageCandidates = 1 }; break;
            case "catalog-versions":
            case "feed-versions":
                imports = "review.one";
                var other = PublishingFeed.Package("review.one", new(), version: "2.0.0");
                if (bound == "catalog-versions") await upload.UploadPackageAsync(new MemoryStream(other));
                else await File.WriteAllBytesAsync(Path.Combine(feed.Folder, "review.one.2.0.0.nupkg"), other);
                options = new() { MaxPackageCandidates = 1, NuGetConfigPath = feed.Config };
                break;
            case "metadata-bytes": options = new() { MaxPackageResolutionBytes = 1 }; break;
            case "feed-download-bytes":
                imports = "review.one@1.0.0";
                await store.DeleteAsync(ArtifactDigest.Of(bytes));
                await File.WriteAllBytesAsync(Path.Combine(feed.Folder, "review.one.1.0.0.nupkg"), bytes);
                options = new() { NuGetConfigPath = feed.Config, MaxPackageResolutionBytes = bytes.Length - 1 };
                break;
            default:
                imports = "review.one@1.0.0";
                options = new() { MaxPackageResolutionBytes = bytes.Length + 1 };
                break;
        }
        var draft = await store.SaveDefinitionAsync(new("Review", 1, "workflow", Yaml(imports)), null);
        await AssertFailure(store, new WorkflowPublisher(store, store, options), draft, "PUB042", WorkflowPublishDiagnosticClass.Limit);
    }

    [Theory]
    [InlineData("count")]
    [InlineData("bytes")]
    public async Task VerifiedCalleeClosureBudgetsAlsoUseLimitDiagnostics(string bound)
    {
        var store = new InMemoryWorkflowStateRepository();
        var publisher = new WorkflowPublisher(store, store);
        foreach (var id in new[] { "review.one", "review.two" })
            await publisher.UploadPackageAsync(new MemoryStream(PublishingFeed.Package(id, new())));
        var source = TypedPublishingScenarios.Yaml("ReviewCallee") + "\nimports: {catalogs: [review.one@1.0.0, review.two@1.0.0]}\n";
        var callee = await store.SaveDefinitionAsync(new("ReviewCallee", 1, "workflow", source), null);
        var published = await publisher.PublishAsync(callee.Id);
        Assert.True(published.Success, string.Join("; ", published.Diagnostics));
        var first = JsonSerializer.Deserialize<WorkflowBundleManifest>(published.Workflow!.ManifestJson!)!.ActivityPackages[0];
        await using var archive = (await store.OpenAsync(first.Digest))!;
        using var buffer = new MemoryStream(); await archive.CopyToAsync(buffer);
        var entries = await WorkflowPackageArchive.ReadAsync(new MemoryStream(buffer.ToArray()), first.Digest, new());
        var options = bound == "count" ? new WorkflowPublisherOptions { MaxResolvedPackages = 1 } :
            new WorkflowPublisherOptions { MaxPackageResolutionBytes = buffer.Length + entries.Values.Sum(entry => (long)entry.Length) - 1 };
        var caller = await store.SaveDefinitionAsync(new("Review", 1, "workflow", """
            workflow: {name: Review, version: 1, dataType: System.Object}
            imports: {workflows: [ReviewCallee@1]}
            definitions:
              - kind: invokeWorkflow
                id: child
                workflow: {name: ReviewCallee, version: 1}
            """), null);
        await AssertFailure(store, new WorkflowPublisher(store, store, options), caller, "PUB042", WorkflowPublishDiagnosticClass.Limit);
    }

    [Theory]
    [InlineData("refetched-digest")]
    [InlineData("stored-digest")]
    [InlineData("entries")]
    [InlineData("missing-content")]
    public async Task ImmutablePackageFailuresHaveUnavailableDiagnosticsAndNeverReplaceBytes(string failure)
    {
        var store = new InMemoryWorkflowStateRepository();
        using var feed = new PublishingFeed();
        var bytes = PublishingFeed.Package("review.immutable", new());
        var changed = PublishingFeed.Package("review.immutable", new() { ["changed.txt"] = [1] });
        var publisher = new WorkflowPublisher(store, store);
        var candidate = await publisher.ReadPackageAsync(new MemoryStream(bytes));
        await store.PutAsync(ArtifactKind.Package, new MemoryStream(bytes));
        var package = candidate.Package;
        if (failure == "entries") package = package with { Entries = new Dictionary<string, ArtifactDigest>
            { ["review.immutable.nuspec"] = ArtifactDigest.Of([99]) } };
        var registered = (await store.RegisterPackageAsync(package, new([], PackageExtraction.Indexed, null, [], []), store)).Stored;
        var artifacts = new PublicationArtifacts(store);
        if (failure == "refetched-digest")
        {
            await store.DeleteAsync(package.Digest);
            await File.WriteAllBytesAsync(Path.Combine(feed.Folder, "review.immutable.1.0.0.nupkg"), changed);
        }
        else if (failure == "stored-digest") artifacts.Replace = (package.Digest, changed);
        else if (failure == "missing-content") artifacts.Replace = (package.Digest, null);
        var draft = await store.SaveDefinitionAsync(new("Review", 1, "workflow", Yaml("review.immutable@1.0.0")), null);
        await AssertFailure(store, new WorkflowPublisher(store, artifacts, new() { NuGetConfigPath = feed.Config }), draft,
            "PUB041", WorkflowPublishDiagnosticClass.Unavailable);
        Assert.Equal(JsonSerializer.Serialize(registered), JsonSerializer.Serialize(await store.GetPackageAsync(package.Id, package.Version)));
        Assert.False(artifacts.BundleWritten);
        await using var original = await store.OpenAsync(package.Digest);
        if (failure == "refetched-digest") Assert.Null(original);
        else
        {
            Assert.NotNull(original);
            using var copy = new MemoryStream(); await original.CopyToAsync(copy);
            Assert.Equal(bytes, copy.ToArray());
        }
    }

    private static string Yaml(string imports) =>
        "workflow: {name: Review, version: 1, dataType: System.Object}\nimports: {catalogs: [" + imports + "]}\ndefinitions: []\n";

    private static async Task AssertFailure(IWorkflowStateRepository store, WorkflowPublisher publisher, WorkflowDraft draft,
        string code, WorkflowPublishDiagnosticClass failureClass)
    {
        var diagnostic = Assert.Single((await publisher.ValidateAsync(draft)).Diagnostics);
        Assert.Equal(code, diagnostic.Code); Assert.Equal(failureClass, diagnostic.Class);
        Assert.DoesNotContain("review.", diagnostic.Message);
        using var service = new PublicationService(store, publisher);
        var attempt = await service.PublishAsync(draft.Id, TimeSpan.FromSeconds(15));
        Assert.Equal(WorkflowPublicationState.Failed, attempt.State);
        var saved = (await service.GetPublicationAsync(attempt.Id, CatalogScope.Unrestricted))!;
        Assert.Equal(failureClass, saved.Outcome!.FailureClass);
        Assert.Equal(code, Assert.Single(saved.Outcome.Diagnostics).Code);
        Assert.Equal(draft.Id, (await store.GetDefinitionAsync(draft.Kind, draft.Name, draft.Version))!.Id);
        Assert.Null(await store.GetWorkflowVersionAsync(draft.Name, draft.Version));
        await service.StopAsync(default);
    }

    public sealed class LoopModel
    {
        public int[] Items { get; set; } = [];
        public Empty Bounds { get; set; } = new();
    }
    public sealed class Empty;

    [Theory]
    [InlineData("for", "offset: MissingOffset limit: MissingLimit")]
    [InlineData("for", "limit: MissingLimit offset: MissingOffset")]
    [InlineData("tablerow", "cols: MissingCols offset: MissingOffset limit: MissingLimit")]
    [InlineData("tablerow", "offset: MissingOffset limit: MissingLimit cols: MissingCols")]
    [InlineData("for", "offset: Bounds.MissingOffset limit: Bounds.MissingLimit")]
    [InlineData("tablerow", "cols: Bounds.MissingCols offset: Bounds.MissingOffset limit: Bounds.MissingLimit")]
    public async Task LoopParameterExpressionsKeepTheirOwnSourceOccurrences(string tag, string parameters)
    {
        // Repeated and nested loops must bind to their own header, including repeated root identifiers.
        var header = "{% " + tag + " item in Items " + parameters + " %}";
        var source = header + "\n" + header + "{{ item }}{% end" + tag + " %}\n{% end" + tag + " %}\n" + header + "{% end" + tag + " %}";
        var diagnostics = await new LiquidTemplateEngine().ValidateAsync(new("review", 1, "", TemplateFormat.Text, source), typeof(LoopModel));
        var names = parameters.Contains("MissingCols", StringComparison.Ordinal) ? new[] { "MissingOffset", "MissingLimit", "MissingCols" } : ["MissingOffset", "MissingLimit"];
        Assert.Equal(names.Length * 3, diagnostics.Count);
        foreach (var name in names)
        {
            var matches = diagnostics.Where(item => item.Message.Contains(name, StringComparison.Ordinal)).ToArray();
            Assert.Equal(new int?[] { 1, 2, 4 }, matches.Select(item => item.Line));
            Assert.All(matches, item => Assert.Equal(header.IndexOf(name, StringComparison.Ordinal) + 1, item.Column));
        }
    }

    [Theory]
    [InlineData("for", "offset: MissingOffset limit: MissingLimit")]
    [InlineData("tablerow", "cols: MissingCols offset: MissingOffset limit: MissingLimit")]
    public async Task ReorderedLoopDiagnosticsMapIntoLiteralYamlContent(string tag, string parameters)
    {
        var store = new InMemoryWorkflowStateRepository();
        using var feed = new PublishingFeed();
        var model = PublishingFeed.Emit("Review.Models", "namespace Review; public class Model { public int[] Items {get;set;} = []; }");
        await File.WriteAllBytesAsync(Path.Combine(feed.Folder, "review.models.1.0.0.nupkg"),
            PublishingFeed.Package("review.models", new() { ["lib/net10.0/Review.Models.dll"] = model }));
        var header = "{% " + tag + " item in Items " + parameters + " %}{{ item }}{% end" + tag + " %}";
        var yaml = "template: {key: review, version: 1, engine: liquid, format: text, model: Review.Model}\n" +
            "imports: {catalogs: [review.models@1.0.0]}\ncontent: |\n  first line\n  " + header + "\n";
        var publisher = new WorkflowPublisher(store, store, new() { NuGetConfigPath = feed.Config },
            templateEngines: new([new LiquidTemplateEngine()]));
        var diagnostics = (await publisher.ValidateAsync(new("review", 1, "template", yaml))).Diagnostics;
        Assert.Equal(tag == "for" ? 2 : 3, diagnostics.Count);
        foreach (var diagnostic in diagnostics)
        {
            Assert.Equal("TPL006", diagnostic.Code); Assert.Equal(5, diagnostic.Line);
            var name = new[] { "MissingOffset", "MissingLimit", "MissingCols" }.Single(name => diagnostic.Message.Contains(name, StringComparison.Ordinal));
            Assert.Equal(header.IndexOf(name, StringComparison.Ordinal) + 3, diagnostic.Column);
        }
    }

    private sealed class PublicationArtifacts(IArtifactStore inner) : IArtifactStore
    {
        internal Func<Task>? BeforeBundle;
        internal bool BundleWritten;
        internal (ArtifactDigest Digest, byte[]? Bytes)? Replace;
        public async ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken token = default)
        {
            if (kind == ArtifactKind.Bundle)
            {
                if (BeforeBundle != null) await BeforeBundle();
                BundleWritten = true;
            }
            return await inner.PutAsync(kind, content, token);
        }
        public ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken token = default) =>
            Replace is { } replacement && digest == replacement.Digest
                ? ValueTask.FromResult<Stream?>(replacement.Bytes == null ? null : new MemoryStream(replacement.Bytes))
                : inner.OpenAsync(digest, token);
        public ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken token = default) => inner.ExistsAsync(digest, token);
        public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken token = default) => inner.DeleteAsync(digest, token);
        public ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime before, int limit, CancellationToken token = default) =>
            inner.PurgeUnreferencedAsync(references, before, limit, token);
    }
}
