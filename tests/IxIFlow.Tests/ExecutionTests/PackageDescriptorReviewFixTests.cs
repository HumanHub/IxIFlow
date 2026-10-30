using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.Json;
using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "registration")]
[Trait("Spec", "F-14")]
[Trait("Spec", "F-12")]
public sealed class PackageDescriptorReviewFixTests
{
    [Theory]
    [InlineData("identity")]
    [InlineData("version")]
    [InlineData("digest")]
    [InlineData("entries")]
    [InlineData("dependencies")]
    public async Task PendingRetryVerifiesStoredRootBeforeExecutingExtraction(string mismatch)
    {
        var folder = Scratch();

        try
        {
            var marker = Path.Combine(folder, "extracted");

            var implementation = Emit("Retry.Activity",
                ActivitySource("string").Replace("[WorkflowActivity", "[ObservedDescription, WorkflowActivity") + $$"""
                      public sealed class ObservedDescriptionAttribute : System.ComponentModel.DescriptionAttribute {
                        public ObservedDescriptionAttribute() { System.IO.File.WriteAllText({{JsonSerializer.Serialize(marker)}}, "extracted"); }
                      }
                      """);

            var bytes = ActivityPackageFixture.Package("retry.actual", "1.0.0",
                new() { ["lib/net10.0/Retry.Activity.dll"] = implementation });

            var store = new InMemoryWorkflowStateRepository();
            var candidate = await new PackageIngestion(store, store).ReadPackageAsync(new MemoryStream(bytes));

            var stored = mismatch switch
            {
                "identity" => candidate.Package with { Id = "retry.stored" },
                "version" => candidate.Package with { Version = "2.0.0" },
                "digest" => candidate.Package with { Digest = ArtifactDigest.Of([1]) },
                "entries" => candidate.Package with
                {
                    Entries = candidate.Package.Entries.ToDictionary(pair => pair.Key, _ => ArtifactDigest.Of([2]))
                },
                _ => candidate.Package with { DependencyGroups = [new("net10.0", [new("retry.missing", "[1.0.0]")])] }
            };

            await store.PutAsync(ArtifactKind.Package, new MemoryStream(bytes));

            if (mismatch == "digest")
                await store.PutAsync(ArtifactKind.Package, new MemoryStream([1]));

            await store.RegisterPackageAsync(stored, new([], PackageExtraction.Pending, "waiting", null, ["retry.missing"]), store);
            var revision = await store.GetCatalogRevisionAsync();
            var artifacts = new SubstitutedArtifact(store, stored.Digest, bytes);
            await Assert.ThrowsAsync<WorkflowEngineException>(() => new PackageIngestion(store, artifacts).RetryAsync(stored, default));
            Assert.False(File.Exists(marker));
            Assert.Equal(revision, await store.GetCatalogRevisionAsync());
            Assert.Equal(stored.Id, Assert.Single(await store.GetPackagesAsync()).Id);

            Assert.Equal(PackageExtraction.Pending,
                (await store.GetPackageDetailAsync(stored.Id, stored.Version, CatalogScope.Unrestricted))!.Summary.Extraction);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task IndexedReuploadRecoversAnIdenticalMissingDependencyFromTheFeed()
    {
        var folder = Scratch();

        try
        {
            var (activity, dependency) = ActivityPackageFixture.Create();
            var store = new InMemoryWorkflowStateRepository();
            var ingestion = new PackageIngestion(store, store);
            await ingestion.UploadPackageAsync(new MemoryStream(dependency));
            var first = await ingestion.UploadPackageAsync(new MemoryStream(activity), "first");
            var pin = Assert.Single(first.Activities.Closure!);
            await store.DeleteAsync(pin.Digest);
            await File.WriteAllBytesAsync(Path.Combine(folder, "fixture.dependency.1.0.0.nupkg"), dependency);
            var config = await FeedConfig(folder);
            var revision = await store.GetCatalogRevisionAsync();

            var replay =
                await new PackageIngestion(store, store, new() { NuGetConfigPath = config }).UploadPackageAsync(new MemoryStream(activity),
                    "second");

            Assert.False(replay.Created);
            Assert.True(await store.ExistsAsync(pin.Digest));
            Assert.Equal(first.Stored.RegisteredAtUtc, replay.Stored.RegisteredAtUtc);
            Assert.Equal("first", replay.Stored.RegisteredBy);
            Assert.Equal(JsonSerializer.Serialize(first.Activities), JsonSerializer.Serialize(replay.Activities));
            Assert.Equal(revision, await store.GetCatalogRevisionAsync());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("Models.Value")]
    [InlineData("System.Collections.Generic.List<Models.Value[]>[,]")]
    public async Task SameDisplayNameFromDifferentMemberAssembliesIsPub014(string memberType)
    {
        var first = Emit("ContractsA", "namespace Models; public sealed class Value { }");
        var second = Emit("ContractsB", "namespace Models; public sealed class Value { }");
        var reference = Emit("Agreement.Activity", ActivitySource(memberType), first);
        var runtime = Emit("Agreement.Activity", ActivitySource(memberType), second);
        var store = new InMemoryWorkflowStateRepository();
        var ingestion = new PackageIngestion(store, store);

        await ingestion.UploadPackageAsync(
            new MemoryStream(ActivityPackageFixture.Package("agreement.a", "1.0.0", new() { ["lib/net10.0/ContractsA.dll"] = first })));

        await ingestion.UploadPackageAsync(
            new MemoryStream(ActivityPackageFixture.Package("agreement.b", "1.0.0", new() { ["lib/net10.0/ContractsB.dll"] = second })));

        var revision = await store.GetCatalogRevisionAsync();

        var bytes = ActivityPackageFixture.Package("agreement.root", "1.0.0", new()
                { ["ref/net10.0/Agreement.Activity.dll"] = reference, ["lib/net10.0/Agreement.Activity.dll"] = runtime },
            "<dependency id=\"agreement.a\" version=\"[1.0.0]\"/><dependency id=\"agreement.b\" version=\"[1.0.0]\"/>");

        var error = await Assert.ThrowsAsync<WorkflowPublishException>(() => ingestion.UploadPackageAsync(new MemoryStream(bytes)));
        Assert.Equal("PUB014", error.Code);
        Assert.Null(await store.GetPackageAsync("agreement.root", "1.0.0"));
        Assert.False(await store.ExistsAsync(ArtifactDigest.Of(bytes)));
        Assert.Equal(revision, await store.GetCatalogRevisionAsync());
    }

    [Fact]
    public async Task MemberAssemblyIdentityNormalizesRuntimeTypeForwarding()
    {
        var referenceTypes = Emit("Forwarder", "namespace Models; public sealed class Value { }");
        var implementationTypes = Emit("Forwarded", "namespace Models; public sealed class Value { }");

        var forwarder = Emit("Forwarder", "[assembly: System.Runtime.CompilerServices.TypeForwardedTo(typeof(Models.Value))]",
            implementationTypes);

        var activity = Emit("Forwarding.Activity", ActivitySource("System.Collections.Generic.List<Models.Value[]>"), referenceTypes);

        var bytes = ActivityPackageFixture.Package("forwarding.root", "1.0.0", new()
        {
            ["ref/net10.0/Forwarder.dll"] = referenceTypes,
            ["ref/net10.0/Forwarding.Activity.dll"] = activity,
            ["lib/net10.0/Forwarder.dll"] = forwarder,
            ["lib/net10.0/Forwarded.dll"] = implementationTypes,
            ["lib/net10.0/Forwarding.Activity.dll"] = activity
        });

        var store = new InMemoryWorkflowStateRepository();
        var receipt = await new PackageIngestion(store, store).UploadPackageAsync(new MemoryStream(bytes));
        Assert.Equal(PackageExtraction.Indexed, receipt.Activities.Extraction);
        Assert.Contains("Forwarded", Assert.Single(Assert.Single(receipt.Activities.Activities).Inputs).Type);
    }

    [Fact]
    public async Task IndexedActivityOnAnExcludedRidDoesNotChangeItsStoredContract()
    {
        var otherRid = RuntimeInformation.RuntimeIdentifier.StartsWith("win", StringComparison.Ordinal) ? "linux-x64" : "win-x64";
        var implementation = Emit("Conditional.Activity", ActivitySource("string"));
        var store = new InMemoryWorkflowStateRepository();
        var ingestion = new PackageIngestion(store, store);

        var dependency = await ingestion.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("conditional.activity", "1.0.0",
            new()
                { ["lib/net10.0/Conditional.Activity.dll"] = implementation })));

        Assert.Single(dependency.Activities.Activities);

        var graph = JsonSerializer.Serialize(new
        {
            runtimes = new Dictionary<string, object>
            {
                [otherRid] = new Dictionary<string, object>
                    { ["conditional.root"] = new Dictionary<string, string> { ["conditional.activity"] = "[1.0.0]" } }
            }
        });

        var root = await ingestion.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("conditional.root", "1.0.0", new()
            { ["runtime.json"] = Encoding.UTF8.GetBytes(graph) })));

