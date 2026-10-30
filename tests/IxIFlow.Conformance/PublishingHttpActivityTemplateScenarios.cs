using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using IxIFlow.AspNetCore;
using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Dsl.Documents;
using IxIFlow.Templates;
using IxIFlow.Templates.Liquid;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

public sealed partial class PublishingHttpScenarios
{
    internal const string ActivityYaml =
        "# verbatim\nactivity: {key: acme.http, version: 1, name: Http}\noutput:\n  Result: {type: int}\nbody: |\n  Result = 7;\n  await Task.CompletedTask;\n";

    internal static string TemplateYaml(string key = "notice", int version = 1, string model = "HttpModels.Model",
        string content = "Hello {{ Name }}") =>
        $"template: {{key: {key}, version: {version}, engine: liquid, format: text, model: {model}}}\nimports: {{catalogs: [http.models@1.0.0]}}\ncontent: '{content}'\ncultures: {{de: 'Hallo {{{{ Name }}}}'}}\n";

    private static readonly Lazy<byte[]> HttpModels = new(() => PublishingFeed.Package("http.models", new()
    {
        ["lib/net10.0/Http.Models.dll"] = PublishingFeed.Emit("Http.Models", """
                                                                             namespace HttpModels;
                                                                             public class Model { public string Name {get;set;} = "Ana"; }
                                                                             public class Slow { public Slow() { System.Threading.Thread.Sleep(700); } public string Name {get;set;} = "Ana"; }
                                                                             public class Data { public Model Model {get;set;} = new Model(); public int Value {get;set;} public IxIFlow.Templates.RenderedTemplate Body {get;set;} }
                                                                             """)
    }));

    private static PublishingFeed ModelFeed()
    {
        var feed = new PublishingFeed();
        File.WriteAllBytes(Path.Combine(feed.Folder, "http.models.1.0.0.nupkg"), HttpModels.Value);

        return feed;
    }

    private static void Templates(IServiceCollection services) => services.AddSingleton(new TemplateEngines([new LiquidTemplateEngine()]));

    private static async Task<JsonElement> PublishHttp(HttpClient client, string kind, string name, int version, string yaml)
    {
        var path = $"/pub/drafts/{kind}/{name}/{version}";
        using var create = Request("PUT", path, content: new StringContent(yaml, Encoding.UTF8, "application/yaml"));
        create.Headers.TryAddWithoutValidation("If-None-Match", "*");
        var saved = await client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        var savedDraft = (await Read(saved)).GetProperty("draft");
        var response = await client.SendAsync(Request("POST", path + "/publish", saved.Headers.ETag!.ToString()));
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());

        var result = response.StatusCode == HttpStatusCode.Accepted
            ? await Until(client, response.Headers.Location!.ToString())
            : await Read(response);

        Assert.Equal("Published", result.GetProperty("state").GetString());
        Assert.Equal(kind, result.GetProperty("draft").GetProperty("kind").GetString());
        Assert.Equal(version, result.GetProperty("draft").GetProperty("version").GetInt32());
        Assert.Equal(savedDraft.GetProperty("generation").GetGuid(), result.GetProperty("draft").GetProperty("generation").GetGuid());
        Assert.Equal(savedDraft.GetProperty("revision").GetInt64(), result.GetProperty("draft").GetProperty("revision").GetInt64());
        var history = await Read(await client.GetAsync(path + "/publications"));

