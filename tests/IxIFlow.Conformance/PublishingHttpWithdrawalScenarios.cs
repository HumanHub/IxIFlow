using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Templates;
using IxIFlow.Templates.Liquid;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

public sealed partial class PublishingHttpScenarios
{
    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task WithdrawalSucceedsWithMaskedHoldersAndAtomicStaleCasWithoutDeleteAlias(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile); var store = database.NewRepository(); var seed = await Seed(store);
        var pin = new WorkflowPackageReference(seed.Package.Id, seed.Package.Version, seed.Package.Digest);
        foreach (var name in new[] { "Visible", "Secret" })
            await store.RegisterWorkflowVersionAsync(seed.Version with { Name = name, ArtifactReferences = [pin.Digest] });
        var model = TemplateVersion.Create<TemplateCatalogScenarios.Model>("holder", 1, "liquid", TemplateFormat.Text, new TemplateVariant("", "text"));
        await store.RegisterTemplateVersionAsync(new(model.Key, model.Version, model.Engine, model.Format, model.ModelType, model.ModelTypeId, [pin], null, null, model.Variants), store);
        await using var factory = new PublishingHttpFactory(store); using var client = factory.Operator("read lifecycle");
        factory.Scope.Value = new(new(["Visible"]), new(), new(), new([]));
        const string path = "/pub/catalog/packages/http.package/1.0.0";
        Assert.Equal(HttpStatusCode.PreconditionRequired, (await client.DeleteAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Request("DELETE", path, "W/\"l-0\""))).StatusCode);
        var detail = await client.GetAsync(path); var tag = detail.Headers.ETag!.ToString();
        var replies = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => client.SendAsync(Request("DELETE", path, tag))));
        Assert.Single(replies, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(replies, response => response.StatusCode == HttpStatusCode.PreconditionFailed);
        var result = await Read(replies.Single(response => response.StatusCode == HttpStatusCode.OK));
        Assert.True(result.GetProperty("package").GetProperty("summary").GetProperty("withdrawn").GetBoolean());
        var holders = result.GetProperty("holders"); Assert.True(holders.GetProperty("outsideScope").GetBoolean());
        Assert.Equal("Visible", Assert.Single(holders.GetProperty("workflows").EnumerateArray()).GetProperty("name").GetString());
        Assert.Empty(holders.GetProperty("templates").EnumerateArray()); Assert.DoesNotContain("Secret", result.GetRawText());
        Assert.DoesNotContain("holder\"", result.GetRawText());
        Assert.Equal(holders.GetRawText(), (await client.GetStringAsync(path + "/holders")));
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Request("DELETE", "/pub/catalog/packages/http.package/versions/1.0.0", tag))).StatusCode);
        Assert.True(await store.ExistsAsync(seed.Package.Digest)); Assert.NotNull(await store.GetPackageAsync(seed.Package.Id, seed.Package.Version));
        factory.Scope.Value = CatalogScope.Unrestricted;
        var all = await Read(await client.GetAsync(path + "/holders")); Assert.Equal("holder", all.GetProperty("templates")[0].GetProperty("key").GetString());
        Assert.Equal(2, all.GetProperty("workflows").GetArrayLength());
        Assert.All((await Read(await client.GetAsync(path + "/activities"))).GetProperty("items").EnumerateArray(), item => Assert.True(item.GetProperty("withdrawn").GetBoolean()));
        using var noSource = Request("GET", path + "/source"); noSource.Headers.TryAddWithoutValidation("If-None-Match", "*");
        using var downloader = factory.Operator("download");
        var absentSource = await downloader.SendAsync(noSource); Assert.Equal(HttpStatusCode.NotFound, absentSource.StatusCode); Assert.Null(absentSource.Headers.ETag);
        factory.Scope.Value = new(new(), new(), new([]), new());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path + "/holders")).StatusCode);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task NuGetIndexIncludesOnlyAuthorizedAvailableVersionsIncludingWithdrawnAndVerifiesExactBytes(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile); var store = database.NewRepository(); var publisher = new WorkflowPublisher(store, store);
        var packages = new List<WorkflowPackage>(); var bytes = new List<byte[]>();
        for (var version = 1; version <= 4; version++)
        {
            var content = PublishingFeed.Package("http.feed", new() { ["ixiflow/activity.yaml"] = Encoding.UTF8.GetBytes("source" + version) }, version: version + ".0.0");
            bytes.Add(content); packages.Add((await publisher.UploadPackageAsync(new MemoryStream(content))).Stored);
        }
        await store.SetPackageWithdrawnAsync("http.feed", "1.0.0", true, new(packages[0].LifecycleRevision));
        await store.DeleteAsync(packages[3].Digest);
        var artifacts = new ArtifactProbe(store);
        await using var factory = new PublishingHttpFactory(store) { Artifacts = artifacts, ResourceAllowed = resource => resource is not CatalogPackageDetail { Summary.Version: "2.0.0" } };
        using var client = factory.Operator("download");
        const string root = "/pub/nuget/flatcontainer/http.feed/";
        var index = await client.GetAsync(root + "index.json"); var body = await Read(index);
        Assert.Equal(new[] { "1.0.0", "3.0.0" }, body.GetProperty("versions").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("no-store", index.Headers.CacheControl!.ToString());
        var services = await Read(await client.GetAsync("/pub/nuget/index.json"));
        Assert.Equal("3.0.0", services.GetProperty("version").GetString());
        Assert.EndsWith("/pub/nuget/flatcontainer/", services.GetProperty("resources")[0].GetProperty("@id").GetString());
        Assert.Equal("PackageBaseAddress/3.0.0", services.GetProperty("resources")[0].GetProperty("@type").GetString());
        var file = root + "1.0.0/http.feed.1.0.0.nupkg";
        var download = await client.GetAsync(file); Assert.Equal(bytes[0], await download.Content.ReadAsByteArrayAsync());
        var nuspec = await client.GetAsync(root + "1.0.0/http.feed.nuspec");
        using (var zip = new ZipArchive(new MemoryStream(bytes[0])))
        using (var expected = new StreamReader(zip.GetEntry("http.feed.nuspec")!.Open()))
            Assert.Equal(await expected.ReadToEndAsync(), await nuspec.Content.ReadAsStringAsync());
        Assert.Equal("application/xml", nuspec.Content.Headers.ContentType!.MediaType);
        foreach (var name in new[] { "wrong.nupkg", "http.feed.1.0.0.nuspec", "http.feed.dll" })
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(root + "1.0.0/" + name)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(root + "2.0.0/http.feed.2.0.0.nupkg")).StatusCode);
        artifacts.Corrupt = true;
        foreach (var path in new[] { file, root + "1.0.0/http.feed.nuspec", "/pub/catalog/packages/http.feed/1.0.0/source" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path); request.Headers.IfNoneMatch.Add(download.Headers.ETag!);
            var corrupt = await client.SendAsync(request); Assert.Equal(HttpStatusCode.ServiceUnavailable, corrupt.StatusCode);
            Assert.Equal("PUB041", (await Read(corrupt)).GetProperty("code").GetString()); Assert.Null(corrupt.Headers.ETag);
        }
        artifacts.Corrupt = false; await store.DeleteAsync(packages[2].Digest);
        using (var conditional = new HttpRequestMessage(HttpMethod.Get, root + "index.json"))
        {
            conditional.Headers.TryAddWithoutValidation("If-None-Match", "*"); var response = await client.SendAsync(conditional);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Single((await Read(response)).GetProperty("versions").EnumerateArray());
        }
        factory.Scope.Value = new(new(), new(), new([]), new());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(root + "index.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(file)).StatusCode);
        using var readOnly = factory.Operator("read"); Assert.Equal(HttpStatusCode.Forbidden, (await readOnly.GetAsync("/pub/nuget/index.json")).StatusCode);
        var refused = await publisher.ValidateAsync(new("Refused", 1, "workflow", "workflow: {name: Refused, dataType: Missing}\nimports: {catalogs: [http.feed@1.0.0]}\ndefinitions: []"));
        Assert.Contains(refused.Diagnostics, item => item.Code == "PUB025");
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ColdHostExecutesPinnedActivityAndTemplateAfterHttpWithdrawal(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile); var store = database.NewRepository(); using var feed = ModelFeed();
        await using var factory = new PublishingHttpFactory(store) { ConfigureServices = Templates, PublisherOptions = new() { NuGetConfigPath = feed.Config } };
        using var client = factory.Operator();
        await PublishHttp(client, "activity", "acme.http", 1, ActivityYaml);
        await PublishHttp(client, "template", "notice", 1, TemplateYaml());
        const string yaml = """
            workflow: {name: ColdHttp, version: 1, dataType: HttpModels.Data}
            imports: {catalogs: [acme.http@1.0.0, http.models@1.0.0]}
            definitions:
              - {kind: activity, id: run, activity: acme.http, activityVersion: '1', output: [{source: Result, to: {path: workflow.Value}}]}
              - {kind: renderTemplate, id: render, template: notice, model: {path: workflow.Model}, output: [{source: Rendered, to: {path: workflow.Body}}]}
            """;
        await PublishHttp(client, "workflow", "ColdHttp", 1, yaml);
        var version = (await store.GetWorkflowVersionAsync("ColdHttp", 1))!;
        await PublishHttp(client, "template", "notice", 2, TemplateYaml(version: 2, content: "new"));
        foreach (var id in new[] { "acme.http", "http.models" })
        {
            var path = $"/pub/catalog/packages/{id}/1.0.0"; var detail = await client.GetAsync(path);
            var withdrawn = await client.SendAsync(Request("DELETE", path, detail.Headers.ETag!.ToString()));
            Assert.Equal(HttpStatusCode.OK, withdrawn.StatusCode);
            Assert.NotEmpty((await Read(withdrawn)).GetProperty("holders").GetProperty("workflows").EnumerateArray());
        }
        var failed = await client.PostAsync("/pub/drafts/workflow/ColdHttp/1/validate", new StringContent(yaml, Encoding.UTF8, "application/yaml"));
        Assert.Contains((await Read(failed)).GetProperty("diagnostics").EnumerateArray(), item => item.GetProperty("code").GetString() == "PUB025");
        var cache = Path.Combine(Path.GetTempPath(), "IxIFlow.HttpCold", Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(cache));
        try
        {
            await using var host = PublishedBundleScenario.Host(store, cache, configure: services =>
            {
                database.ConfigureHost(services);
                services.AddSingleton(new TemplateRenderer(store, new([new LiquidTemplateEngine()])));
            });
            await database.StartHostLinksAsync(host);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var workflowClient = host.GetRequiredService<IWorkflowClient>();
            var id = await workflowClient.StartAsync(version.Name, version.Version, new { Model = new { Name = "Ana" } }, cancellationToken: timeout.Token);
            var result = await workflowClient.AwaitAsync(id, WorkflowAwaitCondition.Finished, timeout.Token);
            Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
            var data = result.WorkflowData!; Assert.Equal(7, data.GetType().GetProperty("Value")!.GetValue(data));
            var body = Assert.IsType<RenderedTemplate>(data.GetType().GetProperty("Body")!.GetValue(data));
            Assert.Equal("Hello Ana", body.Content); Assert.Equal(1, body.Version);
        }
        finally { if (Directory.Exists(cache)) Directory.Delete(cache, true); }
    }
}
