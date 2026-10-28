using System.Security.Claims;
using IxIFlow.AspNetCore;
using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "authoring")]
[Trait("Spec", "F-09")]
[Trait("Spec", "F-14")]
public sealed class PublishingUploadOrchestrationScenarios
{
    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ProposedNormalizedIdentityIsAuthorizedBeforeWritesAndReceiptsUseStoredWinner(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        using var feed = new PublishingFeed();
        var scope = new Scope();
        var allow = false;
        WorkflowPackage? proposed = null;
        var services = new ServiceCollection().AddLogging();
        services.AddAuthorization(options => options.AddPolicy("publish", policy => policy.RequireAssertion(context =>
        {
            proposed = Assert.IsType<WorkflowPackage>(context.Resource);
            return allow;
        })));
        var options = new WorkflowPublisherOptions { NuGetConfigPath = feed.Config };
        services.AddIxIFlow().AddWorkflowPublishing(new() { PublishPolicy = "publish" }, options);
        services.AddSingleton<IWorkflowStateRepository>(store);
        services.AddSingleton<ICatalogQueryScope>(scope);
        await using var provider = services.BuildServiceProvider();
        Assert.Same(options, provider.GetRequiredService<WorkflowPublisherOptions>());
        Assert.Contains(provider.GetServices<IHostedService>(), service => service is PublicationService);
        Assert.Contains(provider.GetServices<IHostedService>(), service => service is PackageIndexingService);
        var uploads = provider.GetRequiredService<WorkflowPackageUploadService>();
        var bytes = PublishingFeed.Package("Mixed.Case", []);
        HttpContext Context()
        {
            var http = new DefaultHttpContext { RequestServices = provider, User = new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "first")], "test")) };
            http.Request.Body = new MemoryStream(bytes);
            http.Request.PathBase = "/api";
            return http;
        }
        var revision = await store.GetCatalogRevisionAsync();
        var denied = await uploads.UploadAsync(Context(), "/publishing/catalog/packages");
        Assert.Equal(403, ((IStatusCodeHttpResult)denied).StatusCode);
        Assert.Equal("mixed.case", proposed!.Id);
        Assert.Equal(revision, await store.GetCatalogRevisionAsync());
        Assert.Empty(await store.GetPackagesAsync());
        scope.Value = new(new(), new(), new([]), new());
        allow = true;
        Assert.Equal(404, ((IStatusCodeHttpResult)await uploads.UploadAsync(Context(), "/publishing/catalog/packages")).StatusCode);
        Assert.Equal(revision, await store.GetCatalogRevisionAsync());
        scope.Value = CatalogScope.Unrestricted;
        var firstContext = Context();
        Assert.Equal(201, ((IStatusCodeHttpResult)await uploads.UploadAsync(firstContext, "/publishing/catalog/packages")).StatusCode);
        Assert.Equal("/api/publishing/catalog/packages/mixed.case/1.0.0", firstContext.Response.Headers.Location.ToString());
        var replayContext = Context();
        replayContext.User = new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "second")], "test"));
        var replay = await uploads.UploadAsync(replayContext, "/publishing/catalog/packages");
        Assert.Equal(200, ((IStatusCodeHttpResult)replay).StatusCode);
        Assert.Equal("first", Assert.IsType<WorkflowPackageResponse>(((IValueHttpResult)replay).Value).Package.RegisteredBy);
        var delegated = Context(); delegated.Request.Headers["X-IxIFlow-Actor"] = "delegate";
        Assert.Equal(403, ((IStatusCodeHttpResult)await uploads.UploadAsync(delegated, "/publishing/catalog/packages")).StatusCode);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task AdmissionCoversChunkedReadsAndArchiveExpandedLimits(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var services = new ServiceCollection().AddLogging();
        services.AddAuthorization(options => options.AddPolicy("publish", policy => policy.RequireAssertion(_ => true)));
        services.AddSingleton<ICatalogQueryScope>(new Scope());
        await using var provider = services.BuildServiceProvider();
        var publisherOptions = new WorkflowPublisherOptions { PackageReadLimits = new() { MaxArchiveBytes = 1024, MaxExpandedBytes = 2048 } };
        using var uploads = new WorkflowPackageUploadService(new(store, store, publisherOptions), publisherOptions,
            new() { PublishPolicy = "publish" }, provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<WorkflowPackageUploadService>>());
        HttpContext Context(Stream body) => new DefaultHttpContext
        {
            RequestServices = provider,
            User = new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "author")], "test")),
            Request = { Body = body }
        };
        var first = new GatedReadStream(new byte[2049]);
        var second = new GatedReadStream(new byte[2049]);
        var one = uploads.UploadAsync(Context(first), "/catalog/packages");
        var two = uploads.UploadAsync(Context(second), "/catalog/packages");
        try
        {
            await Task.WhenAll(first.Entered.Task, second.Entered.Task).WaitAsync(TimeSpan.FromSeconds(10));
            var third = Context(new MemoryStream([1]));
            Assert.Equal(429, ((IStatusCodeHttpResult)await uploads.UploadAsync(third, "/catalog/packages")).StatusCode);
            Assert.Equal("5", third.Response.Headers.RetryAfter.ToString());
            Assert.Equal(0, third.Request.Body.Position);
        }
        finally { first.Release.TrySetResult(); second.Release.TrySetResult(); }
        var bounded = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult>(await one);
        Assert.Equal(413, bounded.StatusCode);
        Assert.Equal("PUB042", bounded.ProblemDetails.Extensions["code"]);
        Assert.Equal("Limit", bounded.ProblemDetails.Extensions["class"]);
        Assert.Equal(413, ((IStatusCodeHttpResult)await two).StatusCode);
        var expanded = PublishingFeed.Package("expanded", new() { ["content/large.txt"] = new byte[4096] });
        Assert.True(expanded.Length < publisherOptions.PackageReadLimits.MaxArchiveBytes);
        Assert.Equal(413, ((IStatusCodeHttpResult)await uploads.UploadAsync(Context(new MemoryStream(expanded)), "/catalog/packages")).StatusCode);
        var declared = Context(new MemoryStream()); declared.Request.ContentLength = 1025;
        Assert.Equal(413, ((IStatusCodeHttpResult)await uploads.UploadAsync(declared, "/catalog/packages")).StatusCode);
        Assert.Empty(await store.GetPackagesAsync());
    }

    [Fact]
    public void IdentityUsesTheSingleRootReaderAndKindSpecificKeyRules()
    {
        var workflow = WorkflowDocumentKinds.Identify(TypedPublishingScenarios.Yaml());
        Assert.Equal(("workflow", "Typed", 1), (workflow.Kind, workflow.Name, workflow.Version));
        Assert.True(workflow.Location.Line > 1);
        var activity = WorkflowDocumentKinds.Identify("activity: {key: example.echo, version: 2}\nbody: return;");
        Assert.Equal(("activity", "example.echo", 2), (activity.Kind, activity.Name, activity.Version));
        Assert.ThrowsAny<Exception>(() => WorkflowDocumentKinds.Identify("workflow: {name: One}\nactivity: {key: two, version: 1}"));
        Assert.ThrowsAny<Exception>(() => WorkflowDocumentKinds.Identify("activity: {key: Invalid, version: 1}\nbody: return;"));
        Assert.ThrowsAny<Exception>(() => WorkflowDocumentKinds.Identify("activity: {key: valid, version: 65535}\nbody: return;"));
        Assert.ThrowsAny<Exception>(() => WorkflowDocumentKinds.Identify("workflow: {name: Name, version: 0}"));
        Assert.ThrowsAny<Exception>(() => WorkflowDocumentKinds.Identify("workflow: {name: 'A\0B'}"));
    }

    [Fact]
    public void UnionRequiresExactlyTheMatchingArmAndReceiptsPreserveFullIdentity()
    {
        var id = new WorkflowDraftId("activity", "example.echo", 2, Guid.NewGuid(), 3);
        var package = new WorkflowPackage(id.Name, "2.0.0", ArtifactDigest.Of([1]), new Dictionary<string, ArtifactDigest>(), [])
        { PublishedDraft = id, PublishedAtUtc = DateTime.UtcNow, PublishedBy = "publisher" };
        var activity = new WorkflowPublishResult("activity", null, package, null, []);
        Assert.True(activity.Success);
        Assert.Equal(id, activity.Receipt!.Draft);
        Assert.Equal(id, WorkflowPublishResult.FromReceipt(activity.Receipt).Package!.PublishedDraft);
        Assert.False((activity with { Kind = "workflow" }).Success);
        var templateId = id with { Kind = "template", Name = "example.template" };
        var template = new IxIFlow.Templates.TemplateVersionEntry(
            IxIFlow.Templates.TemplateVersion.Create<PreviewModel>(templateId.Name, 2, "liquid", IxIFlow.Templates.TemplateFormat.Text,
                new IxIFlow.Templates.TemplateVariant("", "{{ Value }}")), templateId, DateTime.UtcNow, "publisher");
        var publishedTemplate = new WorkflowPublishResult("template", null, null, template, []);
        Assert.True(publishedTemplate.Success);
        Assert.Equal(templateId, WorkflowPublishResult.FromReceipt(publishedTemplate.Receipt!).Template!.PublishedDraft);
        Assert.False((activity with { Template = template }).Success);
        Assert.Null((activity with { Template = template }).Receipt);
        var error = new WorkflowPublishDiagnostic(WorkflowPublishStage.Compile, "TEST", "Invalid source", IxIFlow.Dsl.Compilation.WorkflowDiagnosticSeverity.Error, "activity.yaml", 1, 1)
        { Class = WorkflowPublishDiagnosticClass.Authoring };
        var failed = activity with { Diagnostics = [error] };
        Assert.False(failed.Success); Assert.Null(failed.Receipt);
        Assert.Equal(WorkflowPublishDiagnosticClass.Authoring, failed.FailureClass);
    }

    public sealed class PreviewModel { public string Value { get; set; } = ""; }

    private sealed class Scope : ICatalogQueryScope
    {
        internal CatalogScope Value = CatalogScope.Unrestricted;
        public ValueTask<CatalogScope> ScopeAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default) => ValueTask.FromResult(Value);
    }

    private sealed class GatedReadStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(token); return await _inner.ReadAsync(buffer, token); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