        Assert.Equal(result.GetProperty("id").GetGuid(),
            Assert.Single(history.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        var replay = await client.SendAsync(Request("POST", path + "/publish", saved.Headers.ETag!.ToString()));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(result.GetProperty("id").GetGuid(), (await Read(replay)).GetProperty("id").GetGuid());

        return result;
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ActivityAndTemplateUseSharedDraftValidationPublicationHistoryAndSource(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        using var feed = ModelFeed();
        var artifacts = new ArtifactProbe(store);

        await using var factory = new PublishingHttpFactory(store)
            { Artifacts = artifacts, ConfigureServices = Templates, PublisherOptions = new() { NuGetConfigPath = feed.Config } };

        using var client = factory.Operator();
        var before = await store.GetCatalogRevisionAsync();

        foreach (var (kind, name, yaml) in new[] { ("activity", "acme.http", ActivityYaml), ("template", "notice", TemplateYaml()) })
        {
            var validation = await client.PostAsync($"/pub/drafts/{kind}/{name}/1/validate",
                new StringContent(yaml, Encoding.UTF8, "application/yaml"));

            Assert.Equal(HttpStatusCode.OK, validation.StatusCode);
            var report = await Read(validation);
            Assert.True(report.GetProperty("isValid").GetBoolean(), report.GetRawText());
            Assert.Empty((await store.GetPublicationsAsync(new(CatalogScope.Unrestricted, kind, name, 1))).Items);
        }

        Assert.Equal(before, await store.GetCatalogRevisionAsync());
        Assert.Equal(0, artifacts.Puts);
        Assert.Empty(await store.GetPackagesAsync());
        var activity = await PublishHttp(client, "activity", "acme.http", 1, ActivityYaml);
        Assert.Equal("acme.http", activity.GetProperty("package").GetProperty("summary").GetProperty("id").GetString());
        Assert.Equal(ActivityYaml, await client.GetStringAsync("/pub/catalog/packages/acme.http/1.0.0/source"));
        var template = await PublishHttp(client, "template", "notice", 1, TemplateYaml());
        Assert.Equal("notice", template.GetProperty("template").GetProperty("summary").GetProperty("key").GetString());
        var metadata = await client.GetStringAsync("/pub/catalog/templates/notice/versions/1");
        Assert.DoesNotContain("Hello", metadata);
        var source = await client.GetStringAsync("/pub/catalog/templates/notice/versions/1/source");
        var document = WorkflowYamlSerializer.ReadTemplate(source);
        Assert.Equal("Hello {{ Name }}", document.Content);
        Assert.Equal(new[] { "http.models@1.0.0" }, document.Imports.Catalogs);
        Assert.Contains("sha256:", source);
        Assert.DoesNotContain("sha256:sha256:", source);
        Assert.Equal(source, await client.GetStringAsync("/pub/catalog/templates/notice/versions/1/source"));
        factory.Scope.Value = new(new(), new(), new([]), new());
        var masked = await Read(await client.GetAsync("/pub/catalog/templates/notice/versions/1"));
        Assert.Empty(masked.GetProperty("summary").GetProperty("modelPackages").EnumerateArray());
        Assert.Equal(source, await client.GetStringAsync("/pub/catalog/templates/notice/versions/1/source"));
        factory.Scope.Value = CatalogScope.Unrestricted;
        using var readOnly = factory.Operator("read");

        foreach (var path in new[]
                 {
                     "/catalog/packages/acme.http/1.0.0/source", "/catalog/packages/acme.http/1.0.0/nupkg",
                     "/catalog/templates/notice/versions/1/source"
                 })
            Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.GetAsync("/pub" + path)).StatusCode);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task PreviewHasOwnPolicyAndFeedBackedZeroWriteBoundsAndTimeout(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        using var feed = ModelFeed();
        var draft = await store.SaveDefinitionAsync(new("notice", 1, "template", TemplateYaml()), null);
        var artifacts = new ArtifactProbe(store);
        var before = await store.GetCatalogRevisionAsync();

        await using var factory = new PublishingHttpFactory(store)
            { ConfigureServices = Templates, Artifacts = artifacts, PublisherOptions = new() { NuGetConfigPath = feed.Config } };

        using var client = factory.Operator("preview");
        using var execute = factory.Operator("read execute");
        const string path = "/pub/drafts/template/notice/1/preview";
        Assert.Equal(HttpStatusCode.Forbidden, (await execute.PostAsJsonAsync(path, new { model = new { Name = "Ana" } })).StatusCode);
        var allowed = await client.PostAsJsonAsync(path, new { culture = "de-AT", model = new { Name = "Ana" } });
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal("Hallo Ana", (await Read(allowed)).GetProperty("content").GetString());
        factory.ResourceAllowed = resource => resource is not WorkflowDraftSummary;

        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.PostAsync(path, new StringContent("{", Encoding.UTF8, "application/json"))).StatusCode);