        Assert.Equal(PackageExtraction.Indexed, root.Activities.Extraction);

        using var resolved = await new WorkflowPackageResolver(store, store, new()).ResolveAsync(
            new List<string> { "conditional.root@1.0.0" }, PackageAcquisition.CatalogOnly, [], default);

        Assert.False(resolved.Pins.Single(pin => pin.Id == "conditional.activity").AppliesTo(RuntimeInformation.RuntimeIdentifier));
        Assert.Empty(resolved.Activities["conditional.activity"]);

        Assert.Single(
            (await store.GetPackageDetailAsync("conditional.activity", "1.0.0", CatalogScope.Unrestricted))!.Activities.Activities);
    }

    [Fact]
    public async Task CatalogOnlyPresentRootWithUnavailableDependencyIsPub030AndDoesNotReadFeeds()
    {
        var store = new InMemoryWorkflowStateRepository();
        var (activity, _) = ActivityPackageFixture.Create();
        var receipt = await new PackageIngestion(store, store).UploadPackageAsync(new MemoryStream(activity));
        Assert.Equal(PackageExtraction.Pending, receipt.Activities.Extraction);
        var revision = await store.GetCatalogRevisionAsync();

        var error = await Assert.ThrowsAsync<WorkflowPublishException>(() => new WorkflowPackageResolver(store, store,
            new() { NuGetConfigPath = "this-config-must-not-be-read" }).ResolveAsync(new List<string> { "fixture.activities@1.0.0" },
            PackageAcquisition.CatalogOnly, [], default));

        Assert.Equal("PUB030", error.Code);
        Assert.Equal(revision, await store.GetCatalogRevisionAsync());
        Assert.Single(await store.GetPackagesAsync());
    }

    private static string ActivitySource(string type) => $$"""
                                                           using System.Threading; using System.Threading.Tasks; using IxIFlow.Core; using IxIFlow.ActivitySdk;
                                                           [WorkflowActivity("review.activity")]
                                                           public sealed class Activity : IAsyncActivity {
                                                             [ActivityInput] public {{type}} Value {get;set;}
                                                             public Task ExecuteAsync(IActivityContext context,CancellationToken token=default)=>Task.CompletedTask;
                                                           }
                                                           """;

    private static byte[] Emit(string name, string source, params byte[][] dependencies)
    {
        var references = WorkflowCompilation.ReferencePaths().Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .Concat(dependencies.Select(bytes => MetadataReference.CreateFromImage(bytes)));

        var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));

        using var output = new MemoryStream();
        var emitted = compilation.Emit(output);
        Assert.True(emitted.Success, string.Join("; ", emitted.Diagnostics));

        return output.ToArray();
    }

    private static string Scratch()
    {
        var folder = Path.Combine(Path.GetTempPath(), "IxIFlow.DescriptorReview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        return folder;
    }

    private static async Task<string> FeedConfig(string folder)
    {
        var config = Path.Combine(folder, "NuGet.Config");

        await File.WriteAllTextAsync(config,
            "<configuration><packageSources><clear/><add key=\"offline\" value=\"" + SecurityElement.Escape(folder) +
            "\"/></packageSources></configuration>");

        return config;
    }

    private sealed class SubstitutedArtifact(IArtifactStore inner, ArtifactDigest target, byte[] replacement) : IArtifactStore
    {
        public ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken token = default) => digest == target
            ? ValueTask.FromResult<Stream?>(new MemoryStream(replacement, writable: false))
            : inner.OpenAsync(digest, token);

        public ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken token = default) => inner.ExistsAsync(digest, token);

        public ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken token = default) =>
            inner.PutAsync(kind, content, token);

        public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken token = default) => inner.DeleteAsync(digest, token);

        public ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime before, int size,
            CancellationToken token = default) => inner.PurgeUnreferencedAsync(references, before, size, token);
    }
}