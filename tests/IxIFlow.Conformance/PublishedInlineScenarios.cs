using IxIFlow.Core;
using IxIFlow.Authoring;
using IxIFlow.Core.Runtime;
using System.Text;
using System.Text.Json;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using static IxIFlow.Tests.Infrastructure.PublishedInlineScenario;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "registration")]
[Trait("Spec", "F-17")]
[Trait("Spec", "F-18")]
[Trait("Spec", "F-10")]
public sealed class PublishedInlineScenarios
{
    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task BothDependencyKindsSurviveReopenAndChildStillBlocksRetirement(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var leaf = await Publish(store, "Leaf", Empty("Leaf"));
        var yaml = Caller("Both", "Leaf") + "  - kind: invokeWorkflow\n    id: child\n    workflow: {name: Leaf, version: 1}\n";
        var caller = await Publish(store, "Both", yaml);
        Assert.Equal(new[] { WorkflowDependencyKind.Child, WorkflowDependencyKind.Inline }, caller.Dependencies!.Select(edge => edge.Kind));

        await store.SeedAsync(new()
        {
            InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = caller.Name, WorkflowVersion = 1,
            Status = WorkflowStatus.Suspended, ExecutionStateJson = StoreTestSupport.CheckpointJson,
            WorkflowDataJson = StoredJson.Inline("{}"),
            WorkflowDataType = WorkflowTypeIdentity.SavedName(typeof(IxIFlow.BundleFixtures.FirstData)),
            BundleDigest = caller.ArtifactDigest
        });

        await Retire(store, caller);

        if (store is IAsyncDisposable disposable)
            await disposable.DisposeAsync();

        var reopened = database.NewRepository();
        Assert.Equal(caller.Dependencies, (await reopened.GetWorkflowVersionAsync(caller.Name, 1))!.Dependencies);
        Assert.NotNull((await reopened.GetRetirementBlockersAsync("Leaf", 1, CatalogScope.Unrestricted)).UnfinishedCaller);
        Assert.Contains("unfinished parent", (await Assert.ThrowsAsync<WorkflowEngineException>(() => Retire(reopened, leaf))).Message);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task LatestPublishedAndDeprecatedPinsUseTheSameCatalogRules(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await Publish(store, "Leaf", Empty("Leaf"));
        var newer = await Publish(store, "Leaf", Empty("Leaf", 2), 2);
        await store.SetWorkflowVersionStateAsync("Leaf", 2, WorkflowVersionState.Deprecated, new(newer.LifecycleRevision));
        var latest = await Publish(store, "Latest", Caller("Latest", "Leaf"));
        var pinned = await Publish(store, "Pinned", Caller("Pinned", "Leaf", "2"));
        Assert.Equal(1, Assert.Single((await Read(store, latest)).Manifest.InlineWorkflows).Version);
        Assert.Equal(2, Assert.Single((await Read(store, pinned)).Manifest.InlineWorkflows).Version);
    }

    public static IEnumerable<object[]> RefusalProfiles => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { "framework", "source", "private", "closure" }.Select(mutation => new object[] { profile, mutation }));

    [Theory]
    [MemberData(nameof(RefusalProfiles))]
    public async Task CalleeVerificationRefusalsPreserveTheDraftOnEveryStore(string profile, string mutation)
    {
        var source = new InMemoryWorkflowStateRepository();
        var original = await Publish(source, "Leaf", Empty("Leaf"));
        var bundle = await Read(source, original);
        var entries = bundle.Entries.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());

        if (mutation == "source")
            entries[WorkflowBundleArchive.YamlPath] = Encoding.UTF8.GetBytes(Empty("Leaf") + "# not canonical\n");

        if (mutation == "private")
            entries["private/System.Payments.dll"] = ConflictingPrivateAssembly();

        if (mutation == "closure")
            entries.Remove("private/System.Payments.dll");

        var manifest = mutation == "framework" ? bundle.Manifest with { TargetFramework = "net99.0" } : bundle.Manifest;
        var bytes = WorkflowBundleArchive.Create(manifest, entries);
        var digest = ArtifactDigest.Of(bytes);
        var verified = await WorkflowBundleArchive.ReadAsync(new MemoryStream(bytes), digest);
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await store.PutAsync(ArtifactKind.Bundle, new MemoryStream(bytes));

        await store.RegisterWorkflowVersionAsync(original with
        {
            ArtifactDigest = digest, PublishedDraft = null, ManifestJson = JsonSerializer.Serialize(verified.Manifest)
        });

