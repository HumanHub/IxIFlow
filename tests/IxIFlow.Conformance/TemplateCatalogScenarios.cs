using System.Data;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Dapper;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Templates;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "templates")]
[Trait("Spec", "H-03")]
[Trait("Spec", "F-14")]
[Trait("Spec", "F-12")]
public sealed class TemplateCatalogScenarios
{
    [Fact]
    public async Task GuardedTemplateFailuresUseTheInfrastructureChannel()
    {
        var guarded = GuardedStateRepository.Wrap(DispatchProxy.Create<IWorkflowStateRepository, ThrowingStore>());

        var calls = new Func<Task>[]
        {
            () => guarded.GetTemplateVersionAsync("receipt", 1), () => guarded.GetTemplateVersionsAsync(),
            () => guarded.RegisterTemplateVersionAsync(Content(), new InMemoryWorkflowStateRepository()),
            () => guarded.PublishTemplateDefinitionAsync(new(WorkflowDraftKinds.Template, "receipt", 1, Guid.NewGuid(), 1), Content(),
                new InMemoryWorkflowStateRepository()),
            () => guarded.GetPackageHoldersAsync("package", "1.0.0", CatalogScope.Unrestricted)
        };

        foreach (var call in calls)
            Assert.Equal(WorkflowErrorKind.Infrastructure, (await Assert.ThrowsAsync<WorkflowEngineException>(call)).Kind);
    }

