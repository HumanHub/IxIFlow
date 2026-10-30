using Microsoft.Extensions.Time.Testing;
using Dapper;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.ExceptionServices;
using System.Security;
using System.Text;
using System.Text.Json;
using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Dsl.Documents;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "authoring")]
[Trait("Spec", "F-09")]
[Trait("Spec", "F-14")]
[Trait("Spec", "F-01")]
public sealed class TypedPublishingScenarios
{
    internal static string Yaml(string name = "Typed") => $$"""
                                                            # Deliberately unlike the canonical formatting.

                                                            workflow: { name: {{name}}, version: 1, dataType: System.Object }
                                                            definitions:
                                                              - { kind: delay, id: pause, duration: '00:00:01' }
                                                            """;

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task FeedBackedFullValidationCannotWriteArtifactsCatalogAgesDraftsOrHistory(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        using var feed = new PublishingFeed();
        var (activity, dependency) = PublishingFeed.Packages();
        await File.WriteAllBytesAsync(Path.Combine(feed.Folder, "fixture.activities.1.0.0.nupkg"), activity);
        await File.WriteAllBytesAsync(Path.Combine(feed.Folder, "fixture.dependency.1.0.0.nupkg"), dependency);

        var yaml = """
                   workflow: {name: FeedDryRun, version: 1, dataType: Packaged.Data}
                   imports: {catalogs: [fixture.activities]}
                   definitions:
                     - kind: activity
                       id: echo
                       activity: fixture.echo
                   """;

        var draft = await store.SaveDefinitionAsync(new("FeedDryRun", 1, "workflow", yaml), null, "author");
        var revision = await store.GetCatalogRevisionAsync();
        var catalog = CatalogProbe.Wrap(store, out var probe);
        probe.ReadOnly = true;
        var artifacts = new ArtifactProbe(store) { ReadOnly = true };
        var publisher = new WorkflowPublisher(catalog, artifacts, new() { NuGetConfigPath = feed.Config });
        var report = await publisher.ValidateAsync(draft);
        Assert.True(report.IsValid, string.Join("; ", report.Diagnostics));
        var unsaved = await publisher.ValidateAsync(draft with { Generation = Guid.Empty, Revision = 0 });
        Assert.True(unsaved.IsValid, string.Join("; ", unsaved.Diagnostics));
        Assert.Equal(0, artifacts.Puts);
        Assert.Equal(0, probe.Writes);
        Assert.Equal(revision, await store.GetCatalogRevisionAsync());
        Assert.Empty(await store.GetPackagesAsync());
        Assert.Empty(await store.GetWorkflowVersionsAsync());
        Assert.Empty((await store.GetPublicationsAsync(new(CatalogScope.Unrestricted, "workflow", draft.Name, 1))).Items);
        Assert.Equal(draft, await store.GetDefinitionAsync("workflow", draft.Name, 1));

        // The validator must reach Build/Test, beyond Roslyn and descriptor extraction.
        var broken = PublishingFeed.Package("broken.activity", new Dictionary<string, byte[]>
        {
            ["lib/net10.0/Broken.dll"] = PublishingFeed.Emit("Broken", """
                                                                       using IxIFlow.Core; using IxIFlow.ActivitySdk; using System.Threading; using System.Threading.Tasks;
                                                                       namespace Broken;
                                                                       [WorkflowActivity("broken.echo")]
                                                                       public sealed class Echo : IAsyncActivity {
                                                                           public string Unrestorable => "value";
                                                                           public Task ExecuteAsync(IActivityContext context, CancellationToken token = default) => Task.CompletedTask;
                                                                       }
                                                                       """)
        });

        await File.WriteAllBytesAsync(Path.Combine(feed.Folder, "broken.activity.1.0.0.nupkg"), broken);

        var invalid = await publisher.ValidateAsync(new("Broken", 1, "workflow", """
                                                                                 workflow: {name: Broken, version: 1, dataType: System.Object}
                                                                                 imports: {catalogs: [broken.activity]}
                                                                                 definitions: [{kind: activity, activity: broken.echo}]
                                                                                 """));

        Assert.False(invalid.IsValid);
        Assert.Contains(invalid.Diagnostics, item => item.Stage == WorkflowPublishStage.Test && item.Code == "PUB004");
        Assert.Equal(revision, await store.GetCatalogRevisionAsync());
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task TypedReceiptAuditAndCanonicalPdbUseTheSameReparsedSource(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var draft = await store.SaveDefinitionAsync(new("Typed", 1, "workflow", Yaml()), null, "author");
        var publisher = new WorkflowPublisher(store, store);
        var result = await publisher.PublishAsync(draft.Id, "publisher");
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Equal("workflow", result.Kind);
        Assert.Null(result.Package);
        Assert.Null(result.Template);
        Assert.Equal("publisher", result.Workflow!.PublishedBy);
        Assert.Equal(draft.Id, result.Receipt!.Draft);
        Assert.Equal(result.Workflow, WorkflowPublishResult.FromReceipt(result.Receipt).Workflow);

        Assert.Throws<ArgumentException>(() =>
            WorkflowPublishResult.FromReceipt(result.Receipt with { Draft = draft.Id with { Generation = Guid.NewGuid() } }));

        await using var stream = await store.OpenAsync(result.Workflow.ArtifactDigest!.Value);
        var bundle = await WorkflowBundleArchive.ReadAsync(stream!, result.Workflow.ArtifactDigest.Value);
        var canonical = Encoding.UTF8.GetString(bundle.Entries[WorkflowBundleArchive.YamlPath].Span);
        var parsed = WorkflowYamlSerializer.Read(canonical);
        Assert.Equal(WorkflowYamlSerializer.Write(WorkflowYamlSerializer.Read(draft.Source)), canonical);
        Assert.NotEqual(WorkflowYamlSerializer.Read(draft.Source).Definitions[0].Location.Line, parsed.Definitions[0].Location.Line);
        var source = Encoding.UTF8.GetString(bundle.Entries[WorkflowBundleArchive.SourcePath].Span);
        Assert.Contains("#line (" + parsed.Definitions[0].Location.Line + ",", source);

        using var symbols =
            MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(bundle.Entries[WorkflowBundleArchive.PdbPath].ToArray()));

        var metadata = symbols.GetMetadataReader();
        var yamlDocument = metadata.Documents.Single(handle => metadata.GetString(metadata.GetDocument(handle).Name) == "workflow.yaml");

        var points = metadata.MethodDebugInformation.SelectMany(handle =>
        {
            var method = metadata.GetMethodDebugInformation(handle);

            return method.GetSequencePoints()
                .Select(point => (Document: point.Document.IsNil ? method.Document : point.Document, point.StartLine));
        }).ToArray();

        var durationLine = DocumentModel.GetLocation(parsed.Definitions[0], "duration").Line;

        Assert.True(points.Any(point => point.Document == yamlDocument && point.StartLine == durationLine),
            canonical + "\nPDB: " + string.Join(", ",
                points.Select(point =>
                    point.Document.IsNil
                        ? "hidden"
                        : metadata.GetString(metadata.GetDocument(point.Document).Name) + ":" + point.StartLine)));

        var retry = await publisher.PublishAsync(draft.Id, "retry");
        Assert.Equal(result.Receipt, retry.Receipt);
        Assert.Equal("publisher", retry.Workflow!.PublishedBy);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task UploadFacadesReturnFirstWinnerStoredAuditAndCompleteOfflinePendingAfterRestart(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        using var feed = new PublishingFeed();
        var options = new WorkflowPublisherOptions { NuGetConfigPath = feed.Config };
        var publisher = new WorkflowPublisher(store, store, options);
        var (activity, dependency) = PublishingFeed.Packages();
        var candidate = await publisher.ReadPackageAsync(new MemoryStream(activity));
        Assert.Equal("fixture.activities", candidate.Package.Id);
        Assert.Empty(await store.GetPackagesAsync());
        var first = await publisher.StorePackageAsync(candidate, "first");
        Assert.True(first.Created);
        Assert.Equal(PackageExtraction.Pending, first.Activities.Extraction);
        Assert.Equal("first", first.Stored.RegisteredBy);

        var repeats = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(index => publisher.UploadPackageAsync(new MemoryStream(activity), "loser" + index)));

        Assert.All(repeats, row =>
        {
            Assert.False(row.Created);
            Assert.Equal(first.Stored.RegisteredAtUtc, row.Stored.RegisteredAtUtc);
            Assert.Equal("first", row.Stored.RegisteredBy);
        });

        var conflict = PublishingFeed.Package("fixture.activities", new() { ["different.txt"] = [1] });

        Assert.Equal(WorkflowErrorKind.InvalidState,
            (await Assert.ThrowsAsync<WorkflowEngineException>(() => publisher.UploadPackageAsync(new MemoryStream(conflict)))).Kind);

        Assert.Equal(WorkflowErrorKind.InvalidRequest,
            (await Assert.ThrowsAsync<WorkflowEngineException>(() => publisher.UploadPackageAsync(new MemoryStream([1, 2])))).Kind);

        if (store is IAsyncDisposable disposable)
            await disposable.DisposeAsync();

        var restarted = database.NewRepository();

        // Seed dependency acceptance directly to simulate a crash before the indexing sweep.
        var dependencyCandidate = await publisher.ReadPackageAsync(new MemoryStream(dependency));
        await restarted.PutAsync(ArtifactKind.Package, new MemoryStream(dependency));

        await restarted.RegisterPackageAsync(dependencyCandidate.Package, new([], PackageExtraction.Indexed, null, [], []), restarted,
            "dependency");

        using var indexing = new PackageIndexingService(restarted, restarted, options);
        Assert.Equal(1, await indexing.SweepAsync());
        var completed = await restarted.GetPackageDetailAsync(first.Stored.Id, first.Stored.Version, CatalogScope.Unrestricted);
        Assert.Equal(PackageExtraction.Indexed, completed!.Activities.Extraction);
        Assert.Single(completed.Activities.Activities);
        Assert.Single(completed.Activities.Closure!);
        Assert.Equal("first", completed.RegisteredBy);

        var races = await Task.WhenAll(Enumerable.Range(0, 4).Select(index => new WorkflowPublisher(restarted, restarted, options)
            .UploadPackageAsync(new MemoryStream(PublishingFeed.Package("concurrent.upload", [])), "winner" + index)));

        var winner = Assert.Single(races, row => row.Created);

        Assert.All(races, row =>
        {
            Assert.Equal(winner.Stored.RegisteredAtUtc, row.Stored.RegisteredAtUtc);
            Assert.Equal(winner.Stored.RegisteredBy, row.Stored.RegisteredBy);
        });
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ServiceJoinsHeartbeatsAndPersistsDespiteCanceledRequest(string profile)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var database = await StoreProfiles.CreateAsync(profile, time);
        var store = database.NewRepository();
        var draft = await store.SaveDefinitionAsync(new("Typed", 1, "workflow", Yaml()), null);
        var artifacts = new ArtifactProbe(store) { GatePut = true };
        var options = ServiceOptions();
        using var owner = new PublicationService(store, new WorkflowPublisher(store, artifacts, options), options, time);
        using var other = new PublicationService(store, new WorkflowPublisher(store, store, options), options, time);
        using var request = new CancellationTokenSource();
        Task<WorkflowPublication> waiting;

        using (ExecutionContext.SuppressFlow())
            waiting = owner.PublishAsync(draft.Id, TimeSpan.FromSeconds(15), "owner", request.Token);

        try
        {
            await artifacts.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));

