using System.Collections.Concurrent;
using System.Net;
using System.Security;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using IxIFlow.AspNetCore;
using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Dsl.Documents;
using IxIFlow.Extensions;
using IxIFlow.Templates;
using IxIFlow.Templates.Liquid;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "authoring"), Trait("Spec", "F-19"), Trait("Spec", "H-03")]
public sealed class PublishingHttpTransitiveWithdrawalScenarios
{
    private const string Root = "withdrawal.root";
    private const string Leaf = "withdrawal.leaf";

    private static string Yaml(string import) =>
        $"workflow: {{name: Withdrawal, version: 1, dataType: System.Object}}\nimports: {{catalogs: [{import}]}}\ndefinitions: []";

    private static string Dependency(string id, string range = "[1.0.0]") => $"<dependency id=\"{id}\" version=\"{range}\"/>";

    private static byte[] Package(string id, string dependencies = "", string version = "1.0.0") =>
        PublishingFeed.Package(id, new(), dependencies, version);

    private static async Task<WorkflowPackage> Upload(IWorkflowStateRepository store, byte[] bytes) =>
        (await new WorkflowPublisher(store, store).UploadPackageAsync(new MemoryStream(bytes))).Stored;

    private static Task Withdraw(IWorkflowStateRepository store, WorkflowPackage package) =>
        store.SetPackageWithdrawnAsync(package.Id, package.Version, true, new(package.LifecycleRevision));

    private static void Refused(IEnumerable<WorkflowPublishDiagnostic> diagnostics, string code)
    {
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(code, diagnostic.Code);
        Assert.Equal(WorkflowPublishStage.Validate, diagnostic.Stage);

        Assert.Equal(code == "PUB025" ? WorkflowPublishDiagnosticClass.Conflict : WorkflowPublishDiagnosticClass.Unavailable,
            diagnostic.Class);

        if (code == "PUB025")
            Assert.Contains(Leaf + "@1.0.0", diagnostic.Message);
    }

