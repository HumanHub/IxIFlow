using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using IxIFlow.ActivitySdk;
using IxIFlow.AspNetCore;
using IxIFlow.Authoring;
using IxIFlow.Builders;
using IxIFlow.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "authoring"), Trait("Spec", "F-09"), Trait("Spec", "F-14")]
public sealed partial class PublishingHttpScenarios
{
    private const string DraftPath = "/pub/drafts/workflow/Work/1";
    private static StringContent Yaml(string name = "Work") => new(TypedPublishingScenarios.Yaml(name), Encoding.UTF8, "application/yaml");
    private static HttpRequestMessage Request(string method, string path, string? tag = null, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = content };
        if (tag != null) request.Headers.TryAddWithoutValidation("If-Match", tag);
        return request;
    }
    private static string Tag(WorkflowDraftId id) => $"\"d-{id.Generation:N}-{id.Revision}\"";
    private static async Task<JsonElement> Read(HttpResponseMessage response) => JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
    private static async Task<(WorkflowDraft Draft, WorkflowVersion Version, WorkflowPackage Package, ActivityDescriptor Activity)> Seed(IWorkflowStateRepository store)
    {
        var draft = await store.SaveDefinitionAsync(new("Work", 1, "workflow", TypedPublishingScenarios.Yaml("Work")), null, "original");
        var definition = Workflow.Create<object>("Published").Delay(TimeSpan.FromHours(1)).Build();
        var contract = WorkflowDefinitionValidator.GetContract(definition);
        var version = new WorkflowVersion("Published", 1, WorkflowDefinitionValidator.Inspect(definition), contract.ToJson(), contract.Hash, WorkflowDescriptor.Of(definition), []);
        await store.RegisterWorkflowVersionAsync(version);
        var bytes = PublishingFeed.Package("http.package", []);
        var candidate = await new WorkflowPublisher(store, store).ReadPackageAsync(new MemoryStream(bytes));
        await store.PutAsync(ArtifactKind.Package, new MemoryStream(bytes));
        var descriptor = ActivityDescriptor.OfType(typeof(Echo), "http.package", "1.0.0");
        await store.RegisterPackageAsync(candidate.Package, new([descriptor], PackageExtraction.Indexed, null, [], []), store);
        return (draft, version, candidate.Package, descriptor);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task EveryMappedRouteRequiresAuthenticationAndItsOwnPolicy(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository(); var seeded = await Seed(store);
        await store.SaveDefinitionAsync(new("notice", 1, "template", "template: {key: notice, version: 1}"), null);
        await store.RegisterTemplateVersionAsync(IxIFlow.Templates.TemplateVersion.Create<TemplateCatalogScenarios.Model>("published", 1, "liquid", IxIFlow.Templates.TemplateFormat.Text, new IxIFlow.Templates.TemplateVariant("", "text")), store);
        var attempt = await store.BeginPublicationAsync(seeded.Draft.Id, Guid.NewGuid(), "first", TimeSpan.FromMinutes(1));
        await store.CompletePublicationAsync(attempt.Id, attempt.HostId, new(null, [], WorkflowPublishDiagnosticClass.Authoring));
        await using var factory = new PublishingHttpFactory(store);
        using var authenticated = factory.Operator();
        var routes = new (string Method, string Path, string Policy)[]
        {
            ("GET", "/drafts", "read"), ("GET", "/drafts/workflow/Work/1", "read"),
            ("GET", "/drafts/workflow/Work/1/source", "download"), ("PUT", "/drafts/workflow/Work/1", "author"),
            ("DELETE", "/drafts/workflow/Work/1", "author"), ("POST", "/drafts/workflow/Work/1/validate", "execute"),
            ("POST", "/drafts/workflow/Work/1/publish", "publish"), ("GET", "/drafts/workflow/Work/1/publications", "read"),
            ("GET", "/publications/" + attempt.Id, "read"), ("GET", "/catalog/workflows", "read"),
            ("GET", "/catalog/workflows/Published/versions", "read"), ("GET", "/catalog/workflows/Published/versions/1", "read"),
            ("GET", "/catalog/workflows/Published/versions/1/hosts", "read"), ("GET", "/catalog/workflows/Published/versions/1/source", "download"),
            ("GET", "/catalog/workflows/Published/versions/1/bundle", "download"), ("POST", "/catalog/workflows/Published/versions/1/deprecate", "lifecycle"),
            ("POST", "/catalog/workflows/Published/versions/1/retire", "lifecycle"), ("GET", "/catalog/packages", "read"),
            ("POST", "/catalog/packages", "publish"), ("GET", "/catalog/packages/http.package/versions", "read"),
            ("GET", "/catalog/packages/http.package/1.0.0", "read"), ("GET", "/catalog/packages/http.package/1.0.0/activities", "read"),
            ("GET", "/catalog/packages/http.package/1.0.0/hosts", "read"), ("GET", "/catalog/packages/http.package/1.0.0/nupkg", "download"),
            ("GET", "/catalog/activities", "read"), ("GET", "/catalog/activities/http.echo/versions", "read"),
            ("GET", $"/catalog/activities/http.echo/versions/1?packageId=http.package&packageVersion=1.0.0&build={seeded.Package.Digest.Value}", "read"),
            ("GET", "/schemas/workflow", "read"),
            ("POST", "/drafts/template/notice/1/preview", "preview"), ("GET", "/catalog/templates", "read"),
            ("GET", "/catalog/templates/published/versions", "read"), ("GET", "/catalog/templates/published/versions/1", "read"),
            ("GET", "/catalog/templates/published/versions/1/source", "download"), ("GET", "/catalog/steps", "read"),
            ("GET", "/catalog/packages/http.package/1.0.0/source", "download"), ("GET", "/catalog/packages/http.package/1.0.0/holders", "lifecycle"),
            ("DELETE", "/catalog/packages/http.package/1.0.0", "lifecycle"), ("GET", "/nuget/index.json", "download"),
            ("GET", "/nuget/flatcontainer/http.package/index.json", "download"),
            ("GET", "/nuget/flatcontainer/http.package/1.0.0/http.package.1.0.0.nupkg", "download")
        };
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<WorkflowPublishingEndpoint>() != null).ToArray();
        Assert.Equal(routes.Length, endpoints.Length);
        foreach (var route in routes)
        {
            HttpRequestMessage Make() => Request(route.Method, "/pub" + route.Path,
                route.Policy == "lifecycle" ? "\"l-0\"" : Tag(seeded.Draft.Id),
                route.Method == "PUT" ? Yaml() : route.Method == "POST" && route.Path == "/catalog/packages" ? new ByteArrayContent(PublishingFeed.Package("http.upload", [])) : null);
            using var anonymous = factory.CreateClient();
            using var unauthorized = await anonymous.SendAsync(Make());
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            using var client = factory.Operator(string.Join(' ', PublishingHttpFactory.Policies.Except([route.Policy])));
            using var denied = await client.SendAsync(Make());
            Assert.True(denied.StatusCode == HttpStatusCode.Forbidden, route.Method + " " + route.Path + ": " + await denied.Content.ReadAsStringAsync());
        }
        Assert.Equal(seeded.Draft.Id, (await store.GetDefinitionAsync("workflow", "Work", 1))!.Id);
        Assert.Single(await store.GetPackagesAsync());
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task MapRequiresScopeEveryPolicyAndPublishingServices(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile); var store = database.NewRepository();
        await using (var missing = new PublishingHttpFactory(store) { RegisterScope = false })
            Assert.Contains("ICatalogQueryScope", Assert.Throws<WorkflowEngineException>(() => missing.CreateClient()).Message);
        foreach (var property in typeof(WorkflowPublishingOptions).GetProperties().Where(property => property.Name.EndsWith("Policy") && property.Name != "ActForPolicy"))
        {
            await using var missing = new PublishingHttpFactory(store);
            property.SetValue(missing.Options, "unknown");
            Assert.Throws<WorkflowEngineException>(() => missing.CreateClient());
        }
        foreach (var type in new[] { typeof(WorkflowPublisher), typeof(WorkflowPublisherOptions), typeof(PublicationService), typeof(PackageIndexingService),
            typeof(WorkflowPackageUploadService), typeof(IWorkflowCatalogStore), typeof(IWorkflowCatalog), typeof(IArtifactStore), typeof(WorkflowHost) })
        {
            await using var missing = new PublishingHttpFactory(store) { ConfigureServices = services => services.RemoveAll(type) };
            Assert.Throws<WorkflowEngineException>(() => missing.CreateClient());
        }
        await using var invalid = new PublishingHttpFactory(store) { PublisherOptions = new() { PackageReadLimits = new() { MaxArchiveBytes = 0 } } };
        Assert.Throws<WorkflowEngineException>(() => invalid.CreateClient());
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ProposedDraftAndUploadDenialsPrecedeAllWritesAndScopeHidesExistingResources(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile); var store = database.NewRepository();
        await using var factory = new PublishingHttpFactory(store) { ResourceAllowed = _ => false };
        using var client = factory.Operator(); var before = await store.GetCatalogRevisionAsync();
        using var create = Request("PUT", DraftPath, content: Yaml()); create.Headers.TryAddWithoutValidation("If-None-Match", "*");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(create)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/pub/catalog/packages", new ByteArrayContent(PublishingFeed.Package("http.denied", [])))).StatusCode);
        Assert.Equal(before, await store.GetCatalogRevisionAsync()); Assert.Empty(await store.GetPackagesAsync());
        Assert.Null(await store.GetDefinitionAsync("workflow", "Work", 1));
        await Seed(store);
        factory.Scope.Value = new(new([]), new([]), new([]), new([]));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(DraftPath)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/pub/catalog/packages/http.package/1.0.0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/pub/catalog/workflows/Published/versions/1")).StatusCode);
        factory.Scope.Value = CatalogScope.Unrestricted;
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(DraftPath)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/pub/catalog/workflows/Published/versions/1")).StatusCode);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task MissingPreconditionsAre428(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile); var store = database.NewRepository(); await Seed(store);
        await using var factory = new PublishingHttpFactory(store); using var client = factory.Operator();
        foreach (var route in new[] { ("PUT", DraftPath), ("DELETE", DraftPath), ("POST", DraftPath + "/publish"),
            ("POST", "/pub/catalog/workflows/Published/versions/1/deprecate"), ("POST", "/pub/catalog/workflows/Published/versions/1/retire") })
            Assert.Equal((HttpStatusCode)428, (await client.SendAsync(Request(route.Item1, route.Item2))).StatusCode);
    }
    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task MalformedPreconditionsAre400(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile); var store = database.NewRepository(); await Seed(store);
        await using var factory = new PublishingHttpFactory(store); using var client = factory.Operator();
        foreach (var tag in new[] { "*", "W/\"d-00000000000000000000000000000000-1\"", "\"1\"", "\"d-no-1\"", "\"l-0\",\"l-1\"" })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Request("DELETE", DraftPath, tag))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Request("POST", "/pub/catalog/workflows/Published/versions/1/retire", tag))).StatusCode);
        }
    }
    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task AtomicStalePreconditionsAre412AndGenerationCannotBeReused(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile); var store = database.NewRepository(); var seeded = await Seed(store);
        await using var factory = new PublishingHttpFactory(store); using var client = factory.Operator();
        var newer = await store.SaveDefinitionAsync(seeded.Draft, seeded.Draft.Id);
        foreach (var route in new[] { ("PUT", DraftPath), ("DELETE", DraftPath), ("POST", DraftPath + "/publish") })
            Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(Request(route.Item1, route.Item2, Tag(seeded.Draft.Id), route.Item1 == "PUT" ? Yaml() : null))).StatusCode);
        using var detail = await client.GetAsync("/pub/catalog/workflows/Published/versions/1"); var oldTag = detail.Headers.ETag!.ToString();
        var first = await client.SendAsync(Request("POST", "/pub/catalog/workflows/Published/versions/1/deprecate", oldTag)); first.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(Request("POST", "/pub/catalog/workflows/Published/versions/1/retire", oldTag))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(Request("DELETE", DraftPath, Tag(newer.Id)))).StatusCode);
        using var create = Request("PUT", DraftPath, content: Yaml()); create.Headers.TryAddWithoutValidation("If-None-Match", "*");
        var replacement = await client.SendAsync(create); Assert.Equal(HttpStatusCode.Created, replacement.StatusCode);
        Assert.NotEqual(newer.Generation, (await store.GetDefinitionAsync("workflow", "Work", 1))!.Generation);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(Request("DELETE", DraftPath, Tag(newer.Id)))).StatusCode);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task DraftMetadataVerbatimSourceActorAndStringDiagnosticsUseSeparatePolicies(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile); var store = database.NewRepository();
        await using var factory = new PublishingHttpFactory(store); using var client = factory.Operator();
        using var create = Request("PUT", DraftPath, content: Yaml()); create.Headers.TryAddWithoutValidation("If-None-Match", "*"); create.Headers.Add("X-IxIFlow-Actor", "delegate");
        var saved = await client.SendAsync(create); Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        var json = await Read(saved); Assert.Equal("delegate", json.GetProperty("draft").GetProperty("updatedBy").GetString());
        Assert.DoesNotContain("source\"", json.GetRawText(), StringComparison.OrdinalIgnoreCase);
        var source = await client.GetAsync(DraftPath + "/source"); Assert.Equal(TypedPublishingScenarios.Yaml("Work"), await source.Content.ReadAsStringAsync());
        Assert.Equal(saved.Headers.ETag, source.Headers.ETag);
        using var readOnly = factory.Operator("read"); Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.GetAsync(DraftPath + "/source")).StatusCode);
        var before = await store.GetCatalogRevisionAsync();
        var validation = await client.PostAsync(DraftPath + "/validate", new StringContent("workflow: {name: Work, version: 1, dataType: Missing.Type}\ndefinitions: []", Encoding.UTF8, "application/yaml"));
        Assert.Equal(HttpStatusCode.OK, validation.StatusCode); json = await Read(validation); Assert.False(json.GetProperty("isValid").GetBoolean());
        var diagnostic = json.GetProperty("diagnostics")[0];
        foreach (var field in new[] { "class", "stage", "severity" }) Assert.Equal(JsonValueKind.String, diagnostic.GetProperty(field).ValueKind);
        Assert.Equal(before, await store.GetCatalogRevisionAsync());
        using var noActor = factory.Operator("author");
        using var update = Request("PUT", DraftPath, saved.Headers.ETag!.ToString(), Yaml()); update.Headers.Add("X-IxIFlow-Actor", "forbidden");
        Assert.Equal(HttpStatusCode.Forbidden, (await noActor.SendAsync(update)).StatusCode);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task AttemptsResolveAcrossFactoriesAfterDraftConsumptionAndRetriesHaveNewIds(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile); var store = database.NewRepository();
        var draft = await store.SaveDefinitionAsync(new("Work", 1, "workflow", TypedPublishingScenarios.Yaml("Work")), null);
        var failed = await store.BeginPublicationAsync(draft.Id, Guid.NewGuid(), "failed-author", TimeSpan.FromMinutes(1));
        await store.CompletePublicationAsync(failed.Id, failed.HostId, new(null, [new("Compile", "CS0001", "Curated compiler failure", "Error", "workflow.yaml", 1, 1, WorkflowPublishDiagnosticClass.Authoring)], WorkflowPublishDiagnosticClass.Authoring));
        await using var one = new PublishingHttpFactory(store); await using var two = new PublishingHttpFactory(store);
        using var first = one.Operator(); using var second = two.Operator();
        var accepted = await first.SendAsync(Request("POST", DraftPath + "/publish", Tag(draft.Id)));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode); Assert.Equal(TimeSpan.FromSeconds(5), accepted.Headers.RetryAfter!.Delta);
        var location = accepted.Headers.Location!.ToString();
        var running = await Read(accepted); Assert.Equal("Running", running.GetProperty("state").GetString()); Assert.Equal(2, running.GetProperty("attempt").GetInt32());
        var outcome = await Until(second, location);
        Assert.Equal("Published", outcome.GetProperty("state").GetString()); Assert.Equal("Published", outcome.GetProperty("workflow").GetProperty("summary").GetProperty("state").GetString());
        Assert.Equal("operator", outcome.GetProperty("workflow").GetProperty("publishedBy").GetString());
        Assert.Null(await store.GetDefinitionAsync("workflow", "Work", 1));
        var history = await Read(await second.GetAsync(DraftPath + "/publications"));
        Assert.Equal(2, history.GetProperty("items").GetArrayLength()); Assert.DoesNotContain("receiptJson", history.GetRawText(), StringComparison.OrdinalIgnoreCase);
        var prior = await Read(await second.GetAsync("/pub/publications/" + failed.Id)); Assert.Equal("Failed", prior.GetProperty("state").GetString());
        Assert.Equal("CS0001", prior.GetProperty("diagnostics")[0].GetProperty("code").GetString());
        var retry = await second.SendAsync(Request("POST", DraftPath + "/publish", Tag(draft.Id)));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode); Assert.Equal(location, retry.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/pub/catalog/workflows/Work/versions/1")).StatusCode);
    }
    private static async Task<JsonElement> Until(HttpClient client, string location)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (true)
        {
            using var response = await client.GetAsync(location, timeout.Token); response.EnsureSuccessStatusCode();
            var result = await Read(response); if (result.GetProperty("state").GetString() != "Running") return result;
            await Task.Delay(30, timeout.Token);
        }
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ScopeAndQueryBoundValidatorsNeverCacheAvailabilityOrHosts(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile); var store = database.NewRepository(); await Seed(store);
        await using var factory = new PublishingHttpFactory(store); using var client = factory.Operator();
        using var first = await client.GetAsync("/pub/catalog/workflows"); var tag = first.Headers.ETag!.ToString();
        Assert.StartsWith("W/\"", tag); Assert.True(first.Headers.CacheControl!.Private); Assert.True(first.Headers.CacheControl.NoCache);
        using var unchanged = Request("GET", "/pub/catalog/workflows"); unchanged.Headers.TryAddWithoutValidation("If-None-Match", tag);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(unchanged)).StatusCode);
        var filtered = await client.GetAsync("/pub/catalog/workflows?text=missing"); Assert.NotEqual(tag, filtered.Headers.ETag!.ToString());
        var normalized = await client.GetAsync("/pub/catalog/workflows?text=%20MISSING%20"); Assert.Equal(filtered.Headers.ETag, normalized.Headers.ETag);
        factory.Scope.Value = new(new(["Published"]), new(), new(), new());
        var scoped = await client.GetAsync("/pub/catalog/workflows"); Assert.NotEqual(tag, scoped.Headers.ETag!.ToString());
        foreach (var path in new[] { "/catalog/packages", "/catalog/activities", "/catalog/packages/http.package/1.0.0", "/catalog/packages/http.package/1.0.0/hosts", "/catalog/workflows/Published/versions/1", "/catalog/workflows/Published/versions/1/hosts" })
        {
            using var request = Request("GET", "/pub" + path); request.Headers.TryAddWithoutValidation("If-None-Match", "*");
            var response = await client.SendAsync(request); Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        }
        foreach (var query in new[] { "limit=0", "limit=1001", "state=1", "state=published", "text=x&text=y", "after=bad" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/pub/catalog/workflows?" + query)).StatusCode);
        factory.Scope.Value = CatalogScope.Unrestricted;
        var page = await Read(await client.GetAsync("/pub/drafts?limit=1"));
        await store.SaveDefinitionAsync(new("Zulu", 1, "workflow", TypedPublishingScenarios.Yaml("Zulu")), null);
        page = await Read(await client.GetAsync("/pub/drafts?limit=1"));
        var cursor = page.GetProperty("nextCursor").GetString(); Assert.NotNull(cursor);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/pub/drafts?kind=workflow&after=" + Uri.EscapeDataString(cursor!))).StatusCode);
        factory.Scope.Value = new(new(["Work", "Zulu"]), new(), new(), new());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/pub/drafts?after=" + Uri.EscapeDataString(cursor!))).StatusCode);
        var revision = await store.GetCatalogRevisionAsync();
        var package = (await store.GetPackageAsync("http.package", "1.0.0"))!;
        await store.DeleteAsync(package.Digest);
        var unavailable = await client.GetAsync("/pub/catalog/packages/http.package/1.0.0");
        Assert.False((await Read(unavailable)).GetProperty("summary").GetProperty("artifactPresent").GetBoolean());
        Assert.True(unavailable.Headers.CacheControl!.NoStore); Assert.Equal(revision, await store.GetCatalogRevisionAsync());
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ActivityReadsIntersectPackageAndActivityScopesAndRequireEverySelector(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile); var store = database.NewRepository(); var seed = await Seed(store);
        await using var factory = new PublishingHttpFactory(store); using var client = factory.Operator();
        var path = "/pub/catalog/activities/http.echo/versions/1";
        foreach (var selector in new[] { "", "?packageId=http.package", "?packageId=http.package&packageVersion=1.0.0", "?packageVersion=1.0.0&build=x" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(path + selector)).StatusCode);
        var full = path + "?packageId=http.package&packageVersion=1.0.0&build=" + seed.Package.Digest.Value;
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(full)).StatusCode);
        factory.Scope.Value = new(new(), new([]), new(["http.package"]), new());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(full)).StatusCode);
        var package = await Read(await client.GetAsync("/pub/catalog/packages/http.package/1.0.0"));
        Assert.Empty(package.GetProperty("activities").GetProperty("activities").EnumerateArray());
        var activities = await Read(await client.GetAsync("/pub/catalog/packages/http.package/1.0.0/activities")); Assert.Empty(activities.GetProperty("items").EnumerateArray());
        factory.Scope.Value = new(new(), new(["http.echo"]), new([]), new());
        activities = await Read(await client.GetAsync("/pub/catalog/activities")); Assert.Empty(activities.GetProperty("items").EnumerateArray());
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task UploadBoundsAndCreatedReplayConflictsHaveResolvableReceiptLocations(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile); var store = database.NewRepository();
        await using var factory = new PublishingHttpFactory(store) { PublisherOptions = new() { PackageReadLimits = new() { MaxArchiveBytes = 4096, MaxExpandedBytes = 1024 } } };
        using var client = factory.Operator(); var bytes = PublishingFeed.Package("http.upload", []);
        var first = await client.PostAsync("/pub/catalog/packages", new ByteArrayContent(bytes)); Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var response = await Read(first); Assert.True(response.GetProperty("created").GetBoolean());
        Assert.Equal("Uploaded", response.GetProperty("package").GetProperty("summary").GetProperty("origin").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(first.Headers.Location)).StatusCode);
        var replay = await client.PostAsync("/pub/catalog/packages", new ByteArrayContent(bytes)); Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.False((await Read(replay)).GetProperty("created").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync("/pub/catalog/packages", new ByteArrayContent(PublishingFeed.Package("http.upload", new() { ["other"] = [1] })))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/pub/catalog/packages", new ByteArrayContent([1, 2, 3]))).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsync("/pub/catalog/packages", new ByteArrayContent(new byte[4097]))).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PostAsync("/pub/catalog/packages", new ByteArrayContent(PublishingFeed.Package("http.expanded", new() { ["expanded"] = new byte[2048] })))).StatusCode);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task DownloadsVerifyBeforeFirstByteEvenForConditionalRequestsAndHaveIndependentPolicy(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile); var store = database.NewRepository(); var seed = await Seed(store);
        var artifacts = new ArtifactProbe(store);
        await using var factory = new PublishingHttpFactory(store) { Artifacts = artifacts }; using var client = factory.Operator();
        const string path = "/pub/catalog/packages/http.package/1.0.0/nupkg";
        var valid = await client.GetAsync(path); Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Equal(seed.Package.Digest, ArtifactDigest.Of(await valid.Content.ReadAsByteArrayAsync()));
        artifacts.Corrupt = true;
        using var conditional = Request("GET", path); conditional.Headers.TryAddWithoutValidation("If-None-Match", valid.Headers.ETag!.ToString());
        var corrupt = await client.SendAsync(conditional); Assert.Equal(HttpStatusCode.ServiceUnavailable, corrupt.StatusCode);
        Assert.Equal("application/problem+json", corrupt.Content.Headers.ContentType!.MediaType);
        Assert.Equal("PUB041", (await Read(corrupt)).GetProperty("code").GetString());
        Assert.False((await corrupt.Content.ReadAsByteArrayAsync()).AsSpan().StartsWith("PK"u8));
        using var metadata = factory.Operator("read"); Assert.Equal(HttpStatusCode.Forbidden, (await metadata.GetAsync(path)).StatusCode);
        artifacts.Corrupt = false; artifacts.Oversize = true;
        var oversized = await client.GetAsync(path); Assert.Equal(HttpStatusCode.ServiceUnavailable, oversized.StatusCode);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task OpenApiDeclaresSelectorsPoliciesPreconditionsStringEnumsAndRetryHeaders(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        await using var factory = new PublishingHttpFactory(database.NewRepository()); using var client = factory.Operator();
        var response = await client.GetAsync("/openapi/v1.json"); response.EnsureSuccessStatusCode(); var document = await Read(response);
        var paths = document.GetProperty("paths");
        var publish = paths.GetProperty("/pub/drafts/{kind}/{name}/{version}/publish").GetProperty("post");
        Assert.Contains("publish", publish.GetProperty("description").GetString());
        Assert.True(publish.GetProperty("responses").GetProperty("202").GetProperty("headers").TryGetProperty("Retry-After", out _));
        foreach (var status in new[] { "409", "413", "422", "500", "503" })
        {
            var content = publish.GetProperty("responses").GetProperty(status).GetProperty("content");
            Assert.EndsWith("/WorkflowPublicationResponse", content.GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
            Assert.True(content.TryGetProperty("application/problem+json", out _));
        }
        var validation = paths.GetProperty("/pub/drafts/{kind}/{name}/{version}/validate").GetProperty("post");
        Assert.EndsWith("/WorkflowValidationResponse", validation.GetProperty("responses").GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        Assert.Contains(publish.GetProperty("parameters").EnumerateArray(), item => item.GetProperty("name").GetString() == "If-Match" && item.GetProperty("required").GetBoolean());
        var activity = paths.GetProperty("/pub/catalog/activities/{key}/versions/{version}").GetProperty("get");
        foreach (var name in new[] { "packageId", "packageVersion", "build" }) Assert.Contains(activity.GetProperty("parameters").EnumerateArray(), item => item.GetProperty("name").GetString() == name && item.GetProperty("required").GetBoolean());
        var schemas = document.GetProperty("components").GetProperty("schemas");
        foreach (var pair in new[] { ("WorkflowVersionResponse", "state"), ("ActivityProviderResponse", "origin"), ("PackageResponse", "extraction"), ("WorkflowDiagnosticResponse", "class"),
                     ("ActivityScopeResponse", "concurrency"), ("ActivityScopeResponse", "threading") })
        {
            var property = schemas.GetProperty(pair.Item1).GetProperty("properties").GetProperty(pair.Item2);
            Assert.Equal("string", property.GetProperty("type").GetString()); Assert.NotEmpty(property.GetProperty("enum").EnumerateArray());
        }
        Assert.Equal(Enum.GetNames<ScopeConcurrency>(), schemas.GetProperty("ActivityScopeResponse").GetProperty("properties").GetProperty("concurrency").GetProperty("enum").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(Enum.GetNames<ScopeThreading>(), schemas.GetProperty("ActivityScopeResponse").GetProperty("properties").GetProperty("threading").GetProperty("enum").EnumerateArray().Select(item => item.GetString()));
    }

    [WorkflowActivity("http.echo", Version = "1")]
    public sealed class Echo : IAsyncActivity { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask; }
    private sealed class ArtifactProbe(IArtifactStore inner) : IArtifactStore
    {
        public bool Corrupt { get; set; } public bool Oversize { get; set; }
        public int Puts { get; private set; }
        public ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) => Corrupt ? new(new MemoryStream("PKcorrupt"u8.ToArray()))
            : Oversize ? new(new MemoryStream(new byte[64 * 1024 * 1024 + 1])) : inner.OpenAsync(digest, cancellationToken);
        public ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken cancellationToken = default)
        { Puts++; return inner.PutAsync(kind, content, cancellationToken); }
        public ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) => inner.ExistsAsync(digest, cancellationToken);
        public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) => inner.DeleteAsync(digest, cancellationToken);
        public ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime olderThanUtc, int batchSize, CancellationToken cancellationToken = default) => inner.PurgeUnreferencedAsync(references, olderThanUtc, batchSize, cancellationToken);
    }
}
