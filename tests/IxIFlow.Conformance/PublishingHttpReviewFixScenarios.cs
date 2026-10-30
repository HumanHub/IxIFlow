using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using IxIFlow.Authoring;
using IxIFlow.Core;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

public sealed partial class PublishingHttpScenarios
{
    private static readonly Lazy<byte[]> HttpScopePackage = new(() => PublishingFeed.Package("http.scope", new()
    {
        ["lib/net10.0/Http.Scope.dll"] = PublishingFeed.Emit("Http.Scope", """
                                                                           using IxIFlow.Core; using IxIFlow.ActivitySdk; using System.Threading; using System.Threading.Tasks;
                                                                           [WorkflowActivity("http.scope", Version = "1"), ScopeThreading(ScopeThreading.ThreadAffine)]
                                                                           public sealed class Container : IScopeActivity<object>
                                                                           {
                                                                               public Task<object> OpenAsync(ScopeOpening opening, IScopeContext context, CancellationToken token) => Task.FromResult(new object());
                                                                               public Task CloseAsync(object value, ScopeClosing closing, IScopeContext context, CancellationToken token) => Task.CompletedTask;
                                                                           }
                                                                           """)
    }));

    // Like the neighbouring HTTP scenarios, every regression declares StoreProfiles.All
    // (or its cross product). Local verification selects memory and SQLite.
    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task UploadAuthorizesTheStoredWithdrawnPackageBeforeAnyWrite(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var draft = await store.SaveDefinitionAsync(new("acme.http", 1, "activity", ActivityYaml), null, "author");
        var published = await new WorkflowPublisher(store, store).PublishAsync(draft.Id, "publisher");
        Assert.True(published.Success, string.Join("; ", published.Diagnostics));
        var package = published.Package!;
        await store.SetPackageWithdrawnAsync(package.Id, package.Version, true, new(package.LifecycleRevision), "withdrawer");
        var before = (await store.GetPackageAsync(package.Id, package.Version))!;
        var revision = await store.GetCatalogRevisionAsync();
        byte[] bytes;

        await using (var content = await store.OpenAsync(package.Digest))
        {
            Assert.NotNull(content);
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer);
            bytes = buffer.ToArray();
        }

        var artifacts = new ArtifactProbe(store);
        var authorized = new List<WorkflowPackage>();

        await using var factory = new PublishingHttpFactory(store)
        {
            Artifacts = artifacts,
            ResourceAllowed = resource =>
            {
                if (resource is not WorkflowPackage row)
                    return true;

                authorized.Add(row);

                return !row.Withdrawn;
            }
        };