        factory.ResourceAllowed = _ => true;
        factory.Scope.Value = new(new(), new(), new(), new([]));
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(path, new { model = new { Name = "Ana" } })).StatusCode);
        factory.Scope.Value = CatalogScope.Unrestricted;

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsync(path, new StringContent("{", Encoding.UTF8, "application/json"))).StatusCode);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.PostAsync(path, new StringContent("{}"))).StatusCode);

        foreach (var (options, expected) in new[]
                 {
                     (new WorkflowPublisherOptions { NuGetConfigPath = feed.Config, MaxPreviewModelBytes = 4 }, "TPL018"),
                     (new WorkflowPublisherOptions { NuGetConfigPath = feed.Config, MaxPreviewOutputBytes = 4 }, "TPL018")
                 })
        {
            await using var bounded = new PublishingHttpFactory(store)
                { ConfigureServices = Templates, Artifacts = artifacts, PublisherOptions = options };

            using var boundedClient = bounded.Operator("preview");
            var result = await boundedClient.PostAsJsonAsync(path, new { model = new { Name = "Ana" } });
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, result.StatusCode);
            Assert.Equal(expected, (await Read(result)).GetProperty("diagnostics")[0].GetProperty("code").GetString());

            Assert.Equal(HttpStatusCode.RequestEntityTooLarge,
                (await boundedClient.PostAsJsonAsync(path, new { model = new { Name = new string('a', 5000) } })).StatusCode);
        }

        draft = await store.SaveDefinitionAsync(draft with { Source = TemplateYaml(model: "HttpModels.Slow") }, draft.Id);

        await using (var timed = new PublishingHttpFactory(store)
                     {
                         ConfigureServices = Templates, Artifacts = artifacts,
                         PublisherOptions = new() { NuGetConfigPath = feed.Config, BuildTimeout = TimeSpan.FromMilliseconds(100) }
                     })
        {
            using var timedClient = timed.Operator("preview");
            var result = await timedClient.PostAsJsonAsync(path, new { model = new { Name = "Ana" } });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, result.StatusCode);
            Assert.Equal("PUB043", (await Read(result)).GetProperty("diagnostics")[0].GetProperty("code").GetString());
        }

        Assert.Equal(0, artifacts.Puts);
        Assert.Empty(await store.GetPackagesAsync());
        Assert.Empty((await store.GetPublicationsAsync(new(CatalogScope.Unrestricted, "template", "notice", 1))).Items);
        Assert.Equal(draft.Id, (await store.GetDefinitionAsync("template", "notice", 1))!.Id);
        Assert.Equal(before + 1, await store.GetCatalogRevisionAsync()); // Only the explicit source update wrote.
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ExtendedOpenApiDescribesPreviewWithdrawalAndBothNuGetMediaTypes(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await using var factory = new PublishingHttpFactory(store);
        using var client = factory.Operator();
        var document = await Read(await client.GetAsync("/openapi/v1.json"));
        var paths = document.GetProperty("paths");
        var preview = paths.GetProperty("/pub/drafts/template/{name}/{version}/preview").GetProperty("post");
        Assert.Contains("preview", preview.GetProperty("description").GetString());
        Assert.Contains("Trusted execution", preview.GetProperty("description").GetString());
        Assert.True(preview.GetProperty("requestBody").GetProperty("content").TryGetProperty("application/json", out _));
        Assert.True(preview.GetProperty("responses").GetProperty("413").GetProperty("content").TryGetProperty("application/json", out _));

        Assert.True(preview.GetProperty("responses").GetProperty("413").GetProperty("content")
            .TryGetProperty("application/problem+json", out _));

        var withdrawal = paths.GetProperty("/pub/catalog/packages/{id}/{version}").GetProperty("delete");
        Assert.False(withdrawal.GetProperty("responses").TryGetProperty("204", out _));

        Assert.Contains(withdrawal.GetProperty("parameters").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "If-Match" && item.GetProperty("required").GetBoolean());

        var file = paths.GetProperty("/pub/nuget/flatcontainer/{id}/{version}/{file}").GetProperty("get");
        var content = file.GetProperty("responses").GetProperty("200").GetProperty("content");
        Assert.True(content.TryGetProperty("application/octet-stream", out _));
        Assert.True(content.TryGetProperty("application/xml", out _));

        Assert.Equal("string",
            Assert.Single(file.GetProperty("parameters").EnumerateArray(), item => item.GetProperty("name").GetString() == "version")
                .GetProperty("schema").GetProperty("type").GetString());

        Assert.False(paths.GetProperty("/pub/nuget/flatcontainer/{id}/index.json").GetProperty("get").GetProperty("responses")
            .TryGetProperty("304", out _));
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task TemplatePagesAndRenderDescriptorRespectScopeCursorsAndSourceBoundary(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();

        foreach (var key in new[] { "visible", "hidden" })
        foreach (var version in new[] { 1, 2, 10 })
            await store.RegisterTemplateVersionAsync(
                TemplateVersion.Create<TemplateCatalogScenarios.Model>(key, version, "liquid", TemplateFormat.Text,
                    new TemplateVariant("", "secret content")), store);

        await using var factory = new PublishingHttpFactory(store);
        factory.Scope.Value = new(new(), new(), new(), new(["visible"]));
        using var client = factory.Operator();
        var first = await client.GetAsync("/pub/catalog/templates/visible/versions?limit=1");
        var page = await Read(first);
        Assert.Equal(1, page.GetProperty("items")[0].GetProperty("version").GetInt32());
        Assert.DoesNotContain("secret content", page.GetRawText());

        var next = "/pub/catalog/templates/visible/versions?limit=1&after=" +
            Uri.EscapeDataString(page.GetProperty("nextCursor").GetString()!);

        Assert.Equal(2, (await Read(await client.GetAsync(next))).GetProperty("items")[0].GetProperty("version").GetInt32());
        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/pub/catalog/templates/visible/versions?limit=1");
        conditional.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(conditional)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/pub/catalog/templates/hidden/versions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/pub/catalog/templates/hidden/versions/1/source")).StatusCode);

        Assert.All((await Read(await client.GetAsync("/pub/catalog/templates"))).GetProperty("items").EnumerateArray(),
            item => Assert.Equal("visible", item.GetProperty("key").GetString()));

        factory.Scope.Value = CatalogScope.Unrestricted;
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(next)).StatusCode);
        factory.Scope.Value = new(new(), new(), new(), new([]));
        var steps = await Read(await client.GetAsync("/pub/catalog/steps"));
        var render = Assert.Single(steps.EnumerateArray(), item => item.GetProperty("kind").GetString() == "renderTemplate");
        Assert.Contains(render.GetProperty("inputs").EnumerateArray(), item => item.GetProperty("name").GetString() == "Model");
        Assert.Contains(render.GetProperty("outputs").EnumerateArray(), item => item.GetProperty("name").GetString() == "Rendered");
    }
}