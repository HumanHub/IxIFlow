using System.Net;
using System.Text;
using IxIFlow.Authoring;
using IxIFlow.Core;

namespace IxIFlow.Tests.Conformance;

public sealed partial class PublishingHttpScenarios
{
    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task PinnedAlternativeExcludesItsWithdrawnDependencyFromUnavailableDiagnostics(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var publisher = new WorkflowPublisher(store, store);
        Task<PackageRegistration> Upload(string id, string version = "1.0.0", string dependencies = "") =>
            publisher.UploadPackageAsync(new MemoryStream(PublishingFeed.Package(id, [], dependencies, version)));
        var leaf = (await Upload("pinned.leaf")).Stored;
        await Upload("pinned.middle", dependencies: "<dependency id=\"pinned.leaf\" version=\"[1.0.0]\"/>");
        await Upload("pinned.middle", "2.0.0");
        // Register before withdrawal so both versions' metadata are explored without
        // permanently refusing this pending root's unavailable dependency.
        await Upload("pinned.root", dependencies: "<dependency id=\"pinned.middle\" version=\"[1.0.0,2.0.0]\"/>" +
            "<dependency id=\"pinned.unavailable\" version=\"[1.0.0]\"/>");
        await store.SetPackageWithdrawnAsync(leaf.Id, leaf.Version, true, new(leaf.LifecycleRevision));
        var source = "workflow: {name: Pinned, version: 1, dataType: System.Object}\n" +
            "imports: {catalogs: [pinned.root@1.0.0, pinned.middle@2.0.0]}\ndefinitions: []";
        var draft = await store.SaveDefinitionAsync(new("Pinned", 1, "workflow", source), null);
        var report = await publisher.ValidateAsync(draft);
        var diagnostic = Assert.Single(report.Diagnostics);
        Assert.Equal("PUB041", diagnostic.Code);
        Assert.Equal(WorkflowPublishDiagnosticClass.Unavailable, diagnostic.Class);
        var published = await publisher.PublishAsync(draft.Id);
        Assert.Equal("PUB041", Assert.Single(published.Diagnostics).Code);
        await using var factory = new PublishingHttpFactory(store);
        using var client = factory.Operator();
        using var response = await client.PostAsync("/pub/drafts/workflow/Pinned/1/validate", new StringContent(source, Encoding.UTF8, "application/yaml"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Read(response);
        var httpDiagnostic = Assert.Single(body.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("PUB041", httpDiagnostic.GetProperty("code").GetString());
        Assert.Equal("Unavailable", httpDiagnostic.GetProperty("class").GetString());
        Assert.Equal(draft.Id, (await store.GetDefinitionAsync("workflow", draft.Name, draft.Version))!.Id);
        Assert.Null(await store.GetWorkflowVersionAsync(draft.Name, draft.Version));
    }
}