        using var client = factory.Operator();
        using var denied = await client.PostAsync("/pub/catalog/packages", new ByteArrayContent(bytes));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(2, authorized.Count);
        Assert.Null(authorized[0].PublishedDraft);
        Assert.False(authorized[0].Withdrawn);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(authorized[1]));
        Assert.Equal(draft.Id, authorized[1].PublishedDraft);
        Assert.Equal("publisher", authorized[1].PublishedBy);
        Assert.Equal("withdrawer", authorized[1].WithdrawnBy);
        factory.Scope.Value = new(new(), new(), new([]), new());
        authorized.Clear();
        using var hidden = await client.PostAsync("/pub/catalog/packages", new ByteArrayContent(bytes));
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Empty(authorized);
        Assert.Equal(0, artifacts.Puts);
        Assert.Equal(revision, await store.GetCatalogRevisionAsync());
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await store.GetPackageAsync(package.Id, package.Version)));
        await using var unchanged = await store.OpenAsync(package.Digest);
        Assert.NotNull(unchanged);
        using var after = new MemoryStream();
        await unchanged.CopyToAsync(after);
        Assert.Equal(bytes, after.ToArray());
    }

    public static IEnumerable<object[]> ExistingPublicationTargets => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { "workflow", "activity", "template" }.Select(kind => new object[] { profile, kind }));

    [Theory, MemberData(nameof(ExistingPublicationTargets))]
    public async Task PublicationAuthorizesTheExistingTargetBeforeStartingAnAttempt(string profile, string kind)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        WorkflowDraft draft;

        if (kind == "workflow")
        {
            draft = await store.SaveDefinitionAsync(new("Published", 1, kind, TypedPublishingScenarios.Yaml("Published")), null);
            await Seed(store);
        }
        else if (kind == "activity")
        {
            draft = await store.SaveDefinitionAsync(new("acme.http", 1, kind, ActivityYaml), null);

            var package =
                (await new WorkflowPublisher(store, store).UploadPackageAsync(new MemoryStream(PublishingFeed.Package("acme.http", []))))
                .Stored;

            await store.SetPackageWithdrawnAsync(package.Id, package.Version, true, new(package.LifecycleRevision), "protected");
        }
        else
        {
            draft = await store.SaveDefinitionAsync(new("notice", 1, kind, "template: {key: notice, version: 1}"), null);

            var version = IxIFlow.Templates.TemplateVersion.Create<TemplateCatalogScenarios.Model>("notice", 1, "liquid",
                IxIFlow.Templates.TemplateFormat.Text,
                new IxIFlow.Templates.TemplateVariant("", "protected"));

            await store.RegisterTemplateVersionAsync(version, store);
        }

        var artifacts = new ArtifactProbe(store);
        object? deniedResource = null;

        await using var factory = new PublishingHttpFactory(store)
        {
            Artifacts = artifacts,
            ResourceAllowed = resource =>
            {
                if (resource is not (WorkflowVersion or WorkflowPackage or IxIFlow.Templates.TemplateVersionEntry))
                    return true;

                deniedResource = resource;

                return false;
            }
        };

        factory.Options.PublishWait = TimeSpan.FromSeconds(10);
        using var client = factory.Operator();
        var revision = await store.GetCatalogRevisionAsync();
        using var response = await client.SendAsync(Request("POST", $"/pub/drafts/{kind}/{draft.Name}/1/publish", Tag(draft.Id)));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(deniedResource);
        Assert.Equal(0, artifacts.Puts);
        Assert.Equal(revision, await store.GetCatalogRevisionAsync());
        Assert.Equal(draft.Id, (await store.GetDefinitionAsync(kind, draft.Name, 1))!.Id);
        Assert.Empty((await store.GetPublicationsAsync(new(CatalogScope.Unrestricted, kind, draft.Name, 1))).Items);
    }

    public static IEnumerable<object[]> DescriptorJsonCases => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { false, true }.Select(converter => new object[] { profile, converter }));

    [Theory, MemberData(nameof(DescriptorJsonCases))]
    public async Task NestedScopeEnumsUsePublishingStringsAcrossReadsAndUploadReceipts(string profile, bool applicationConverter)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var bytes = HttpScopePackage.Value;

        await using var factory = new PublishingHttpFactory(store)
        {
            ConfigureServices = services =>
            {
                if (applicationConverter)
                    services.ConfigureHttpJsonOptions(options =>
                        options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
            }
        };

        using var client = factory.Operator();

        void Check(JsonElement value)
        {
            var scope = value.GetProperty("scope");
            Assert.Equal("Serialized", scope.GetProperty("concurrency").GetString());
            Assert.Equal("ThreadAffine", scope.GetProperty("threading").GetString());
        }

        foreach (var status in new[] { HttpStatusCode.Created, HttpStatusCode.OK })
        {
            using var upload = await client.PostAsync("/pub/catalog/packages", new ByteArrayContent(bytes));
            Assert.Equal(status, upload.StatusCode);

            Check(Assert.Single((await Read(upload)).GetProperty("package").GetProperty("activities").GetProperty("activities")
                .EnumerateArray()));
        }

        foreach (var path in new[]
                 {
                     "/pub/catalog/activities", "/pub/catalog/activities/http.scope/versions",
                     "/pub/catalog/packages/http.scope/1.0.0/activities"
                 })
            Check(Assert.Single((await Read(await client.GetAsync(path))).GetProperty("items").EnumerateArray()).GetProperty("descriptor"));

        Check((await Read(await client.GetAsync(
                "/pub/catalog/activities/http.scope/versions/1?packageId=http.scope&packageVersion=1.0.0&build=" +
                ArtifactDigest.Of(bytes).Value)))
            .GetProperty("activity").GetProperty("descriptor"));

        Check(Assert.Single((await Read(await client.GetAsync("/pub/catalog/packages/http.scope/1.0.0")))
            .GetProperty("activities").GetProperty("activities").EnumerateArray()));
    }

    public static IEnumerable<object[]> GroupedLocationCases => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { "", "/base" }.Select(pathBase => new object[] { profile, pathBase }));

    [Theory, MemberData(nameof(GroupedLocationCases))]
    public async Task LocationsAndFeedLinksIncludeEnclosingGroupsAndPathBase(string profile, string pathBase)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();

        await using var factory = new PublishingHttpFactory(store)
            { GroupPrefix = "/api", PathBase = pathBase.Length == 0 ? null : pathBase };

        using var client = factory.Operator();
        var root = pathBase + "/api/pub";
        var path = root + "/drafts/workflow/Work/1";
        using var save = Request("PUT", path, content: Yaml());
        save.Headers.TryAddWithoutValidation("If-None-Match", "*");
        using var saved = await client.SendAsync(save);
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        Assert.Equal(path, saved.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(saved.Headers.Location)).StatusCode);
        using var published = await client.SendAsync(Request("POST", path + "/publish", saved.Headers.ETag!.ToString()));
        Assert.True(published.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted, await published.Content.ReadAsStringAsync());
        Assert.StartsWith(root + "/publications/", published.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(published.Headers.Location)).StatusCode);
        var bytes = PublishingFeed.Package("http.grouped", []);

        foreach (var status in new[] { HttpStatusCode.Created, HttpStatusCode.OK })
        {
            using var upload = await client.PostAsync(root + "/catalog/packages", new ByteArrayContent(bytes));
            Assert.Equal(status, upload.StatusCode);
            Assert.Equal(root + "/catalog/packages/http.grouped/1.0.0", upload.Headers.Location!.ToString());
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(upload.Headers.Location)).StatusCode);
        }

        var feed = await Read(await client.GetAsync(root + "/nuget/index.json"));
        var address = feed.GetProperty("resources")[0].GetProperty("@id").GetString();
        Assert.EndsWith(root + "/nuget/flatcontainer/", address);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(address + "http.grouped/index.json")).StatusCode);
    }
}