    public static IEnumerable<object[]> ClosureCases => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { "withdrawn", "feed-listed", "newer", "missing", "unavailable", "unrelated-withdrawal", "alternate" }
            .Select(scenario => new object[] { profile, scenario }));

    [Theory, MemberData(nameof(ClosureCases))]
    public async Task PublisherAndHttpPreserveWithdrawalAndAvailabilitySemantics(string profile, string scenario)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        using var feed = new PublishingFeed();
        var leafBytes = Package(Leaf, version: scenario == "unrelated-withdrawal" ? "2.0.0" : "1.0.0");
        var leaf = scenario == "missing" ? null : await Upload(store, leafBytes);

        if (scenario == "newer")
            await Upload(store, Package(Leaf, version: "2.0.0"));

        var dependency = Dependency(Leaf, scenario == "newer" ? "[1.0.0,3.0.0)" : "[1.0.0]");

        if (scenario == "alternate")
        {
            await Upload(store, Package("withdrawal.middle", dependency));
            await Upload(store, Package("withdrawal.middle", version: "2.0.0"));
            dependency = Dependency("withdrawal.middle", "[1.0.0,3.0.0)");
        }

        // Positive cases register their closure after withdrawal, so the catalog's
        // immutable extraction records the allowed choice from the outset.
        if (scenario is not ("newer" or "alternate"))
            await Upload(store, Package(Root, dependency));

        if (leaf != null)
        {
            if (scenario == "unavailable")
                await store.DeleteAsync(leaf.Digest);
            else
                await Withdraw(store, leaf);
        }

        if (scenario is "newer" or "alternate")
            await Upload(store, Package(Root, dependency));

        var options = new WorkflowPublisherOptions();

        if (scenario == "feed-listed")
        {
            await File.WriteAllBytesAsync(Path.Combine(feed.Folder, Leaf + ".1.0.0.nupkg"), leafBytes);
            options = new() { NuGetConfigPath = feed.Config };
        }

        var publisher = new WorkflowPublisher(store, store, options);
        var draft = await store.SaveDefinitionAsync(new("Withdrawal", 1, "workflow", Yaml(Root + "@1.0.0")), null);
        var report = await publisher.ValidateAsync(draft);
        await using var factory = new PublishingHttpFactory(store) { PublisherOptions = options };
        using var client = factory.Operator();

        using var response = await client.PostAsync("/pub/drafts/workflow/Withdrawal/1/validate",
            new StringContent(draft.Source, Encoding.UTF8, "application/yaml"));

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var published = await publisher.PublishAsync(draft.Id);

        if (scenario is "newer" or "alternate")
        {
            Assert.True(report.IsValid, string.Join("; ", report.Diagnostics));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(json.RootElement.GetProperty("isValid").GetBoolean(), json.RootElement.GetRawText());
            Assert.True(published.Success, string.Join("; ", published.Diagnostics));
            var manifest = JsonSerializer.Deserialize<WorkflowBundleManifest>(published.Workflow!.ManifestJson!)!;
            var selected = Assert.Single(manifest.ActivityPackages, pin => pin.Id == (scenario == "newer" ? Leaf : "withdrawal.middle"));
            Assert.Equal("2.0.0", selected.Version);
            Assert.DoesNotContain(manifest.ActivityPackages, pin => pin.Id == Leaf && pin.Version == "1.0.0");
        }
        else
        {
            var code = scenario is "withdrawn" or "feed-listed" ? "PUB025" : "PUB041";
            Refused(report.Diagnostics, code);
            Refused(published.Diagnostics, code);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); // Validation reports every failure class at 200 (shared contracts §11).
            var diagnostic = Assert.Single(json.RootElement.GetProperty("diagnostics").EnumerateArray());
            Assert.Equal(code, diagnostic.GetProperty("code").GetString());
            Assert.Equal(code == "PUB025" ? "Conflict" : "Unavailable", diagnostic.GetProperty("class").GetString());

            if (code == "PUB025")
                Assert.Contains(Leaf + "@1.0.0", diagnostic.GetProperty("message").GetString());

            Assert.Equal(draft.Id, (await store.GetDefinitionAsync("workflow", draft.Name, draft.Version))!.Id);
            Assert.Null(await store.GetWorkflowVersionAsync(draft.Name, draft.Version));
        }
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task VersionlessAndExactSelectionsNameWithdrawnIdentity(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var leaf = await Upload(store, Package(Leaf));
        var bytes = Package(Root, Dependency(Leaf));
        await Upload(store, bytes);
        await Withdraw(store, leaf);
        var publisher = new WorkflowPublisher(store, store);

        foreach (var import in new[] { Leaf, Leaf + "@1.0.0", Root + "@1.0.0" })
            Refused((await publisher.ValidateAsync(new("Withdrawal", 1, "workflow", Yaml(import)))).Diagnostics, "PUB025");

        await Upload(store, Package(Leaf, version: "2.0.0"));
        var latest = await Upload(store, Package(Leaf, version: "3.0.0"));
        await Withdraw(store, latest);
        var draft = await store.SaveDefinitionAsync(new("Withdrawal", 1, "workflow", Yaml(Leaf)), null);
        var published = await publisher.PublishAsync(draft.Id);
        Assert.True(published.Success, string.Join("; ", published.Diagnostics));
        var manifest = JsonSerializer.Deserialize<WorkflowBundleManifest>(published.Workflow!.ManifestJson!)!;
        Assert.Equal("2.0.0", Assert.Single(manifest.ActivityPackages).Version);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task TemplateModelClosureRefusesWithdrawnTransitiveIdentity(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();

        var model = PublishingFeed.Emit("Withdrawal.Model",
            "namespace WithdrawalModels; public class Model { public string Name {get;set;} = \"Ana\"; }");

        var leaf = await Upload(store, PublishingFeed.Package(Leaf, new() { ["lib/net10.0/Withdrawal.Model.dll"] = model }));
        await Upload(store, Package(Root, Dependency(Leaf)));
        var engines = new TemplateEngines([new LiquidTemplateEngine()]);
        var publisher = new WorkflowPublisher(store, store, templateEngines: engines);

        var template = await store.SaveDefinitionAsync(new("withdrawal", 1, "template",
            "template: {key: withdrawal, version: 1, engine: liquid, format: text, model: WithdrawalModels.Model}\n" +
            $"imports: {{catalogs: [{Root}@1.0.0]}}\ncontent: 'Hello {{{{ Name }}}}'"), null);

        var published = await publisher.PublishAsync(template.Id);
        Assert.True(published.Success, string.Join("; ", published.Diagnostics));
        Assert.Contains(published.Template!.Version.ModelPackages, pin => pin.Id == Leaf);
        await Withdraw(store, leaf);

        Refused((await publisher.ValidateAsync(new("withdrawal-new", 1, "template",
            template.Source.Replace("key: withdrawal,", "key: withdrawal-new,")))).Diagnostics, "PUB025");

        var workflow = WorkflowYamlSerializer.Read("""
                                                   workflow: {name: Withdrawal, version: 1, dataType: System.Object}
                                                   definitions:
                                                     - {kind: renderTemplate, id: render, template: withdrawal}
                                                   """);

        Refused((await publisher.ValidateAsync(new("Withdrawal", 1, "workflow", WorkflowYamlSerializer.Write(workflow)))).Diagnostics,
            "PUB025");
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task OwnHttpFeedListingCannotOverrideCatalogWithdrawal(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var leaf = await Upload(store, Package(Leaf));
        await Upload(store, Package(Root, Dependency(Leaf)));
        await Withdraw(store, leaf);
        using var folder = new PublishingFeed();
        var options = new WorkflowPublisherOptions { NuGetConfigPath = folder.Config };
        var requests = new ConcurrentQueue<string>();

        using var host = Host.CreateDefaultBuilder().ConfigureLogging(logging => logging.ClearProviders()).ConfigureWebHostDefaults(web =>
            web.UseKestrel().UseUrls("http://127.0.0.1:0").ConfigureServices(services =>
            {
                services.AddIxIFlow().AddWorkflowPublishing(new()
                {
                    ReadPolicy = "feed", AuthorPolicy = "feed", ExecutePolicy = "feed", PreviewPolicy = "feed",
                    PublishPolicy = "feed", LifecyclePolicy = "feed", DownloadPolicy = "feed"
                }, options);

                services.RemoveAll<IHostedService>();
                services.Replace(ServiceDescriptor.Singleton<IWorkflowStateRepository>(store));
                services.AddSingleton<ICatalogQueryScope>(new PublishingHttpFactory.ScopeProvider());
                services.AddAuthorization(authorization => authorization.AddPolicy("feed", policy => policy.RequireAuthenticatedUser()));
            }).Configure(app =>
            {
                app.Use(async (context, next) =>
                {
                    requests.Enqueue(context.Request.Path);
                    context.User = new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "fixture")], "fixture"));
                    await next(context);
                });

                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapWorkflowPublishing("/pub"));
            })).Build();

        try
        {
            await host.StartAsync();
            var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();

            await File.WriteAllTextAsync(folder.Config, "<configuration><packageSources><clear/><add key=\"own\" value=\"" +
                SecurityElement.Escape(address + "/pub/nuget/index.json") +
                "\" allowInsecureConnections=\"true\"/></packageSources></configuration>");

            using var client = new HttpClient { BaseAddress = new(address) };
            using var listing = JsonDocument.Parse(await client.GetByteArrayAsync($"/pub/nuget/flatcontainer/{Leaf}/index.json"));
            Assert.Contains(listing.RootElement.GetProperty("versions").EnumerateArray(), version => version.GetString() == "1.0.0");
            requests.Clear();
            var publisher = new WorkflowPublisher(store, store, options);
            var draft = await store.SaveDefinitionAsync(new("Withdrawal", 1, "workflow", Yaml(Root + "@1.0.0")), null);
            Refused((await publisher.ValidateAsync(draft)).Diagnostics, "PUB025");
            Refused((await publisher.PublishAsync(draft.Id)).Diagnostics, "PUB025");

            using var response = await client.PostAsync("/pub/drafts/workflow/Withdrawal/1/validate",
                new StringContent(draft.Source, Encoding.UTF8, "application/yaml"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var diagnostic = Assert.Single(json.RootElement.GetProperty("diagnostics").EnumerateArray());
            Assert.Equal("PUB025", diagnostic.GetProperty("code").GetString());
            Assert.Equal("Conflict", diagnostic.GetProperty("class").GetString());
            Assert.Contains(Leaf + "@1.0.0", diagnostic.GetProperty("message").GetString());
            Assert.Contains($"/pub/nuget/flatcontainer/{Leaf}/index.json", requests);
            Assert.DoesNotContain(requests, path => path.EndsWith(".nupkg", StringComparison.Ordinal));
        }
        finally
        {
            await host.StopAsync();
        }
    }
}