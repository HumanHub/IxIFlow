using System.Reflection;
using System.Text.Json;
using Dapper;
using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "authoring")]
[Trait("Spec", "F-19")]
[Trait("Spec", "H-03")]
public sealed class PublishingReferenceRegressionScenarios
{
    public static IEnumerable<object[]> WithdrawalCases => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { "direct", "transitive", "copied" }.Select(use => new object[] { profile, use }));

    public static IEnumerable<object[]> RetentionCases => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { "references", "orphan" }.Select(root => new object[] { profile, root }));

    [Theory, MemberData(nameof(WithdrawalCases))]
    public async Task WithdrawalChecksAuthoringPinsWhilePublishedCallersRetainTheFullUnion(string profile, string use)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        using var feed = await Feed();
        var artifacts = new FileSystemArtifactStore(Path.Combine(feed.Folder, "artifacts"));
        store.CatalogArtifacts = artifacts;
        var publisher = new WorkflowPublisher(store, artifacts, new() { NuGetConfigPath = feed.Config });
        var callee = await Publish(store, publisher, "ReferenceCallee", CalleeSource);
        var caller = await Publish(store, publisher, "ReferenceCaller", CallerSource("ReferenceCaller", use));
        var pins = Pins(callee);
        Assert.Equal(3, pins.Length);
        Assert.Equal(pins, caller.ArtifactReferences!);
        Assert.Equal(pins, Pins(caller));
        Assert.Equal(use == "copied" ? [] : JsonSerializer.Deserialize<WorkflowBundleManifest>(callee.ManifestJson!)!.ActivityPackages
            .Where(pin => pin.Id != "review.copied").Select(pin => pin.Digest).ToArray(), caller.AuthoringPackageReferences);
        if (store is IAsyncDisposable disposable) await disposable.DisposeAsync();
        store = database.NewRepository();
        store.CatalogArtifacts = artifacts;
        Assert.Equal(caller, await store.GetWorkflowVersionAsync(caller.Name, caller.Version));
        if (use != "copied")
            await Assert.ThrowsAsync<WorkflowEngineException>(() => store.RegisterWorkflowVersionAsync(caller with { AuthoringPackageReferences = [] }, artifacts));

        var draft = await store.SaveDefinitionAsync(new("WithdrawalCaller", 1, "workflow", CallerSource("WithdrawalCaller", use)), null);
        var packages = await store.GetPackagesAsync();
        Assert.All(packages, package => Assert.Equal(WorkflowPackageOrigin.Resolved, package.Origin));
        var withdrawn = packages.Single(package => package.Id == (use == "transitive" ? "fixture.dependency" : "fixture.activities"));
        var probe = new WithdrawalArtifacts(artifacts, async () =>
            await store.SetPackageWithdrawnAsync(withdrawn.Id, withdrawn.Version, true, new(withdrawn.LifecycleRevision)));
        var result = await new WorkflowPublisher(store, probe, new() { NuGetConfigPath = feed.Config }).PublishAsync(draft.Id);
        Assert.True(probe.BundleWritten);
        Assert.True((await store.GetPackageAsync(withdrawn.Id, withdrawn.Version))!.Withdrawn);
        if (use == "copied")
        {
            Assert.True(result.Success, string.Join("; ", result.Diagnostics));
            Assert.Equal(pins, result.Workflow!.ArtifactReferences!);
            Assert.Equal(pins, Pins(result.Workflow));
            Assert.Null(await store.GetDefinitionAsync("workflow", draft.Name, 1));
        }
        else
        {
            Assert.False(result.Success);
            Assert.Equal("PUB040", Assert.Single(result.Diagnostics).Code);
            Assert.Equal(WorkflowPublishDiagnosticClass.Conflict, result.FailureClass);
            Assert.Equal(draft, await store.GetDefinitionAsync("workflow", draft.Name, 1));
            Assert.Null(await store.GetWorkflowVersionAsync(draft.Name, 1));
        }
        foreach (var package in packages)
        {
            var holders = await store.GetPackageHoldersAsync(package.Id, package.Version, CatalogScope.Unrestricted);
            Assert.Contains(new WorkflowVersionReference(caller.Name, 1), holders.Workflows);
            if (use == "copied") Assert.Contains(new WorkflowVersionReference(draft.Name, 1), holders.Workflows);
        }
        var abandoned = await artifacts.PutAsync(ArtifactKind.Bundle, new MemoryStream([99, 48, 17]));
        Age(feed);
        Assert.Equal(use == "copied" ? 1 : 2, await store.PurgeArtifactsAsync(artifacts, TimeSpan.FromDays(90), 100));
        Assert.False(await artifacts.ExistsAsync(abandoned));
        foreach (var pin in pins) Assert.True(await artifacts.ExistsAsync(pin));
        Assert.True(await artifacts.ExistsAsync(caller.ArtifactDigest!.Value));
        Assert.Equal(callee, await store.GetWorkflowVersionAsync(callee.Name, 1));
    }

    [Theory, MemberData(nameof(RetentionCases))]
    public async Task CopiedPinsRemainRootsThroughReferenceOnlyRowsAndOrphanInstances(string profile, string root)
    {
        await using var sourceDatabase = await StoreProfiles.CreateAsync(profile);
        var source = sourceDatabase.NewRepository();
        using var feed = await Feed();
        var artifacts = new FileSystemArtifactStore(Path.Combine(feed.Folder, "artifacts"));
        var publisher = new WorkflowPublisher(source, artifacts, new() { NuGetConfigPath = feed.Config });
        var callee = await Publish(source, publisher, "ReferenceCallee", CalleeSource);
        var caller = await Publish(source, publisher, "ReferenceCaller", CallerSource("ReferenceCaller", "copied"));
        var pins = Pins(callee);
        Assert.Equal(pins, caller.ArtifactReferences!);

        // Isolate the caller's retention root from the original callee and its manifest.
        await using var targetDatabase = await StoreProfiles.CreateAsync(profile);
        var target = targetDatabase.NewRepository();
        target.CatalogArtifacts = artifacts;
        foreach (var package in (await source.GetPackagesAsync()).OrderBy(package => package.DependencyGroups.Sum(group => group.Dependencies.Count)))
        {
            var detail = (await source.GetPackageDetailAsync(package.Id, package.Version, CatalogScope.Unrestricted))!;
            await target.RegisterPackageAsync(package, detail.Activities, artifacts);
        }
        var unheldBytes = PublishingFeed.Package("review.unheld", new());
        var unheldDigest = await artifacts.PutAsync(ArtifactKind.Package, new MemoryStream(unheldBytes));
        var candidate = await new PackageIngestion(target, artifacts).ReadPackageAsync(new MemoryStream(unheldBytes));
        await target.RegisterPackageAsync(candidate.Package with { Origin = WorkflowPackageOrigin.Resolved }, new([], PackageExtraction.Indexed, null, [], []), artifacts);
        if (root == "references")
            await target.RegisterWorkflowVersionAsync(caller with { ManifestJson = null }, artifacts);
        else
        {
            await target.SeedAsync(new WorkflowInstance
            {
                InstanceId = "orphan-caller", WorkflowName = caller.Name, WorkflowVersion = 1,
                Status = WorkflowStatus.Completed,
                WorkflowDataJson = StoredJson.Inline("{}"), ExecutionStateJson = StoreTestSupport.CheckpointJson
            });
            // Model a retained instance whose catalog metadata has been lost.
            if (target is InMemoryWorkflowStateRepository memory)
            {
                var instances = (IDictionary<string, WorkflowInstance>)typeof(InMemoryWorkflowStateRepository)
                    .GetField("_instances", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(memory)!;
                instances["orphan-caller"].BundleDigest = caller.ArtifactDigest;
            }
            else
            {
                await using var connection = targetDatabase.NewConnection();
                Assert.Equal(1, await connection.ExecuteAsync(targetDatabase.Profile == StoreProfiles.DistributedSqlServer
                    ? "UPDATE dbo.IxIFlowWorkflowInstances SET BundleDigest=@Digest WHERE InstanceId=@Id"
                    : "UPDATE ixiflow_workflow_instances SET bundle_digest=@Digest WHERE instance_id=@Id",
                    new { Digest = caller.ArtifactDigest!.Value.Value, Id = "orphan-caller" }));
            }
        }
        foreach (var package in await target.GetPackagesAsync())
            await target.SetPackageWithdrawnAsync(package.Id, package.Version, true, new(package.LifecycleRevision));
        foreach (var package in (await target.GetPackagesAsync()).Where(package => pins.Contains(package.Digest)))
        {
            var holders = await target.GetPackageHoldersAsync(package.Id, package.Version, CatalogScope.Unrestricted);
            Assert.False(holders.UnreadableInstancePins);
            Assert.Equal(root == "orphan", holders.InstancePins);
            if (root == "references") Assert.Equal(new WorkflowVersionReference(caller.Name, 1), Assert.Single(holders.Workflows));
            else Assert.Empty(holders.Workflows);
        }
        Age(feed);
        Assert.Equal(2, await target.PurgeArtifactsAsync(artifacts, TimeSpan.FromDays(90), 100));
        Assert.False(await artifacts.ExistsAsync(unheldDigest));
        Assert.False(await artifacts.ExistsAsync(callee.ArtifactDigest!.Value));
        Assert.True(await artifacts.ExistsAsync(caller.ArtifactDigest!.Value));
        foreach (var pin in pins) Assert.True(await artifacts.ExistsAsync(pin));
    }

    private static async Task<PublishingFeed> Feed()
    {
        var feed = new PublishingFeed();
        var (activity, dependency) = PublishingFeed.Packages();
        await File.WriteAllBytesAsync(Path.Combine(feed.Folder, "fixture.activities.1.0.0.nupkg"), activity);
        await File.WriteAllBytesAsync(Path.Combine(feed.Folder, "fixture.dependency.1.0.0.nupkg"), dependency);
        await File.WriteAllBytesAsync(Path.Combine(feed.Folder, "review.copied.1.0.0.nupkg"), PublishingFeed.Package("review.copied", new()));
        return feed;
    }

    private static async Task<WorkflowVersion> Publish(IWorkflowCatalogStore store, WorkflowPublisher publisher, string name, string source)
    {
        var draft = await store.SaveDefinitionAsync(new(name, 1, "workflow", source), null);
        var result = await publisher.PublishAsync(draft.Id);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        return result.Workflow!;
    }

    private static ArtifactDigest[] Pins(WorkflowVersion version) =>
        JsonSerializer.Deserialize<WorkflowBundleManifest>(version.ManifestJson!)!.ActivityPackages.Select(pin => pin.Digest).ToArray();

    private static void Age(PublishingFeed feed)
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(feed.Folder, "artifacts"), "*.artifact"))
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-100));
    }

    private const string CalleeSource = """
        workflow: {name: ReferenceCallee, version: 1, dataType: System.Object}
        imports: {catalogs: [fixture.activities@1.0.0, review.copied@1.0.0]}
        definitions:
          - kind: activity
            id: echo
            activity: fixture.echo
        """;

    private static string CallerSource(string name, string use) => $$"""
        workflow: {name: {{name}}, version: 1, dataType: System.Object}
        imports:
          workflows: [ReferenceCallee@1]
        """ + (use == "copied" ? "" : "\n  catalogs: [fixture.activities@1.0.0]") + "\n" + """
        definitions:
          - kind: invokeWorkflow
            id: child
            workflow: {name: ReferenceCallee, version: 1}
        """ + (use == "copied" ? "" : "\n  - kind: activity\n    id: direct\n    activity: fixture.echo\n");

    private sealed class WithdrawalArtifacts(IArtifactStore inner, Func<Task> withdraw) : IArtifactStore
    {
        internal bool BundleWritten;
        public async ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken token = default)
        {
            if (kind == ArtifactKind.Bundle) { await withdraw(); BundleWritten = true; }
            return await inner.PutAsync(kind, content, token);
        }
        public ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken token = default) => inner.OpenAsync(digest, token);
        public ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken token = default) => inner.ExistsAsync(digest, token);
        public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken token = default) => inner.DeleteAsync(digest, token);
        public ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime before, int limit, CancellationToken token = default) =>
            inner.PurgeUnreferencedAsync(references, before, limit, token);
    }
}