            var running = Assert.Single((await store.GetPublicationsAsync(new(CatalogScope.Unrestricted, "workflow", draft.Name, 1)))
                .Items);

            request.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            time.Advance(options.PublicationHeartbeatInterval + options.PublicationHeartbeatInterval);

            var heartbeat = await TestEventually.UntilAsync(running.Id.ToString(), "a newer publication heartbeat",
                () => other.GetPublicationAsync(running.Id, CatalogScope.Unrestricted),
                publication => publication!.HeartbeatAtUtc > running.HeartbeatAtUtc);

            Assert.True(heartbeat!.HeartbeatAtUtc > running.HeartbeatAtUtc);
            var joined = await other.PublishAsync(draft.Id, TimeSpan.Zero, "joiner");
            Assert.Equal(running.Id, joined.Id);
            Assert.Equal("owner", joined.StartedBy);
        }
        finally
        {
            artifacts.Release.TrySetResult();
        }

        var terminal = await other.PublishAsync(draft.Id, TimeSpan.FromSeconds(15), "late joiner");
        Assert.Equal(WorkflowPublicationState.Published, terminal.State);
        Assert.Equal("owner", WorkflowPublishResult.FromReceipt(terminal.Outcome!.Receipt!).Workflow!.PublishedBy);
        Assert.Equal(1, artifacts.Puts);
        Assert.Null(await store.GetDefinitionAsync("workflow", draft.Name, 1));
        await owner.StopAsync(default);
        await other.StopAsync(default);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ReceiptCommitCrashIsReconciledByAnotherHostAndOldAttemptRemainsExact(string profile)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var database = await StoreProfiles.CreateAsync(profile, time);
        var store = database.NewRepository();
        var draft = await store.SaveDefinitionAsync(new("Typed", 1, "workflow", Yaml()), null);
        var catalog = CatalogProbe.Wrap(store, out var probe);
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        probe.Before = (method, _) =>
        {
            if (method.Name == "CompletePublicationAsync")
            {
                lost.TrySetResult();

                throw new IOException("secret completion failure");
            }
        };

        var options = ServiceOptions();
        using var first = new PublicationService(catalog, new WorkflowPublisher(catalog, store, options), options, time);
        var original = await first.PublishAsync(draft.Id, TimeSpan.Zero, "publisher");
        await lost.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await first.StopAsync(default);
        Assert.NotNull(await store.GetWorkflowVersionAsync(draft.Name, 1));
        Assert.Null(await store.GetDefinitionAsync("workflow", draft.Name, 1));
        time.Advance(options.PublicationStaleAfter + TimeSpan.FromMilliseconds(50));

        if (database.Profile is StoreProfiles.DistributedSqlServer or StoreProfiles.DistributedPostgreSql)
        {
            // Server ownership uses database time. Age the stopped attempt on that clock.
            await using var connection = database.NewConnection();

            Assert.Equal(1, await connection.ExecuteAsync(database.Profile == StoreProfiles.DistributedSqlServer
                    ? "UPDATE dbo.IxIFlowPublicationAttempts SET HeartbeatAtUtc=DATEADD(SECOND,-2,SYSUTCDATETIME()) WHERE Id=@Id"
                    : "UPDATE ixiflow_publication_attempts SET heartbeat_at_utc=clock_timestamp()-INTERVAL '2 seconds' WHERE id=@Id",
                new { original.Id }));
        }

        var artifacts = new ArtifactProbe(store) { ReadOnly = true };
        using var second = new PublicationService(store, new WorkflowPublisher(store, artifacts, options), options, time);
        var recovered = await second.PublishAsync(draft.Id, TimeSpan.FromSeconds(10), "recovery");
        Assert.Equal(WorkflowPublicationState.Published, recovered.State);
        Assert.NotEqual(original.Id, recovered.Id);
        Assert.Equal(2, recovered.Attempt);
        Assert.Equal("publisher", WorkflowPublishResult.FromReceipt(recovered.Outcome!.Receipt!).Workflow!.PublishedBy);
        var abandoned = await second.GetPublicationAsync(original.Id, CatalogScope.Unrestricted);
        Assert.Equal(WorkflowPublicationState.Abandoned, abandoned!.State);
        Assert.Null(abandoned.Outcome);
        Assert.False(await store.HeartbeatPublicationAsync(original.Id, first.HostId));

        await Assert.ThrowsAsync<CatalogPreconditionException>(() =>
            store.CompletePublicationAsync(original.Id, first.HostId, recovered.Outcome));

        Assert.Null(await second.GetPublicationAsync(original.Id, new(new([]), new(), new(), new())));
        Assert.Equal(0, artifacts.Puts);
        await second.StopAsync(default);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task UnsupportedKindsIdentityAndSanitizedFailuresAreDurable(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();

        foreach (var (kind, code) in new[] { ("activity", "IXIYAML001"), ("template", "IXIYAML001") })
        {
            var draft = await store.SaveDefinitionAsync(new("unsupported", 1, kind, "source"), null);
            var publisher = new WorkflowPublisher(store, store);
            Assert.Equal(code, Assert.Single((await publisher.ValidateAsync(draft)).Diagnostics).Code);
            using var service = new PublicationService(store, publisher);
            var attempt = await service.PublishAsync(draft.Id, TimeSpan.FromSeconds(5));
            Assert.Equal(WorkflowPublicationState.Failed, attempt.State);
            Assert.Equal(code, Assert.Single(attempt.Outcome!.Diagnostics).Code);
            await service.StopAsync(default);
        }

        var mismatch = await new WorkflowPublisher(store, store).ValidateAsync(new("Different", 1, "workflow", Yaml()));
        Assert.Equal("PUB005", Assert.Single(mismatch.Diagnostics).Code);
        var draftWorkflow = await store.SaveDefinitionAsync(new("Typed", 1, "workflow", Yaml()), null);

        foreach (var (error, code, @class) in new (Exception, string, WorkflowPublishDiagnosticClass)[]
                 {
                     (new WorkflowEngineException(WorkflowErrorKind.InvalidState, "secret conflict"), "PUB040",
                         WorkflowPublishDiagnosticClass.Conflict),
                     (new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, "secret path"), "PUB041",
                         WorkflowPublishDiagnosticClass.Unavailable),
                     (new WorkflowEngineException(WorkflowErrorKind.ResourceExhausted, "secret limit"), "PUB042",
                         WorkflowPublishDiagnosticClass.Limit),
                     (new TimeoutException("secret timeout"), "PUB043", WorkflowPublishDiagnosticClass.Unavailable),
                     (new InvalidOperationException("secret credentials"), "PUB007", WorkflowPublishDiagnosticClass.Internal)
                 })
        {
            var catalog = CatalogProbe.Wrap(store, out var probe);

            probe.Before = (method, _) =>
            {
                if (method.Name == "GetWorkflowVersionAsync")
                    throw error;
            };

            var result = await new WorkflowPublisher(catalog, store).PublishAsync(draftWorkflow.Id);
            var diagnostic = Assert.Single(result.Diagnostics);
            Assert.Equal(code, diagnostic.Code);
            Assert.Equal(@class, result.FailureClass);
            Assert.DoesNotContain("secret", diagnostic.Message);
            using var service = new PublicationService(catalog, new WorkflowPublisher(catalog, store));
            var attempt = await service.PublishAsync(draftWorkflow.Id, TimeSpan.FromSeconds(5));
            Assert.Equal(WorkflowPublicationState.Failed, attempt.State);
            Assert.Equal(@class, attempt.Outcome!.FailureClass);
            Assert.Equal(code, Assert.Single(attempt.Outcome.Diagnostics).Code);
            Assert.DoesNotContain("secret", JsonSerializer.Serialize(attempt.Outcome));
            await service.StopAsync(default);
        }
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task CalleeCodesRemainReservedAndPublicationPinsImportsAndCalls(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        using var feed = new PublishingFeed();
        var publisher = new WorkflowPublisher(store, store, new() { NuGetConfigPath = feed.Config });
        await publisher.UploadPackageAsync(new MemoryStream(PublishingFeed.Package("fixture.pin", [])));
        await publisher.UploadPackageAsync(new MemoryStream(PublishingFeed.Package("fixture.pin", [], version: "2.0.0")));

        var callee = await store.SaveDefinitionAsync(new("Typed", 1, "workflow", Yaml() + "\nimports: {catalogs: [fixture.pin@1.0.0]}"),
            null);

        var published = await publisher.PublishAsync(callee.Id);
        Assert.True(published.Success, string.Join("; ", published.Diagnostics));

        var callerSource = """
                           workflow: {name: Caller, version: 1, dataType: System.Object}
                           imports: {workflows: [Typed], catalogs: [fixture.pin@2.0.0]}
                           definitions:
                             - kind: invokeWorkflow
                               workflow: {name: Typed}
                           """;

        var conflict = await publisher.ValidateAsync(new("Caller", 1, "workflow", callerSource));
        Assert.Equal("PUB010", Assert.Single(conflict.Diagnostics).Code);
        Assert.Equal(WorkflowPublishDiagnosticClass.Authoring, conflict.FailureClass);

        var caller = await store.SaveDefinitionAsync(
            new("Caller", 1, "workflow", callerSource.Replace("fixture.pin@2.0.0", "fixture.pin@1.0.0")), null);

        var result = await publisher.PublishAsync(caller.Id);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        await using var stream = await store.OpenAsync(result.Workflow!.ArtifactDigest!.Value);
        var bundle = await WorkflowBundleArchive.ReadAsync(stream!, result.Workflow.ArtifactDigest.Value);
        var canonical = WorkflowYamlSerializer.Read(Encoding.UTF8.GetString(bundle.Entries[WorkflowBundleArchive.YamlPath].Span));
        Assert.Equal("Typed@1", Assert.Single(canonical.Imports.Workflows));
        Assert.Equal(1, Assert.IsType<InvokeWorkflowStepDocument>(Assert.Single(canonical.Definitions)).Workflow.Version);
        await store.DeleteAsync(published.Workflow!.ArtifactDigest!.Value);
        var unavailable = await publisher.ValidateAsync(new("Caller", 1, "workflow", caller.Source));
        Assert.Equal("PUB009", Assert.Single(unavailable.Diagnostics).Code);
        Assert.Equal(WorkflowPublishDiagnosticClass.Unavailable, unavailable.FailureClass);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task LateConflictsAndMissingArtifactsUseP4CodesAndPreserveTheDraft(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var draft = await store.SaveDefinitionAsync(new("Typed", 1, "workflow", Yaml()), null);

        var edited = new ArtifactProbe(store)
            { AfterPut = async _ => await store.SaveDefinitionAsync(draft with { Source = draft.Source + "\n# edit" }, draft.Id) };

        var stale = await new WorkflowPublisher(store, edited).PublishAsync(draft.Id);
        Assert.Equal("PUB040", Assert.Single(stale.Diagnostics).Code);
        Assert.Equal(WorkflowPublishDiagnosticClass.Conflict, stale.FailureClass);
        var current = (await store.GetDefinitionAsync("workflow", draft.Name, 1))!;
        var unavailable = new ArtifactProbe(store) { AfterPut = async digest => await store.DeleteAsync(digest) };
        var missing = await new WorkflowPublisher(store, unavailable).PublishAsync(current.Id);
        Assert.Equal("PUB041", Assert.Single(missing.Diagnostics).Code);
        Assert.Equal(WorkflowPublishDiagnosticClass.Unavailable, missing.FailureClass);
        Assert.Equal(current, await store.GetDefinitionAsync("workflow", draft.Name, 1));
        Assert.Null(await store.GetWorkflowVersionAsync(draft.Name, 1));
        var badDigest = new ArtifactProbe(store) { WrongDigest = true };
        var mismatch = await new WorkflowPublisher(store, badDigest).PublishAsync(current.Id);
        Assert.Equal("PUB006", Assert.Single(mismatch.Diagnostics).Code);
        Assert.Equal(WorkflowPublishDiagnosticClass.Unavailable, mismatch.FailureClass);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task TrustedBuildTimeoutHasUnavailableClassWithoutAnyDryRunWrites(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        using var feed = new PublishingFeed();

        var bytes = PublishingFeed.Emit("SlowModel", """
                                                     using System.ComponentModel; using System.Threading; using IxIFlow.ActivitySdk;
                                                     namespace Slow;
                                                     public sealed class PauseAttribute : DefaultValueAttribute {
                                                         public PauseAttribute() : base(1) { Thread.Sleep(5000); }
                                                     }
                                                     public sealed class Data { [WorkflowInput, Pause] public int Value {get;set;} }
                                                     """);

        await File.WriteAllBytesAsync(Path.Combine(feed.Folder, "slow.model.1.0.0.nupkg"),
            PublishingFeed.Package("slow.model", new() { ["lib/net10.0/SlowModel.dll"] = bytes }));

        var revision = await store.GetCatalogRevisionAsync();

        var publisher = new WorkflowPublisher(store, new ArtifactProbe(store) { ReadOnly = true },
            new() { NuGetConfigPath = feed.Config, BuildTimeout = TimeSpan.FromMilliseconds(200) });

        var report = await publisher.ValidateAsync(new("Slow", 1, "workflow",
            Yaml("Slow").Replace("System.Object", "Slow.Data") + "\nimports: {catalogs: [slow.model]}"));

        Assert.False(report.IsValid);
        var diagnostic = Assert.Single(report.Diagnostics);
        Assert.Equal("PUB043", diagnostic.Code);
        Assert.Equal(WorkflowPublishStage.Test, diagnostic.Stage);
        Assert.Equal(WorkflowPublishDiagnosticClass.Unavailable, report.FailureClass);
        Assert.Equal(revision, await store.GetCatalogRevisionAsync());
        Assert.Empty(await store.GetPackagesAsync());
    }

    private static WorkflowPublisherOptions ServiceOptions() => new()
    {
        PublicationHeartbeatInterval = TimeSpan.FromMilliseconds(20),
        PublicationStaleAfter = TimeSpan.FromMilliseconds(400)
    };

    public class CatalogProbe : DispatchProxy
    {
        public IWorkflowCatalogStore Target = null!;
        public bool ReadOnly;
        public int Writes;
        public Action<MethodInfo, object?[]?>? Before;

        public static IWorkflowCatalogStore Wrap(IWorkflowCatalogStore target, out CatalogProbe probe)
        {
            var result = Create<IWorkflowCatalogStore, CatalogProbe>();
            probe = (CatalogProbe)result;
            probe.Target = target;

            return result;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(method);
            Before?.Invoke(method, args);

            if (!(method.Name.StartsWith("Get", StringComparison.Ordinal) || method.Name.StartsWith("List", StringComparison.Ordinal) ||
                    method.Name.StartsWith("get_", StringComparison.Ordinal)))
            {
                Interlocked.Increment(ref Writes);

                if (ReadOnly)
                    throw new InvalidOperationException("Unexpected catalog mutation: " + method.Name);
            }

            try
            {
                return method.Invoke(Target, args);
            }
            catch (TargetInvocationException error)
            {
                ExceptionDispatchInfo.Capture(error.InnerException!).Throw();

                throw;
            }
        }
    }

    private sealed class ArtifactProbe(IArtifactStore inner) : IArtifactStore
    {
        public int Puts;
        public bool ReadOnly, GatePut, WrongDigest;
        public Func<ArtifactDigest, Task>? AfterPut;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken token = default)
        {
            Interlocked.Increment(ref Puts);

            if (ReadOnly)
                throw new InvalidOperationException("Unexpected put or age refresh");

            if (GatePut)
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(token);
            }

            var digest = await inner.PutAsync(kind, content, token);

            if (AfterPut != null)
                await AfterPut(digest);

            return WrongDigest ? ArtifactDigest.Of([9]) : digest;
        }

        public ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken token = default) => inner.OpenAsync(digest, token);
        public ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken token = default) => inner.ExistsAsync(digest, token);

        public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken token = default) =>
            throw new InvalidOperationException("Unexpected delete");

        public ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime before, int limit,
            CancellationToken token = default) => throw new InvalidOperationException("Unexpected purge");
    }
}