        var draft = await store.SaveDefinitionAsync(new("Caller", 1, "workflow", Caller("Caller", "Leaf")), null);
        var result = await new WorkflowPublisher(store, store, Options()).PublishAsync(draft.Id);
        Assert.Equal(mutation == "source" ? "PUB013" : mutation == "private" ? "PUB010" : "PUB009", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(draft, await store.GetDefinitionAsync("workflow", "Caller", 1));
        Assert.Null(await store.GetWorkflowVersionAsync("Caller", 1));
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task InlinePackagePinsCannotConflictWithTheAuthorsClosure(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var publisher = new WorkflowPublisher(store, store, Options());

        foreach (var version in new[] { 1, 2 })
        {
            var packages = ActivityPackageFixture.Create(version);
            await publisher.UploadPackageAsync(new MemoryStream(packages.Dependency));
            await publisher.UploadPackageAsync(new MemoryStream(packages.Activity));
        }

        await Publish(store, "Leaf", """
                                     workflow: {name: Leaf, dataType: Packaged.Data, input: [Value], output: [Value]}
                                     imports: {catalogs: [fixture.activities@1.0.0]}
                                     definitions: []
                                     """);

        var draft = await store.SaveDefinitionAsync(new("Caller", 1, "workflow", Caller("Caller", "Leaf") +
            "imports: {catalogs: [fixture.activities@2.0.0]}\n"), null);

        var result = await publisher.PublishAsync(draft.Id);
        Assert.Equal("PUB010", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(draft, await store.GetDefinitionAsync("workflow", "Caller", 1));
        Assert.Null(await store.GetWorkflowVersionAsync("Caller", 1));
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task InlineCalleePackageClosureIsCopiedAfterWithdrawal(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var cache = Path.Combine(Path.GetTempPath(), "IxIFlow.InlinePackageConformance", Guid.NewGuid().ToString("N"));

        try
        {
            await PackageClosure(database.NewRepository(), cache, database.ConfigureHost, database.StartHostLinksAsync);
        }
        finally
        {
            if (Directory.Exists(cache))
                Directory.Delete(cache, true);
        }
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task NestedPublishedInlineClosuresColdLoadOnEveryStore(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var cache = Path.Combine(Path.GetTempPath(), "IxIFlow.InlineConformance", Guid.NewGuid().ToString("N"));

        try
        {
            await ColdLoad(database.NewRepository(), cache, database.ConfigureHost, database.StartHostLinksAsync);
        }
        finally
        {
            if (Directory.Exists(cache))
                Directory.Delete(cache, true);
        }
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task RetiredInlineCallerCanFinishAfterCalleeRetiresAndItsNestedChildStaysProtected(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var publishing = database.NewRepository();
        var child = await Publish(publishing, "Child", Empty("Child"));

        var callee = await Publish(publishing, "Body", """
                                                       workflow: {name: Body, version: 1, dataType: Data, input: [Value], output: [Value]}
                                                       definitions:
                                                         - kind: suspend
                                                           id: wait
                                                           event: Signal
                                                           reason: continue
                                                         - kind: invokeWorkflow
                                                           id: nestedChild
                                                           workflow: {name: Child, version: 1}
                                                           input: [{target: Value, from: {path: workflow.Value}}]
                                                           output: [{source: Value, to: {path: workflow.Value}}]
                                                       """);

        var caller = await Publish(publishing, "Parent", Caller("Parent", "Body"));
        Assert.Contains(new("Body", 1, WorkflowDependencyKind.Inline), caller.Dependencies!);
        Assert.Contains(new("Child", 1, WorkflowDependencyKind.Child), caller.Dependencies!);
        var cache = Path.Combine(Path.GetTempPath(), "IxIFlow.InlineRetirement", Guid.NewGuid().ToString("N"));
        string id;

        try
        {
            await using (var host = PublishedBundleScenario.Host(publishing, Path.Combine(cache, "first"),
                             configure: database.ConfigureHost))
            {
                await database.StartHostLinksAsync(host);

                id = await host.GetRequiredService<IWorkflowClient>()
                    .StartAsync("Parent", 1, new IxIFlow.BundleFixtures.FirstData { Value = "retained" });

                await PublishedBundleScenario.UntilAsync(publishing, id, WorkflowStatus.Suspended);
            }

            foreach (var state in new[] { WorkflowVersionState.Published, WorkflowVersionState.Deprecated })
            {
                var current = (await publishing.GetWorkflowVersionAsync("Parent", 1))!;

                if (current.State != state)
                    caller = await publishing.SetWorkflowVersionStateAsync("Parent", 1, state, new(current.LifecycleRevision));

                var blockers = await publishing.GetRetirementBlockersAsync("Body", 1, CatalogScope.Unrestricted);
                Assert.NotEmpty(blockers.Callers);
                await Assert.ThrowsAsync<WorkflowEngineException>(() => Retire(publishing, callee));
            }

            await Retire(publishing, caller);
            await Retire(publishing, callee);

            if (publishing is IAsyncDisposable disposable)
                await disposable.DisposeAsync();

            var cold = database.NewRepository();

            Assert.Equal(WorkflowDependencyKind.Inline,
                (await cold.GetWorkflowVersionAsync("Parent", 1))!.Dependencies!.Single(edge => edge.Name == "Body").Kind);

            var inlineBlockers = await cold.GetRetirementBlockersAsync("Body", 1, CatalogScope.Unrestricted);
            Assert.Null(inlineBlockers.UnfinishedCaller);
            var childBlockers = await cold.GetRetirementBlockersAsync("Child", 1, CatalogScope.Unrestricted);
            Assert.NotNull(childBlockers.UnfinishedCaller);
            var refused = await Assert.ThrowsAsync<WorkflowEngineException>(() => Retire(cold, child));
            Assert.Contains("unfinished parent", refused.Message);
            var garbage = await cold.PutAsync(ArtifactKind.Bundle, new MemoryStream([1, 2, 3]));
            await cold.PurgeArtifactsAsync(cold, TimeSpan.FromTicks(1), 100);
            Assert.False(await cold.ExistsAsync(garbage));
            Assert.True(await cold.ExistsAsync(caller.ArtifactDigest!.Value));

            await using (var host = PublishedBundleScenario.Host(cold, Path.Combine(cache, "second"), configure: database.ConfigureHost))
            {
                await database.StartHostLinksAsync(host);
                var client = host.GetRequiredService<IWorkflowClient>();
                var result = await client.ResumeAsync(id, new IxIFlow.BundleFixtures.Signal());

                if (result.Status is WorkflowExecutionStatus.Suspended or WorkflowExecutionStatus.Running)
                    result = await client.AwaitAsync(id, WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(30));

                Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
                Assert.Equal("retained", result.WorkflowData!.GetType().GetProperty("Value")!.GetValue(result.WorkflowData));
            }

            await Retire(cold, child);
        }
        finally
        {
            if (Directory.Exists(cache))
                Directory.Delete(cache, true);
        }
    }
}