    public class ThrowingStore : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => throw new IOException("store unavailable");
    }

    internal static TemplateVersion Content(string key = "receipt", string text = "Hello {{ Name }}",
        IReadOnlyList<WorkflowPackageReference>? packages = null) =>
        new(key, 1, "liquid", TemplateFormat.Html, TemplateVersion.ModelTypeName(typeof(Model)),
            WorkflowTypeIdentity.SavedName(typeof(Model)),
            packages ?? [], "Receipt", "A receipt", [new("de", "Hallo {{ Name }}"), new("", text)]);

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ConcurrentImmutableRegistrationHasOneAuditAndVersionsUseTheGlobalRevision(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var before = await store.GetCatalogRevisionAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(index => store.RegisterTemplateVersionAsync(Content(), store, "actor" + index)));

        Assert.Single(results.Select(item => (item.PublishedAtUtc, item.PublishedBy)).Distinct());
        Assert.Equal(before + 1, await store.GetCatalogRevisionAsync());

        await store.RegisterTemplateVersionAsync(
            TemplateVersion.Create<Model>("receipt", 2, "liquid", TemplateFormat.Text, new TemplateVariant("", "Version two")), store);

        Assert.Equal(new[] { 1, 2 }, (await store.GetTemplateVersionsAsync()).Select(item => item.Version.Version));
        var changes = await store.GetWorkflowVersionChangesAsync(before);
        Assert.Equal(before + 2, changes.Revision);
        Assert.Empty(changes.Versions);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ImmutableRegistrationPreservesTheFirstAuditAndAdvancesRevisionOnce(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var revision = await store.GetCatalogRevisionAsync();
        var first = await store.RegisterTemplateVersionAsync(Content(), store, "first");
        Assert.Equal(DateTimeKind.Utc, first.PublishedAtUtc.Kind);
        Assert.Null(first.PublishedDraft);
        Assert.Equal("first", first.PublishedBy);
        Assert.Equal(revision + 1, await store.GetCatalogRevisionAsync());
        var replay = await store.RegisterTemplateVersionAsync(TemplateJson.Deserialize(TemplateJson.Serialize(Content())), store, "later");
        Assert.Equal(first.PublishedAtUtc, replay.PublishedAtUtc);
        Assert.Equal("first", replay.PublishedBy);
        Assert.Equal(revision + 1, await store.GetCatalogRevisionAsync());

        await Assert.ThrowsAsync<TemplateVersionConflictException>(() =>
            store.RegisterTemplateVersionAsync(Content(text: "different"), store));

        Assert.Equal(TemplateJson.Serialize(first.Version),
            TemplateJson.Serialize((await store.GetTemplateVersionAsync("receipt", 1))!.Version));

        Assert.Single(await store.GetTemplateVersionsAsync("receipt"));
        Assert.Empty(await store.GetTemplateVersionsAsync("missing"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetTemplateVersionAsync("receipt ", 1));
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task FullGenerationPublicationConsumesTheDraftAndKeepsTheWinningReceipt(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var draft = await store.SaveDefinitionAsync(new("receipt", 1, WorkflowDraftKinds.Template, "source"), null);
        var attempt = await store.BeginPublicationAsync(draft.Id, Guid.NewGuid(), "builder", TimeSpan.FromMinutes(1));
        var before = await store.GetCatalogRevisionAsync();
        var entry = await store.PublishTemplateDefinitionAsync(draft.Id, Content(), store, "publisher");
        Assert.Equal(draft.Id, entry.PublishedDraft);
        Assert.Equal("publisher", entry.PublishedBy);
        Assert.Null(await store.GetDefinitionAsync(WorkflowDraftKinds.Template, "receipt", 1));
        Assert.Equal(before + 1, await store.GetCatalogRevisionAsync());
        var retry = await store.PublishTemplateDefinitionAsync(draft.Id, Content(text: "losing build"), store, "loser");
        Assert.Equal(TemplateJson.Serialize(entry.Version), TemplateJson.Serialize(retry.Version));
        Assert.Equal(entry.PublishedAtUtc, retry.PublishedAtUtc);
        Assert.Equal(before + 1, await store.GetCatalogRevisionAsync());

        await Assert.ThrowsAsync<WorkflowEngineException>(() =>
            store.SaveDefinitionAsync(new("receipt", 1, WorkflowDraftKinds.Template, "again"), null));

        var completed = await store.CompletePublicationAsync(attempt.Id, attempt.HostId,
            new(new(draft.Id, JsonSerializer.Serialize(entry)), [], null));

        Assert.Equal(WorkflowPublicationState.Published, completed.State);
        Assert.Equal(draft.Id, completed.Outcome!.Receipt!.Draft);
        Assert.Equal(completed.Id, (await store.BeginPublicationAsync(draft.Id, Guid.NewGuid(), null, TimeSpan.FromMinutes(1))).Id);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task RecreatedDraftAndCodeRegistrationCannotBeConsumedByAnOldPublisher(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var old = await store.SaveDefinitionAsync(new("receipt", 1, WorkflowDraftKinds.Template, "old"), null);
        await store.DeleteDefinitionAsync(old.Id);
        var current = await store.SaveDefinitionAsync(new("receipt", 1, WorkflowDraftKinds.Template, "new"), null);
        var revision = await store.GetCatalogRevisionAsync();
        await Assert.ThrowsAsync<CatalogPreconditionException>(() => store.PublishTemplateDefinitionAsync(old.Id, Content(), store));
        Assert.Equal(revision, await store.GetCatalogRevisionAsync());
        Assert.Equal(current.Id, (await store.GetDefinitionAsync(current.Kind, current.Name, current.Version))!.Id);
        await store.RegisterTemplateVersionAsync(Content(), store);

        await Assert.ThrowsAsync<TemplateVersionConflictException>(() =>
            store.PublishTemplateDefinitionAsync(current.Id, Content(), store));

        Assert.NotNull(await store.GetDefinitionAsync(current.Kind, current.Name, current.Version));
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task RequiredPackageArtifactsAndCompleteModelClosuresAreCheckedBeforeWriting(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var dependency = await Package(store, "model.dependency", []);
        var model = await Package(store, "model.root", [new(dependency.Id, dependency.Version, dependency.Digest)]);
        var root = new WorkflowPackageReference(model.Id, model.Version, model.Digest);
        var pin = new WorkflowPackageReference(dependency.Id, dependency.Version, dependency.Digest);
        var revision = await store.GetCatalogRevisionAsync();
        await Assert.ThrowsAsync<WorkflowEngineException>(() => store.RegisterTemplateVersionAsync(Content(packages: [root]), store));
        await store.DeleteAsync(pin.Digest);
        await Assert.ThrowsAsync<WorkflowEngineException>(() => store.RegisterTemplateVersionAsync(Content(packages: [root, pin]), store));
        Assert.Equal(revision, await store.GetCatalogRevisionAsync());
        Assert.Empty(await store.GetTemplateVersionsAsync());
        await store.PutAsync(ArtifactKind.Package, new MemoryStream(Encoding.UTF8.GetBytes(dependency.Id)));
        var content = Content(packages: [root, pin]);
        await store.RegisterTemplateVersionAsync(content, store);

        Assert.Equal(new[] { "model.dependency", "model.root" },
            (await store.GetTemplateVersionAsync("receipt", 1))!.Version.ModelPackages.Select(item => item.Id));
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task TemplateHoldersRetainTheEntireClosureAndWithdrawalOnlyRefusesNewAuthoring(string profile)
    {
        var time = new FakeTimeProvider();
        await using var database = await StoreProfiles.CreateAsync(profile, time);
        var store = database.NewRepository();
        var dep = await Package(store, "held.dependency", []);
        var root = await Package(store, "held.model", [new(dep.Id, dep.Version, dep.Digest)]);

        var pins = new[]
        {
            new WorkflowPackageReference(root.Id, root.Version, root.Digest), new WorkflowPackageReference(dep.Id, dep.Version, dep.Digest)
        };

        await store.RegisterTemplateVersionAsync(Content(packages: pins), store);
        await WithdrawFixture(database, store, root);

        var refused = await Assert.ThrowsAsync<WorkflowEngineException>(() =>
            store.RegisterTemplateVersionAsync(Content("another", packages: pins), store));

        Assert.Equal(WorkflowErrorKind.InvalidState, refused.Kind);
        Assert.Contains("withdrawn", refused.Message);

        // Existing immutable registration and consumers survive withdrawal.
        await store.RegisterTemplateVersionAsync(Content(packages: pins), store);
        Assert.NotNull(await store.GetTemplateVersionAsync("receipt", 1));

        Assert.Equal("Hello Ana", (await new TemplateRenderer(store, new([new IxIFlow.Templates.Liquid.LiquidTemplateEngine()]))
            .RenderAsync(new("receipt", 1), new Model(), null)).Content);

        var holders = await store.GetPackageHoldersAsync(dep.Id, dep.Version, CatalogScope.Unrestricted);
        Assert.Equal("receipt", Assert.Single(holders.Templates).Key);
        Assert.Equal(root.Id, Assert.Single(holders.Packages).Id);
        var scoped = await store.GetPackageHoldersAsync(dep.Id, dep.Version, new(new(), new(), new([dep.Id]), new([])));
        Assert.Empty(scoped.Templates);
        Assert.Empty(scoped.Packages);
        Assert.True(scoped.OutsideScope);
        var unrelated = await store.PutAsync(ArtifactKind.Bundle, new MemoryStream([99]));

        if (database.Profile is StoreProfiles.LocalMemory or StoreProfiles.LocalSqlite)
            time.Advance(TimeSpan.FromDays(2));
        else
        {
            await using var connection = database.NewConnection();

            await connection.ExecuteAsync(database.Profile == StoreProfiles.DistributedSqlServer
                ? "UPDATE dbo.IxIFlowArtifacts SET CreatedAtUtc=DATEADD(DAY,-2,SYSUTCDATETIME())"
                : "UPDATE ixiflow_artifacts SET created_at_utc=clock_timestamp()-INTERVAL '2 days'");
        }

        await store.PurgeArtifactsAsync(store, TimeSpan.FromDays(1), 100);
        Assert.True(await store.ExistsAsync(root.Digest));
        Assert.True(await store.ExistsAsync(dep.Digest));
        Assert.False(await store.ExistsAsync(unrelated));
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task GuardedRepositoryAndCatalogForwardAllTemplateOperations(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var guarded = GuardedStateRepository.Wrap(store);
        using var catalog = new WorkflowCatalog(guarded);
        await catalog.RegisterTemplateVersionAsync(Content(), store);
        Assert.Single(await guarded.GetTemplateVersionsAsync());
        Assert.NotNull(await catalog.GetTemplateVersionAsync("receipt", 1));
        var draft = await guarded.SaveDefinitionAsync(new("other", 1, WorkflowDraftKinds.Template, "other"), null);
        Assert.Equal(draft.Id, (await catalog.PublishTemplateDefinitionAsync(draft.Id, Content("other"), store)).PublishedDraft);
        Assert.Empty((await catalog.GetPackageHoldersAsync("missing", "1.0.0", CatalogScope.Unrestricted)).Templates);
    }

    internal static async Task<WorkflowPackage> Package(IWorkflowStateRepository store, string id,
        IReadOnlyList<WorkflowPackageReference> closure)
    {
        var digest = await store.PutAsync(ArtifactKind.Package, new MemoryStream(Encoding.UTF8.GetBytes(id)));
        var package = new WorkflowPackage(id, "1.0.0", digest, new Dictionary<string, ArtifactDigest> { [id + ".nuspec"] = digest }, []);

        return (await store.RegisterPackageAsync(package, new([], PackageExtraction.Indexed, null, closure, []), store)).Stored;
    }

    internal static async Task WithdrawFixture(ProfileStore database, IWorkflowStateRepository store, WorkflowPackage package)
    {
        // Seed an already committed withdrawn package to exercise catalog reads.
        var withdrawn = package with
        {
            Withdrawn = true, WithdrawnAtUtc = new DateTime(DateTime.UtcNow.Ticks / 10 * 10, DateTimeKind.Utc), WithdrawnBy = "owner"
        };

        if (store is InMemoryWorkflowStateRepository memory)
        {
            var rows = (Dictionary<(string, string), WorkflowPackage>)typeof(InMemoryWorkflowStateRepository).GetField("_packages",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(memory)!;

            rows[(package.Id, package.Version)] = withdrawn;
        }
        else
        {
            await using var connection = database.NewConnection();

            await connection.ExecuteAsync(database.Profile == StoreProfiles.DistributedSqlServer
                    ? "UPDATE dbo.IxIFlowPackages SET Withdrawn=@Withdrawn,WithdrawnAtUtc=@WithdrawnAtUtc,WithdrawnBy=@WithdrawnBy,MetadataJson=@Json WHERE Id=@Id AND Version=@Version"
                    : "UPDATE ixiflow_packages SET withdrawn=@Withdrawn,withdrawn_at_utc=@WithdrawnAtUtc,withdrawn_by=@WithdrawnBy,metadata_json=@Json WHERE id=@Id AND version=@Version",
                WithdrawalParameters(database.Profile, withdrawn));
        }
    }

    internal static DynamicParameters WithdrawalParameters(string profile, WorkflowPackage withdrawn)
    {
        var parameters = new DynamicParameters(new
        {
            withdrawn.Id, withdrawn.Version, withdrawn.Withdrawn,
            withdrawn.WithdrawnBy, Json = JsonSerializer.Serialize(withdrawn)
        });

        // Match the product's WithTimes binding: SQL datetime would round the timestamp
        // while MetadataJson retains its precision, making the committed fixture inconsistent.
        parameters.Add("WithdrawnAtUtc", profile == StoreProfiles.LocalSqlite
            ? (object)withdrawn.WithdrawnAtUtc!.Value.ToString("yyyy-MM-dd HH:mm:ss.fffffff",
                System.Globalization.CultureInfo.InvariantCulture)
            : withdrawn.WithdrawnAtUtc!, profile == StoreProfiles.DistributedSqlServer ? DbType.DateTime2 : null);

        return parameters;
    }

    public sealed class Model
    {
        public string Name { get; set; } = "Ana";
    }
}