/// <summary>Real offline packages shared by the new publisher and HTTP orchestration scenarios.</summary>
internal sealed class PublishingFeed : IDisposable
{
    internal string Folder { get; } = Path.Combine(Path.GetTempPath(), "IxIFlow.Publishing", Guid.NewGuid().ToString("N"));
    internal string Config => Path.Combine(Folder, "NuGet.Config");

    internal PublishingFeed()
    {
        Directory.CreateDirectory(Folder);

        File.WriteAllText(Config,
            "<configuration><packageSources><clear/><add key=\"offline\" value=\"" + SecurityElement.Escape(Folder) +
            "\"/></packageSources></configuration>");
    }

    internal static byte[] Emit(string name, string source, byte[]? dependency = null)
    {
        var references = WorkflowCompilation.ReferencePaths().Select(path => Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(path))
            .Cast<Microsoft.CodeAnalysis.MetadataReference>();

        if (dependency != null)
            references = references.Append(Microsoft.CodeAnalysis.MetadataReference.CreateFromImage(dependency));

        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(name,
            [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source)], references,
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));

        using var bytes = new MemoryStream();
        var result = compilation.Emit(bytes);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));

        return bytes.ToArray();
    }

    private static readonly Lazy<(byte[], byte[])> Fixture = new(() =>
    {
        var dependency = Emit("Package.Dependency", "namespace Packaged; public static class Helper { public static int Value => 1; }");

        var activity = Emit("Package.Activities", """
                                                  using IxIFlow.Core; using IxIFlow.ActivitySdk; using System.Threading; using System.Threading.Tasks;
                                                  namespace Packaged;
                                                  public sealed class Data { public int Value {get;set;} }
                                                  [WorkflowActivity("fixture.echo")]
                                                  public sealed class Echo : IAsyncActivity {
                                                      public Task ExecuteAsync(IActivityContext context, CancellationToken token = default) { _ = Helper.Value; return Task.CompletedTask; }
                                                  }
                                                  """, dependency);

        return (
            Package("fixture.activities", new() { ["lib/net10.0/Package.Activities.dll"] = activity },
                "<dependency id=\"fixture.dependency\" version=\"[1.0.0]\"/>"),
            Package("fixture.dependency", new() { ["lib/net10.0/Package.Dependency.dll"] = dependency }));
    });

    internal static (byte[] Activity, byte[] Dependency) Packages() => Fixture.Value;

    internal static byte[] Package(string id, Dictionary<string, byte[]> entries, string dependencies = "", string version = "1.0.0")
    {
        using var bytes = new MemoryStream();

        using (var zip = new System.IO.Compression.ZipArchive(bytes, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            entries = new(entries)
            {
                [id + ".nuspec"] = Encoding.UTF8.GetBytes(
                    $"<package><metadata><id>{id}</id><version>{version}</version><authors>tests</authors><description>publisher fixture</description><dependencies><group targetFramework=\"net10.0\">{dependencies}</group></dependencies></metadata></package>")
            };

            foreach (var (path, content) in entries)
            {
                var entry = zip.CreateEntry(path);
                entry.LastWriteTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open();
                stream.Write(content);
            }
        }

        return bytes.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(Folder))
            Directory.Delete(Folder, true);
    }
}