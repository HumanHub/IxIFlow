using System.Net;
using System.Text;
using System.Text.Json;
using IxIFlow.AspNetCore;
using IxIFlow.Authoring;
using IxIFlow.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.Conformance;

public sealed partial class PublishingHttpScenarios
{
    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task FullFeedBackedHttpValidationDoesNotWriteOrStartPublication(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        using var feed = new PublishingFeed();
        var packages = PublishingFeed.Packages();
        await File.WriteAllBytesAsync(Path.Combine(feed.Folder, "fixture.activities.1.0.0.nupkg"), packages.Activity);
        await File.WriteAllBytesAsync(Path.Combine(feed.Folder, "fixture.dependency.1.0.0.nupkg"), packages.Dependency);
        var artifacts = new ArtifactProbe(store);

        await using var factory = new PublishingHttpFactory(store)
            { Artifacts = artifacts, PublisherOptions = new() { NuGetConfigPath = feed.Config } };

        using var client = factory.Operator();
        var before = await store.GetCatalogRevisionAsync();

        var yaml = """
                   workflow: {name: Work, version: 1, dataType: Packaged.Data}
                   imports: {catalogs: [fixture.activities]}
                   definitions: [{kind: activity, id: echo, activity: fixture.echo}]
                   """;

        var response = await client.PostAsync(DraftPath + "/validate", new StringContent(yaml, Encoding.UTF8, "application/yaml"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await Read(response);
        Assert.True(report.GetProperty("isValid").GetBoolean(), report.GetRawText());
        Assert.Equal(before, await store.GetCatalogRevisionAsync());
        Assert.Equal(0, artifacts.Puts);
        Assert.Empty(await store.GetPackagesAsync());
        Assert.Empty(await store.GetWorkflowVersionsAsync());
        Assert.Null(await store.GetDefinitionAsync("workflow", "Work", 1));
        Assert.Empty((await store.GetPublicationsAsync(new(CatalogScope.Unrestricted, "workflow", "Work", 1))).Items);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task RetirementProblemMasksOutsideScopeCallers(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var seed = await Seed(store);

        foreach (var name in new[] { "VisibleCaller", "SecretCaller" })
            await store.RegisterWorkflowVersionAsync(seed.Version with
            {
                Name = name, Dependencies = [new("Published", 1)]
            });

        await using var factory = new PublishingHttpFactory(store);
        factory.Scope.Value = new(new(["Published", "VisibleCaller"]), new(), new(), new());
        using var client = factory.Operator();
        var detail = await client.GetAsync("/pub/catalog/workflows/Published/versions/1");

        var response = await client.SendAsync(Request("POST", "/pub/catalog/workflows/Published/versions/1/retire",
            detail.Headers.ETag!.ToString()));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await Read(response);
        Assert.True(problem.GetProperty("blockers").GetProperty("outsideScope").GetBoolean());
        Assert.Equal("VisibleCaller", problem.GetProperty("blockers").GetProperty("callers")[0].GetProperty("name").GetString());
        Assert.DoesNotContain("SecretCaller", problem.GetRawText());
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task AbandonedAndFailedAttemptsStayReadableAcrossHttpHostsAndScopeAppliesAfterDeletion(string profile)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var database = await StoreProfiles.CreateAsync(profile, time);
        var store = database.NewRepository();

        var draft = await store.SaveDefinitionAsync(
            new("Work", 1, "workflow", "workflow: {name: Work, version: 1, dataType: Missing.Type}\ndefinitions: []"), null);

        var abandoned = await store.BeginPublicationAsync(draft.Id, Guid.NewGuid(), "old-host", TimeSpan.FromSeconds(90));
        time.Advance(TimeSpan.FromMinutes(2));

        // Server profiles use database time rather than FakeTimeProvider; explicitly aging a lease is store conformance's job.
        if (profile.StartsWith("distributed", StringComparison.Ordinal))
            await store.CompletePublicationAsync(abandoned.Id, abandoned.HostId, new(null, [], WorkflowPublishDiagnosticClass.Authoring));

        await using var one = new PublishingHttpFactory(store);
        await using var two = new PublishingHttpFactory(store);
        using var first = one.Operator();
        using var second = two.Operator();
        var response = await first.SendAsync(Request("POST", DraftPath + "/publish", Tag(draft.Id)));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var failed = await Until(second, response.Headers.Location!.ToString());
        Assert.Equal("Failed", failed.GetProperty("state").GetString());
        Assert.Equal(2, failed.GetProperty("attempt").GetInt32());
        var previous = await Read(await second.GetAsync("/pub/publications/" + abandoned.Id));

        Assert.Equal(profile.StartsWith("distributed", StringComparison.Ordinal) ? "Failed" : "Abandoned",
            previous.GetProperty("state").GetString());

        two.Options.PublishWait = TimeSpan.FromSeconds(10);
        var immediateFailure = await second.SendAsync(Request("POST", DraftPath + "/publish", Tag(draft.Id)));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, immediateFailure.StatusCode);
        Assert.Equal(3, (await Read(immediateFailure)).GetProperty("attempt").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync(immediateFailure.Headers.Location)).StatusCode);
        await store.DeleteDefinitionAsync(draft.Id);
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync(response.Headers.Location)).StatusCode);
        two.Scope.Value = new(new([]), new(), new(), new());
        Assert.Equal(HttpStatusCode.NotFound, (await second.GetAsync(response.Headers.Location)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await second.GetAsync(DraftPath + "/publications")).StatusCode);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task WorkflowBundleAndCanonicalSourceAreVerifiedAndLimitedBeforeResponse(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var draft = await store.SaveDefinitionAsync(new("Work", 1, "workflow", TypedPublishingScenarios.Yaml("Work")), null);
        var published = await new WorkflowPublisher(store, store).PublishAsync(draft.Id);
        Assert.True(published.Success);
        var artifacts = new ArtifactProbe(store);

        await using (var factory = new PublishingHttpFactory(store) { Artifacts = artifacts })
        {
            using var client = factory.Operator();
            var response = await client.GetAsync("/pub/catalog/workflows/Work/versions/1/bundle");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal(published.Workflow!.ArtifactDigest, ArtifactDigest.Of(bytes));
            var bundle = await WorkflowBundleArchive.ReadAsync(new MemoryStream(bytes), published.Workflow.ArtifactDigest!.Value);
            var source = await client.GetStringAsync("/pub/catalog/workflows/Work/versions/1/source");
            Assert.Equal(Encoding.UTF8.GetString(bundle.Entries[WorkflowBundleArchive.YamlPath].Span), source);
            artifacts.Corrupt = true;

            Assert.Equal(HttpStatusCode.ServiceUnavailable,
                (await client.GetAsync("/pub/catalog/workflows/Work/versions/1/source")).StatusCode);
        }

        // A download uses the configured bundle bound, independently of package upload bounds.
        await using var limited = new PublishingHttpFactory(store)
        {
            ConfigureServices = services => services.AddSingleton(
                new WorkflowBundleOptions(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")))
                    { ReadLimits = new() { MaxArchiveBytes = 1 } })
        };

        using var limitedClient = limited.Operator();

        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await limitedClient.GetAsync("/pub/catalog/workflows/Work/versions/1/bundle")).StatusCode);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ChunkedUploadAdmissionReturns429WithRetryWhileBoundsStillApply(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();

        await using var factory = new PublishingHttpFactory(store)
            { PublisherOptions = new() { PackageReadLimits = new() { MaxArchiveBytes = 512 } } };

        using var client = factory.Operator();
        using var one = new GatedReadStream();
        using var two = new GatedReadStream();

        Task<Microsoft.AspNetCore.Http.HttpContext> Send(Stream body) => factory.Server.SendAsync(http =>
        {
            http.Request.Method = "POST";
            http.Request.Path = "/pub/catalog/packages";
            http.Request.ContentType = "application/octet-stream";
            http.Request.Body = body;
            http.Request.Headers["X-Test-User"] = "operator";
            http.Request.Headers["X-Test-Policies"] = "publish";
        });

        var first = Send(one);
        var second = Send(two);

        try
        {
            await Task.WhenAll(one.Entered.Task, two.Entered.Task).WaitAsync(TimeSpan.FromSeconds(10));
            using var busy = await client.PostAsync("/pub/catalog/packages", new ByteArrayContent([1]));
            Assert.Equal(HttpStatusCode.TooManyRequests, busy.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(5), busy.Headers.RetryAfter!.Delta);
        }
        finally
        {
            one.Release.TrySetResult();
            two.Release.TrySetResult();
        }

        Assert.Equal(413, (await first).Response.StatusCode);
        Assert.Equal(413, (await second).Response.StatusCode);
        Assert.Empty(await store.GetPackagesAsync());
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task DraftBoundsAndRootIdentityAreCheckedButSemanticErrorsCanBeSaved(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        await using var factory = new PublishingHttpFactory(store);
        factory.Options.MaxDraftBytes = 512;
        using var client = factory.Operator();

        foreach (var source in new[]
                 {
                     TypedPublishingScenarios.Yaml("Other"), "workflow: {name: Work, version: 1}\nactivity: {key: work, version: 1}",
                     "workflow: ["
                 })
        {
            using var request = Request("PUT", DraftPath, content: new StringContent(source, Encoding.UTF8, "application/yaml"));
            request.Headers.TryAddWithoutValidation("If-None-Match", "*");
            Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
        }

        using var oversized = Request("PUT", DraftPath,
            content: new StringContent(new string('x', 513), Encoding.UTF8, "application/yaml"));

        oversized.Headers.TryAddWithoutValidation("If-None-Match", "*");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.SendAsync(oversized)).StatusCode);
        const string invalid = "workflow: {name: Work, version: 1, dataType: Missing.Type}\ndefinitions: []";
        using var save = Request("PUT", DraftPath, content: new StringContent(invalid, Encoding.UTF8, "application/yaml"));
        save.Headers.TryAddWithoutValidation("If-None-Match", "*");
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(save)).StatusCode);
        Assert.Equal(invalid, (await store.GetDefinitionAsync("workflow", "Work", 1))!.Source);
        using var occupied = Request("PUT", DraftPath, content: new StringContent(invalid, Encoding.UTF8, "application/yaml"));
        occupied.Headers.TryAddWithoutValidation("If-None-Match", "*");
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(occupied)).StatusCode);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task DraftMetadataFindsLatestAttemptAcrossAscendingHistoryPages(string profile)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var database = await StoreProfiles.CreateAsync(profile, time);
        var store = database.NewRepository();
        var draft = await store.SaveDefinitionAsync(new("Work", 1, "workflow", TypedPublishingScenarios.Yaml("Work")), null);
        WorkflowPublication latest = null!;

        foreach (var _ in Enumerable.Range(1, 101))
        {
            latest = await store.BeginPublicationAsync(draft.Id, Guid.NewGuid(), "test", TimeSpan.FromSeconds(90));
            await store.CompletePublicationAsync(latest.Id, latest.HostId, new(null, [], WorkflowPublishDiagnosticClass.Authoring));
        }

        await using var factory = new PublishingHttpFactory(store);
        using var client = factory.Operator();
        var detail = await Read(await client.GetAsync(DraftPath));
        Assert.Equal(latest.Id, detail.GetProperty("publication").GetProperty("id").GetGuid());
        Assert.Equal(101, detail.GetProperty("publication").GetProperty("attempt").GetInt32());
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ConflictingPublishingRegistrationsAreTypedRefusals(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);

        await using var factory = new PublishingHttpFactory(database.NewRepository())
            { ConfigureEndpoints = endpoints => endpoints.MapWorkflowPublishing("/pub") };

        var error = Assert.Throws<WorkflowEngineException>(() => factory.CreateClient());
        Assert.Equal(WorkflowErrorKind.InvalidRequest, error.Kind);
    }

    private sealed class GatedReadStream : Stream
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            buffer.Span[..513].Clear();

            return 513;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}