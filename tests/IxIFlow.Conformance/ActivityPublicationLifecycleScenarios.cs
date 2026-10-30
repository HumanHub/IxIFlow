using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Dapper;
using IxIFlow.ActivitySdk;
using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Templates;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "authoring")]
[Trait("Spec", "F-16")]
[Trait("Spec", "F-19")]
[Trait("Spec", "F-14")]
public sealed class ActivityPublicationLifecycleScenarios
{
    private static readonly PackageActivities Empty = new([], PackageExtraction.Indexed, null, [], []);

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task AtomicPublicationReturnsTheFirstFullReceiptAndPreservesAuditAndOrigin(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var first = await store.SaveDefinitionAsync(new("atomic.echo", 1, "activity", "first"), null, "author");
        var draft = await store.SaveDefinitionAsync(first with { Source = "edited" }, first.Id, "editor");
        var (package, activities) = await Publication(store, draft);

        await Assert.ThrowsAsync<CatalogPreconditionException>(() => store.PublishPackageDefinitionAsync(first.Id,
            package with { PublishedDraft = first.Id }, activities, store));

        var competingDigest = await store.PutAsync(ArtifactKind.Package, new MemoryStream([37]));
        var before = await store.GetCatalogRevisionAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = new[] { "winner-a", "winner-b" }.Select(actor => Task.Run(async () =>
        {
            await start.Task;

            return await store.PublishPackageDefinitionAsync(draft.Id,
                actor == "winner-a" ? package : package with { Digest = competingDigest },
                activities with { Activities = [activities.Activities[0] with { Name = actor }] }, store, actor);
        })).ToArray();

        start.SetResult();
        var receipts = await Task.WhenAll(tasks);
        Assert.Single(receipts, item => item.Created);
        Assert.Equal(JsonSerializer.Serialize(receipts[0].Stored), JsonSerializer.Serialize(receipts[1].Stored));
        var saved = receipts[0].Stored;
        Assert.Contains(saved.PublishedBy, new[] { "winner-a", "winner-b" });
        Assert.Equal(saved.PublishedBy, saved.RegisteredBy);
        Assert.NotNull(saved.PublishedAtUtc);
        Assert.Equal(saved.RegisteredAtUtc, saved.PublishedAtUtc);
        Assert.Equal(draft.Id, saved.PublishedDraft);
        Assert.Equal(WorkflowPackageOrigin.Published, saved.Origin);
        Assert.Equal(before + 1, saved.LifecycleRevision);
        Assert.Equal(before + 1, await store.GetCatalogRevisionAsync());
        Assert.Null(await store.GetDefinitionAsync("activity", draft.Name, 1));
        var activity = Assert.Single((await store.ListActivitiesAsync(new(CatalogScope.Unrestricted))).Items);
        Assert.Equal(ActivityOrigin.Published, activity.Provider.Origin);
        Assert.Equal(saved.PublishedBy, activity.Descriptor.Name);
        Assert.Null(activity.Descriptor.ActivityType);

        // A losing build may produce different bytes or lose its artifacts entirely.
        var losing = package with { Digest = ArtifactDigest.Of([99]) };

        var retry = await store.PublishPackageDefinitionAsync(draft.Id, losing, activities, new Artifacts(store) { RefuseReads = true },
            "retry");

        Assert.False(retry.Created);
        Assert.Equal(JsonSerializer.Serialize(saved), JsonSerializer.Serialize(retry.Stored));
        Assert.Equal(before + 1, await store.GetCatalogRevisionAsync());

        var registered = await store.RegisterPackageAsync(saved with { Origin = WorkflowPackageOrigin.Resolved, PublishedDraft = null },
            receipts[0].Activities, store, "resolver");

        Assert.Equal(WorkflowPackageOrigin.Published, registered.Stored.Origin);
        Assert.Equal(saved.PublishedDraft, registered.Stored.PublishedDraft);
        Assert.Equal(saved.RegisteredBy, registered.Stored.RegisteredBy);
        Assert.Equal(before + 1, await store.GetCatalogRevisionAsync());

        foreach (var wrong in new[] { draft.Id with { Generation = Guid.NewGuid() }, first.Id })
            Assert.Equal(WorkflowErrorKind.InvalidState, (await Assert.ThrowsAsync<WorkflowEngineException>(() =>
                store.PublishPackageDefinitionAsync(wrong, package with { PublishedDraft = wrong }, activities, store))).Kind);

        var recovered = await new WorkflowPublisher(store, new Artifacts(store) { RefuseReads = true }).PublishAsync(draft.Id);
        Assert.True(recovered.Success);
        Assert.Equal(saved.PublishedDraft, recovered.Package!.PublishedDraft);
        using var service = new PublicationService(store, new WorkflowPublisher(store, new Artifacts(store) { RefuseReads = true }));
        var reconciled = await service.PublishAsync(draft.Id, TimeSpan.FromSeconds(5), "recover");
        Assert.Equal(WorkflowPublicationState.Published, reconciled.State);
        Assert.Equal(JsonSerializer.Serialize(saved), reconciled.Outcome!.Receipt!.ReceiptJson);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task OccupiedUploadsWrongKindsAndInvalidDescriptorsNeverConsumeDrafts(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var draft = await store.SaveDefinitionAsync(new("occupied.echo", 1, "activity", "source"), null);
        var (package, activities) = await Publication(store, draft);
        var before = await store.GetCatalogRevisionAsync();

        await Assert.ThrowsAsync<WorkflowEngineException>(() => store.PublishPackageDefinitionAsync(
            draft.Id with { Kind = "workflow" }, package, activities, store));

        foreach (var invalid in new[] { Empty, activities with { Activities = [activities.Activities[0] with { Version = "2" }] } })
            await Assert.ThrowsAsync<WorkflowEngineException>(() => store.PublishPackageDefinitionAsync(draft.Id, package, invalid, store));

        Assert.Equal(before, await store.GetCatalogRevisionAsync());
        Assert.Null(await store.GetPackageAsync(package.Id, package.Version));

        await store.RegisterPackageAsync(package with { Origin = WorkflowPackageOrigin.Uploaded, PublishedDraft = null }, activities, store,
            "uploader");

        Assert.Equal(WorkflowErrorKind.InvalidState, (await Assert.ThrowsAsync<WorkflowEngineException>(() =>
            store.PublishPackageDefinitionAsync(draft.Id, package, activities, store, "publisher"))).Kind);

        Assert.Equal(draft, await store.GetDefinitionAsync("activity", draft.Name, 1));
        Assert.Null((await store.GetPackageAsync(package.Id, package.Version))!.PublishedDraft);
        Assert.Equal("uploader", (await store.GetPackageAsync(package.Id, package.Version))!.RegisteredBy);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task DeleteAndRecreateDuringArtifactPreparationCannotPublishTheOldGeneration(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var draft = await store.SaveDefinitionAsync(new("recreated.echo", 1, "activity", "old"), null);
        var (package, activities) = await Publication(store, draft);
        var gate = new Artifacts(store) { Gate = true };
        var publishing = store.PublishPackageDefinitionAsync(draft.Id, package, activities, gate);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        WorkflowDraft replacement;

        try
        {
            await store.DeleteDefinitionAsync(draft.Id);
            replacement = await store.SaveDefinitionAsync(new(draft.Name, 1, "activity", "new"), null);
        }
        finally
        {
            gate.Release.TrySetResult();
        }

        await Assert.ThrowsAsync<CatalogPreconditionException>(() => publishing);
        Assert.NotEqual(draft.Generation, replacement.Generation);
        Assert.Equal(1, replacement.Revision);
        Assert.Equal(replacement, await store.GetDefinitionAsync("activity", draft.Name, 1));
        Assert.Null(await store.GetPackageAsync(package.Id, package.Version));
        Assert.Empty((await store.ListActivitiesAsync(new(CatalogScope.Unrestricted))).Items);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task FailedDescriptorWriteRollsBackPackageReceiptDraftAndRevision(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var draft = await store.SaveDefinitionAsync(new("rollback.echo", 1, "activity", "source"), null);
        var (package, activities) = await Publication(store, draft);
        var before = await store.GetCatalogRevisionAsync();

        if (database.Profile == StoreProfiles.LocalMemory)
            await Assert.ThrowsAsync<WorkflowEngineException>(() => store.PublishPackageDefinitionAsync(draft.Id, package,
                activities with { Activities = [activities.Activities[0] with { Icon = "INVALID" }] }, store));
        else
        {
            await using var connection = database.NewConnection();

            await connection.ExecuteAsync(database.Profile switch
            {
                StoreProfiles.LocalSqlite =>
                    "CREATE TRIGGER reject_publication BEFORE INSERT ON ixiflow_package_activities BEGIN SELECT RAISE(ABORT,'fixture'); END;",
                StoreProfiles.DistributedSqlServer =>
                    "CREATE TRIGGER dbo.RejectPublication ON dbo.IxIFlowPackageActivities AFTER INSERT AS THROW 51000,'fixture',1;",
                _ =>
                    "CREATE FUNCTION reject_publication() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'fixture'; END $$; CREATE TRIGGER reject_publication BEFORE INSERT ON ixiflow_package_activities FOR EACH ROW EXECUTE FUNCTION reject_publication();"
            });

            await Assert.ThrowsAnyAsync<Exception>(() => store.PublishPackageDefinitionAsync(draft.Id, package, activities, store));

            await connection.ExecuteAsync(database.Profile switch
            {
                StoreProfiles.LocalSqlite => "DROP TRIGGER reject_publication",
                StoreProfiles.DistributedSqlServer => "DROP TRIGGER dbo.RejectPublication",
                _ => "DROP TRIGGER reject_publication ON ixiflow_package_activities; DROP FUNCTION reject_publication()"
            });
        }

        Assert.Equal(before, await store.GetCatalogRevisionAsync());
        Assert.Equal(draft, await store.GetDefinitionAsync("activity", draft.Name, 1));
        Assert.Null(await store.GetPackageAsync(package.Id, package.Version));
        Assert.Empty((await store.ListActivitiesAsync(new(CatalogScope.Unrestricted))).Items);
        Assert.True((await store.PublishPackageDefinitionAsync(draft.Id, package, activities, store)).Created);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task PackageAttemptsReconcileAuditedReceiptsAcrossWithdrawalAndRejectForgery(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var draft = await store.SaveDefinitionAsync(new("receipt.echo", 1, "activity", "source"), null);
        var attempt = await store.BeginPublicationAsync(draft.Id, Guid.NewGuid(), "starter", TimeSpan.FromMinutes(1));
        var (package, activities) = await Publication(store, draft);
        var published = (await store.PublishPackageDefinitionAsync(draft.Id, package, activities, store, "publisher")).Stored;
        var receipt = new WorkflowPublicationReceipt(draft.Id, JsonSerializer.Serialize(published));

        await Assert.ThrowsAsync<ArgumentException>(() => store.CompletePublicationAsync(attempt.Id, attempt.HostId,
            new(receipt with { ReceiptJson = JsonSerializer.Serialize(published with { RegisteredBy = "forged" }) }, [], null)));

        await store.SetPackageWithdrawnAsync(package.Id, package.Version, true, new(published.LifecycleRevision), "owner");
        var completed = await store.CompletePublicationAsync(attempt.Id, attempt.HostId, new(receipt, [], null));
        Assert.Equal(WorkflowPublicationState.Published, completed.State);
        Assert.True(JsonSerializer.Deserialize<WorkflowPackage>(completed.Outcome!.Receipt!.ReceiptJson)!.Withdrawn);
        var publisher = new WorkflowPublisher(store, new Artifacts(store) { RefuseReads = true });
        using var service = new PublicationService(store, publisher);
        var result = await service.PublishAsync(draft.Id, TimeSpan.FromSeconds(5), "retry");
        Assert.Equal(attempt.Id, result.Id);
        Assert.Equal(WorkflowPublicationState.Published, result.State);
        Assert.True((await publisher.PublishAsync(draft.Id)).Success);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task WithdrawalAllowsAllHoldersMasksThemAndUsesCompareAndSwap(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var root = await Register(store, store, "holder.model", WorkflowPackageOrigin.Resolved);
        var pin = new WorkflowPackageReference(root.Id, root.Version, root.Digest);
        await Register(store, store, "holder.parent", WorkflowPackageOrigin.Uploaded, [pin]);
        await store.RegisterTemplateVersionAsync(TemplateCatalogScenarios.Content(packages: [pin]), store);
        var bundle = await store.PutAsync(ArtifactKind.Bundle, new MemoryStream([2]));
        var contract = WorkflowContract.OfType(typeof(object));

        await store.RegisterWorkflowVersionAsync(new("Holder", 1, "hash", contract.ToJson(), contract.Hash,
            new("Holder", null, [], null, null, WorkflowTypeIdentity.SavedName(typeof(object)), "global::System.Object"), [],
            ArtifactDigest: bundle, ArtifactReferences: [root.Digest]), store);

        await store.SeedAsync(new WorkflowInstance
        {
            InstanceId = "retained", WorkflowName = "Holder", WorkflowVersion = 1, BundleDigest = bundle,
            Status = WorkflowStatus.Completed, WorkflowDataJson = StoredJson.Inline("{}"),
            ExecutionStateJson = StoreTestSupport.CheckpointJson
        });

        var holders = await store.GetPackageHoldersAsync(root.Id, root.Version, CatalogScope.Unrestricted);
        Assert.Equal("Holder", Assert.Single(holders.Workflows).Name);
        Assert.Single(holders.Templates);
        Assert.Equal("holder.parent", Assert.Single(holders.Packages).Id);
        Assert.True(holders.InstancePins);

        var hidden = await store.GetPackageHoldersAsync(root.Id, root.Version,
            new CatalogScope(new([], []), new(), new([root.Id]), new([], [])));

        Assert.Empty(hidden.Workflows);
        Assert.Empty(hidden.Templates);
        Assert.Empty(hidden.Packages);
        Assert.True(hidden.OutsideScope);

        foreach (var invalid in new[]
                 {
                     (Id: root.Id + "\0", Version: root.Version, Actor: (string?)null),
                     (Id: root.Id, Version: root.Version + "\0", Actor: (string?)null),
                     (Id: root.Id, Version: root.Version, Actor: (string?)"owner\0hidden")
                 })
            await Assert.ThrowsAsync<ArgumentException>(() =>
                store.SetPackageWithdrawnAsync(invalid.Id, invalid.Version, true, new(root.LifecycleRevision), invalid.Actor));

        var before = await store.GetCatalogRevisionAsync();
        var races = await Task.WhenAll(Withdraw("a"), Withdraw("b"));
        Assert.Single(races, item => item.Row != null);
        Assert.IsType<CatalogPreconditionException>(Assert.Single(races, item => item.Error != null).Error);
        var withdrawn = Assert.Single(races, item => item.Row != null).Row!;
        Assert.True(withdrawn.Withdrawn);
        Assert.NotNull(withdrawn.WithdrawnAtUtc);
        Assert.Equal(before + 1, withdrawn.LifecycleRevision);
        Assert.Equal(root.RegisteredAtUtc, withdrawn.RegisteredAtUtc);
        Assert.True(await store.ExistsAsync(root.Digest));
        Assert.True((await store.GetPackageDetailAsync(root.Id, root.Version, CatalogScope.Unrestricted))!.Summary.Withdrawn);

        await Assert.ThrowsAsync<WorkflowEngineException>(() =>
            store.SetPackageWithdrawnAsync(root.Id, root.Version, false, new(withdrawn.LifecycleRevision)));

        var noOp = await store.SetPackageWithdrawnAsync(root.Id, root.Version, true, new(withdrawn.LifecycleRevision), "no-op");
        Assert.Equal(withdrawn.WithdrawnBy, noOp.WithdrawnBy);
        Assert.Equal(before + 1, await store.GetCatalogRevisionAsync());

        await Assert.ThrowsAsync<WorkflowEngineException>(() => store.RegisterTemplateVersionAsync(
            TemplateCatalogScenarios.Content(key: "new-template", packages: [pin]), store));

        async Task<(WorkflowPackage? Row, Exception? Error)> Withdraw(string actor)
        {
            try
            {
                return (await store.SetPackageWithdrawnAsync(root.Id, root.Version, true, new(root.LifecycleRevision), actor), null);
            }
            catch (CatalogPreconditionException error)
            {
                return (null, error);
            }
        }
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task VerifiedUploadRestoresIdenticalBytesWithoutAFeedAndCannotReplaceTheReceipt(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var bytes = PublishingFeed.Package("restore.echo", new());
        var ingestion = new PackageIngestion(store, store);
        var candidate = await ingestion.ReadPackageAsync(new MemoryStream(bytes));
        var draft = await store.SaveDefinitionAsync(new(candidate.Package.Id, 1, "activity", "source"), null);
        await store.PutAsync(ArtifactKind.Package, new MemoryStream(bytes));
        var descriptor = Descriptor(draft.Name);

        var published = (await store.PublishPackageDefinitionAsync(draft.Id,
            candidate.Package with { Origin = WorkflowPackageOrigin.Published, PublishedDraft = draft.Id },
            Empty with { Activities = [descriptor] }, store, "publisher")).Stored;

        await store.SetPackageWithdrawnAsync(published.Id, published.Version, true, new(published.LifecycleRevision), "withdrawer");
        var withdrawn = (await store.GetPackageAsync(published.Id, published.Version))!;

        await Assert.ThrowsAsync<WorkflowEngineException>(() => store.RegisterPackageAsync(
            candidate.Package with { Origin = WorkflowPackageOrigin.Resolved },
            Empty with { Activities = [descriptor] }, store));

        var different = PublishingFeed.Package(published.Id, new() { ["different.txt"] = [9] });
        await Assert.ThrowsAsync<WorkflowEngineException>(() => ingestion.UploadPackageAsync(new MemoryStream(different)));
        Assert.True((await store.GetPackageAsync(published.Id, published.Version))!.Withdrawn);
        var restored = await ingestion.UploadPackageAsync(new MemoryStream(bytes), "restorer");
        Assert.False(restored.Created);
        Assert.False(restored.Stored.Withdrawn);
        Assert.Null(restored.Stored.WithdrawnAtUtc);
        Assert.Null(restored.Stored.WithdrawnBy);
        Assert.True(restored.Stored.LifecycleRevision > withdrawn.LifecycleRevision);
        Assert.Equal(WorkflowPackageOrigin.Published, restored.Stored.Origin);
        Assert.Equal(published.PublishedDraft, restored.Stored.PublishedDraft);
        Assert.Equal(published.PublishedBy, restored.Stored.PublishedBy);
        Assert.Equal(published.RegisteredAtUtc, restored.Stored.RegisteredAtUtc);
        Assert.Equal(published.RegisteredBy, restored.Stored.RegisteredBy);
        Assert.Equal(descriptor.ToJson(), Assert.Single(restored.Activities.Activities).ToJson());

        await Assert.ThrowsAsync<CatalogPreconditionException>(() =>
            store.SetPackageWithdrawnAsync(published.Id, published.Version, true, new(withdrawn.LifecycleRevision)));
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task RestoringAPendingUploadReentersIndexingWithoutChangingItsFirstAudit(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var ingestion = new PackageIngestion(store, store);
        var bytes = PublishingFeed.Package("restore.pending", new(), "<dependency id=\"restore.dependency\" version=\"[1.0.0]\"/>");
        var first = await ingestion.UploadPackageAsync(new MemoryStream(bytes), "first");
        Assert.Equal(PackageExtraction.Pending, first.Activities.Extraction);
        await store.SetPackageWithdrawnAsync(first.Stored.Id, first.Stored.Version, true, new(first.Stored.LifecycleRevision));
        await ingestion.UploadPackageAsync(new MemoryStream(PublishingFeed.Package("restore.dependency", new())));
        var held = (await store.GetPackageDetailAsync(first.Stored.Id, first.Stored.Version, CatalogScope.Unrestricted))!;
        Assert.True(held.Summary.Withdrawn);
        Assert.Equal(PackageExtraction.Pending, held.Activities.Extraction);
        var restored = await ingestion.UploadPackageAsync(new MemoryStream(bytes), "restorer");
        Assert.False(restored.Stored.Withdrawn);
        var completed = (await store.GetPackageDetailAsync(first.Stored.Id, first.Stored.Version, CatalogScope.Unrestricted))!;
        Assert.Equal(PackageExtraction.Indexed, completed.Activities.Extraction);
        Assert.Equal("restore.dependency", Assert.Single(completed.Activities.Closure!).Id);
        Assert.Equal(first.Stored.RegisteredAtUtc, completed.RegisteredAtUtc);
        Assert.Equal("first", completed.RegisteredBy);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task KnownWithdrawnIdentitiesNeverFetchAndVersionUnionsExcludeFeedTombstones(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        using var feed = new HttpFeed();
        var ingestion = new PackageIngestion(store, store);

        var candidate =
            await ingestion.ReadPackageAsync(new MemoryStream(PublishingFeed.Package("feed.identity", new(), version: "2.0.0")));

        await store.PutAsync(ArtifactKind.Package, new MemoryStream(candidate.Bytes.ToArray()));
        var registered = await store.RegisterPackageAsync(candidate.Package, Empty, store);

        await store.SetPackageWithdrawnAsync(candidate.Package.Id, candidate.Package.Version, true,
            new(registered.Stored.LifecycleRevision));

        var options = new WorkflowPublisherOptions { NuGetConfigPath = feed.Config };
        var publisher = new WorkflowPublisher(store, store, options);
        var source = TypedPublishingScenarios.Yaml("FeedSelection") + "\nimports: {catalogs: [feed.identity@2.0.0]}\n";
        var draft = await store.SaveDefinitionAsync(new("FeedSelection", 1, "workflow", source), null);
        Assert.Equal("PUB025", Assert.Single((await publisher.ValidateAsync(draft)).Diagnostics).Code);
        Assert.Equal("PUB025", Assert.Single((await publisher.PublishAsync(draft.Id)).Diagnostics).Code);
        Assert.Empty(feed.Requests);
        draft = await store.SaveDefinitionAsync(draft with { Source = source.Replace("@2.0.0", "") }, draft.Id);
        var result = await publisher.PublishAsync(draft.Id);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var manifest = JsonSerializer.Deserialize<WorkflowBundleManifest>(result.Workflow!.ManifestJson!)!;
        Assert.Equal("1.0.0", Assert.Single(manifest.ActivityPackages).Version);
        Assert.Contains(feed.Requests, path => path == "/flat/feed.identity/index.json");
        Assert.DoesNotContain(feed.Requests, path => path.Contains("/2.0.0/", StringComparison.Ordinal));
        Assert.True((await store.GetPackageAsync("feed.identity", "2.0.0"))!.Withdrawn);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task WithdrawnDependenciesAreRecheckedInPackageAndWorkflowTransactions(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var dependency = await Register(store, store, "late.dependency", WorkflowPackageOrigin.Resolved);
        var pin = new WorkflowPackageReference(dependency.Id, dependency.Version, dependency.Digest);
        await store.SetPackageWithdrawnAsync(dependency.Id, dependency.Version, true, new(dependency.LifecycleRevision));
        var draft = await store.SaveDefinitionAsync(new("late.echo", 1, "activity", "source"), null);
        var (package, activities) = await Publication(store, draft);

        Assert.Equal(WorkflowErrorKind.InvalidState, (await Assert.ThrowsAsync<WorkflowEngineException>(() =>
            store.PublishPackageDefinitionAsync(draft.Id, package, activities with { Closure = [pin] }, store))).Kind);

        await Assert.ThrowsAsync<WorkflowEngineException>(() => store.RegisterPackageAsync(
            package with { Origin = WorkflowPackageOrigin.Resolved, PublishedDraft = null },
            activities with { Closure = [pin] }, store));

        Assert.Null(await store.GetPackageAsync(package.Id, package.Version));
        Assert.Equal(draft, await store.GetDefinitionAsync("activity", draft.Name, 1));
        var workflow = await store.SaveDefinitionAsync(new("LateWorkflow", 1, "workflow", "source"), null);
        var bundle = await store.PutAsync(ArtifactKind.Bundle, new MemoryStream([55]));
        var contract = WorkflowContract.OfType(typeof(object));

        var version = new WorkflowVersion(workflow.Name, 1, "hash", contract.ToJson(), contract.Hash,
            new(workflow.Name, null, [], null, null, WorkflowTypeIdentity.SavedName(typeof(object)), "global::System.Object"), [],
            ArtifactDigest: bundle, ArtifactReferences: [dependency.Digest], PublishedDraft: workflow.Id, ManifestJson: "{}");

        await Assert.ThrowsAsync<WorkflowEngineException>(() => store.PublishDefinitionAsync(workflow.Id, version, store));
        Assert.Equal(workflow, await store.GetDefinitionAsync("workflow", workflow.Name, 1));
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task WithdrawnPublishedUploadedTemplateAndPackageClosuresSurviveRetention(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        using var folder = new PublishingFeed();
        var files = new FileSystemArtifactStore(folder.Folder);
        store.CatalogArtifacts = files;
        var leaf = await Register(store, files, "retained.leaf", WorkflowPackageOrigin.Resolved);
        var leafPin = new WorkflowPackageReference(leaf.Id, leaf.Version, leaf.Digest);
        var uploaded = await Register(store, files, "retained.upload", WorkflowPackageOrigin.Uploaded, [leafPin]);
        var model = await Register(store, files, "retained.model", WorkflowPackageOrigin.Resolved);

        await store.RegisterTemplateVersionAsync(TemplateCatalogScenarios.Content(packages: [new(model.Id, model.Version, model.Digest)]),
            files);

        var draft = await store.SaveDefinitionAsync(new("retained.echo", 1, "activity", "source"), null);
        var (package, activities) = await Publication(files, draft);
        var published = (await store.PublishPackageDefinitionAsync(draft.Id, package, activities, files)).Stored;

        var collectable = await Register(store, files, "collected.echo", WorkflowPackageOrigin.Resolved, activities: activities with
        {
            Activities = [activities.Activities[0] with { PackageId = "collected.echo" }]
        });

        foreach (var item in new[] { uploaded, published, model, leaf })
            await store.SetPackageWithdrawnAsync(item.Id, item.Version, true, new(item.LifecycleRevision));

        foreach (var file in Directory.EnumerateFiles(folder.Folder, "*.artifact"))
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-100));

        Assert.Equal(1, await store.PurgeArtifactsAsync(files, TimeSpan.FromDays(90), 100));

        foreach (var item in new[] { uploaded, published, model, leaf })
            Assert.True(await files.ExistsAsync(item.Digest));

        Assert.False(await files.ExistsAsync(collectable.Digest));
        var detail = (await store.GetPackageDetailAsync(collectable.Id, collectable.Version, CatalogScope.Unrestricted))!;
        Assert.False(detail.Summary.ArtifactPresent);
        Assert.Equal(collectable.Digest, detail.Summary.Digest);
        Assert.Single(detail.Activities.Activities);

        Assert.Contains((await store.ListActivitiesAsync(new(CatalogScope.Unrestricted))).Items,
            item => item.Provider.PackageId == collectable.Id && !item.ArtifactPresent);

        Assert.Equal(0, await store.PurgeArtifactsAsync(files, TimeSpan.FromDays(90), 100));
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task RequiredDecoratorsForwardPublicationWithdrawalAndHolderReads(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        IWorkflowCatalogStore facade = new WorkflowCatalog(GuardedStateRepository.Wrap(new FaultRepository(store)));
        var draft = await facade.SaveDefinitionAsync(new("forward.echo", 1, "activity", "source"), null);
        var (package, activities) = await Publication(store, draft);
        var result = await facade.PublishPackageDefinitionAsync(draft.Id, package, activities, store, "publisher");

        var withdrawn =
            await facade.SetPackageWithdrawnAsync(package.Id, package.Version, true, new(result.Stored.LifecycleRevision), "owner");

        Assert.True(withdrawn.Withdrawn);
        Assert.Equal("owner", withdrawn.WithdrawnBy);
        Assert.Empty((await facade.GetPackageHoldersAsync(package.Id, package.Version, CatalogScope.Unrestricted)).Workflows);
        Assert.True((await facade.GetPackageDetailAsync(package.Id, package.Version, CatalogScope.Unrestricted))!.Summary.Withdrawn);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task WithdrawnPinsStillDownloadExecuteOnAColdHostAndCopyThroughVerifiedCallees(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var version = await ActivityPackageFixture.PublishAsync(store);
        var packages = await store.GetPackagesAsync();

        foreach (var package in packages)
        {
            await store.SetPackageWithdrawnAsync(package.Id, package.Version, true, new(package.LifecycleRevision));
            await using var stream = (await store.OpenAsync(package.Digest))!;
            var entries = await WorkflowPackageArchive.ReadAsync(stream, package.Digest, new());
            Assert.Equal(package.Entries.Count, entries.Count);
        }

        var publisher = new WorkflowPublisher(store, store);

        Assert.Equal("PUB025",
            Assert.Single((await publisher.ValidateAsync(new(version.Name, 1, "workflow", ActivityPackageFixture.Yaml()))).Diagnostics)
                .Code);

        var caller = await store.SaveDefinitionAsync(new("PinnedCaller", 1, "workflow", """
                                                                                        workflow: {name: PinnedCaller, version: 1, dataType: System.Object}
                                                                                        imports: {workflows: [PackageLifecycle@1]}
                                                                                        definitions:
                                                                                          - kind: invokeWorkflow
                                                                                            id: child
                                                                                            workflow: {name: PackageLifecycle, version: 1}
                                                                                        """), null);

        var copied = await publisher.PublishAsync(caller.Id);
        Assert.True(copied.Success, string.Join("; ", copied.Diagnostics));

        Assert.Equal(packages.Select(item => item.Digest).OrderBy(item => item.Value),
            JsonSerializer.Deserialize<WorkflowBundleManifest>(copied.Workflow!.ManifestJson!)!
                .ActivityPackages.Select(item => item.Digest).OrderBy(item => item.Value));

        using var folder = new PublishingFeed();
        await using var host = PublishedBundleScenario.Host(store, Path.Combine(folder.Folder, "cold"), configure: database.ConfigureHost);
        await database.StartHostLinksAsync(host);
        var client = host.GetRequiredService<IWorkflowClient>();
        var id = await client.StartAsync(version.Name, 1, new { Value = "before" });
        await PublishedBundleScenario.UntilAsync(store, id, WorkflowStatus.Suspended);
        using var definition = await host.GetRequiredService<IWorkflowCatalog>().ResolveWorkflowDefinitionAsync(version.Name, 1);
        var signal = Activator.CreateInstance(definition.Definition.WorkflowDataType!.Assembly.GetType("Packaged.Signal")!)!;
        signal.GetType().GetProperty("Value")!.SetValue(signal, "after-withdrawal");
        var result = await client.ResumeAsync(id, signal);
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("package-v1:after-withdrawal", result.WorkflowData!.GetType().GetProperty("Result")!.GetValue(result.WorkflowData));
        Assert.Equal(version.ArtifactDigest, (await store.GetWorkflowInstanceAsync(id))!.BundleDigest);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task UnreadableOrphansReportHoldersAndProtectUnregisteredClosureBytes(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        using var folder = new PublishingFeed();
        var files = new FileSystemArtifactStore(folder.Folder);
        store.CatalogArtifacts = files;
        var raw = await files.PutAsync(ArtifactKind.Package, new MemoryStream([10]));

        var package = await Register(store, files, "orphan.resolved", WorkflowPackageOrigin.Resolved,
            [new("orphan.unregistered", "1.0.0", raw)]);

        await store.SeedAsync(new WorkflowInstance
        {
            InstanceId = "orphan", WorkflowName = "Lost", WorkflowVersion = 1, Status = WorkflowStatus.Completed,
            WorkflowDataJson = StoredJson.Inline("{}"), ExecutionStateJson = StoreTestSupport.CheckpointJson
        });

        var missing = ArtifactDigest.Of([11]);

        if (store is InMemoryWorkflowStateRepository memory)
        {
            var instances = (IDictionary<string, WorkflowInstance>)typeof(InMemoryWorkflowStateRepository)
                .GetField("_instances", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(memory)!;

            instances["orphan"].BundleDigest = missing;
        }
        else
        {
            await using var connection = database.NewConnection();

            await connection.ExecuteAsync(database.Profile == StoreProfiles.DistributedSqlServer
                ? "UPDATE dbo.IxIFlowWorkflowInstances SET BundleDigest=@Digest"
                : "UPDATE ixiflow_workflow_instances SET bundle_digest=@Digest", new { Digest = missing.Value });
        }

        var holders = await store.GetPackageHoldersAsync(package.Id, package.Version, CatalogScope.Unrestricted);
        Assert.True(holders.UnreadableInstancePins);
        Assert.True(holders.InstancePins);
        Assert.Empty(holders.Workflows);
        Assert.Empty(holders.Templates);
        Assert.Empty(holders.Packages);
        var abandoned = await files.PutAsync(ArtifactKind.Bundle, new MemoryStream([12]));

        foreach (var file in Directory.EnumerateFiles(folder.Folder, "*.artifact"))
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-100));

        Assert.Equal(1, await store.PurgeArtifactsAsync(files, TimeSpan.FromDays(90), 100));
        Assert.True(await files.ExistsAsync(package.Digest));
        Assert.True(await files.ExistsAsync(raw));
        Assert.False(await files.ExistsAsync(abandoned));
    }

    private static ActivityDescriptor Descriptor(string id) =>
        ActivityDescriptor.OfType(typeof(Echo), id, "1.0.0") with { Key = id, Version = "1" };

    [WorkflowActivity("fixture.echo", Version = "1")]
    public sealed class Echo : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static async Task<(WorkflowPackage, PackageActivities)> Publication(IArtifactStore artifacts, WorkflowDraft draft)
    {
        var digest = await artifacts.PutAsync(ArtifactKind.Package, new MemoryStream(Encoding.UTF8.GetBytes(draft.Name)));

        return (new(draft.Name, "1.0.0", digest, new Dictionary<string, ArtifactDigest> { ["fixture.nuspec"] = digest }, [])
            {
                Origin = WorkflowPackageOrigin.Published, PublishedDraft = draft.Id, PublishedBy = "untrusted", RegisteredBy = "untrusted"
            },
            Empty with { Activities = [Descriptor(draft.Name)] });
    }

    private static async Task<WorkflowPackage> Register(IWorkflowCatalogStore store, IArtifactStore artifacts, string id,
        WorkflowPackageOrigin origin, IReadOnlyList<WorkflowPackageReference>? closure = null, PackageActivities? activities = null)
    {
        var digest = await artifacts.PutAsync(ArtifactKind.Package, new MemoryStream(Encoding.UTF8.GetBytes(id)));

        return (await store.RegisterPackageAsync(
            new(id, "1.0.0", digest, new Dictionary<string, ArtifactDigest> { ["fixture.nuspec"] = digest }, [])
                { Origin = origin }, activities ?? Empty with { Closure = closure ?? [] }, artifacts)).Stored;
    }

    private sealed class Artifacts(IArtifactStore inner) : IArtifactStore
    {
        internal bool Gate, RefuseReads;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken token = default)
        {
            if (RefuseReads)
                throw new InvalidOperationException("A committed receipt must precede artifact checks");

            if (Gate)
            {
                Gate = false;
                Entered.TrySetResult();
                await Release.Task.WaitAsync(token);
            }

            return await inner.ExistsAsync(digest, token);
        }

        public ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken token = default) => inner.OpenAsync(digest, token);

        public ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken token = default) =>
            inner.PutAsync(kind, content, token);

        public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken token = default) => inner.DeleteAsync(digest, token);

        public ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime before, int limit,
            CancellationToken token = default) => inner.PurgeUnreferencedAsync(references, before, limit, token);
    }

    private sealed class HttpFeed : IDisposable
    {
        private readonly PublishingFeed _folder = new();
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _serving;
        internal List<string> Requests { get; } = [];
        internal string Config => _folder.Config;

        internal HttpFeed()
        {
            _listener.Start();
            var address = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;

            File.WriteAllText(Config,
                "<configuration><packageSources><clear/><add key=\"memory\" value=\"" + address +
                "/v3/index.json\" allowInsecureConnections=\"true\"/></packageSources></configuration>");

            _serving = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        using var client = await _listener.AcceptTcpClientAsync();
                        await using var stream = client.GetStream();
                        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                        var request = (await reader.ReadLineAsync())!;

                        while (!string.IsNullOrEmpty(await reader.ReadLineAsync()))
                        {
                        }

                        var path = request.Split(' ')[1];
                        Requests.Add(path);
                        byte[] bytes;

                        if (path == "/v3/index.json")
                            bytes = Encoding.UTF8.GetBytes("{\"version\":\"3.0.0\",\"resources\":[{\"@id\":\"" + address +
                                "/flat/\",\"@type\":\"PackageBaseAddress/3.0.0\"}]}");
                        else if (path.EndsWith("index.json", StringComparison.Ordinal))
                            bytes = Encoding.UTF8.GetBytes("{\"versions\":[\"1.0.0\",\"2.0.0\"]}");
                        else if (path.EndsWith(".nuspec", StringComparison.Ordinal))
                            bytes = Encoding.UTF8.GetBytes(
                                "<package><metadata><id>feed.identity</id><version>1.0.0</version><authors>tests</authors><description>fixture</description></metadata></package>");
                        else
                            bytes = PublishingFeed.Package("feed.identity", new());

                        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: " + bytes.Length +
                            "\r\nConnection: close\r\n\r\n"));

                        await stream.WriteAsync(bytes);
                    }
                }
                catch (Exception error) when (error is SocketException or ObjectDisposedException)
                {
                }
            });
        }

        public void Dispose()
        {
            _listener.Stop();
            _serving.GetAwaiter().GetResult();
            _folder.Dispose();
        }
    }
}