using System.Text;
using System.Text.Json;
using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "registration")]
[Trait("Spec", "F-12")]
[Trait("Spec", "F-20")]
public sealed class ActivityPackageReviewFixTests
{
    private static string Folder()
    {
        var path = Path.Combine(Path.GetTempPath(), "IxIFlow.PackageReview", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Theory]
    [InlineData("netstandard2.0")]
    [InlineData("net8.0")]
    public async Task LowerFrameworkReferencesPublishAndLoad(string framework)
    {
        var store = new InMemoryWorkflowStateRepository();
        await new WorkflowPublisher(store, store).UploadPackageAsync(new MemoryStream(ActivityPackageFixture.LowerReferencePackage(framework)));
        var row = await ActivityPackageFixture.PublishAsync(store, yaml: ActivityPackageFixture.Yaml(extraImport: "fixture.lower@1.0.0"));
        using var catalog = new WorkflowCatalog(store, artifacts: store, bundles: new(Folder()));
        using var lease = await catalog.ResolveWorkflowDefinitionAsync(row.Name, 1);
        Assert.Equal(row.StructureHash, WorkflowDefinitionValidator.Inspect(lease.Definition));
    }

    [Fact]
    public async Task LargeClosureStoresNeutralSelectionsOnce()
    {
        var store = new InMemoryWorkflowStateRepository();
        var publisher = new WorkflowPublisher(store, store);
        var dependencies = new StringBuilder();
        for (var i = 0; i < 180; i++)
        {
            var id = "fixture.member" + i;
            await publisher.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package(id, "1.0.0", new())));
            dependencies.Append($"<dependency id=\"{id}\" version=\"[1.0.0]\" />");
        }
        await publisher.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("fixture.large", "1.0.0", new(), dependencies.ToString())));
        var row = await ActivityPackageFixture.PublishAsync(store, yaml: ActivityPackageFixture.Yaml(extraImport: "fixture.large@1.0.0"));
        var manifest = JsonSerializer.Deserialize<WorkflowBundleManifest>(row.ManifestJson!)!;
        Assert.Equal(183, manifest.ActivityPackages.Count);
        Assert.All(manifest.ActivityPackages, pin => Assert.Equal("", Assert.Single(pin.RuntimeAssets).Key));
        Assert.True(Encoding.UTF8.GetByteCount(row.ManifestJson!) < new WorkflowBundleReadLimits().MaxManifestBytes);
    }

    [Theory]
    [InlineData("..", "valid.txt")]
    [InlineData("fixture.safe", "lib/net10.0/CON.dll")]
    [InlineData("fixture.safe", "runtimes/win/native/com1.dll")]
    [InlineData("fixture.safe", "../escaped.txt")]
    public async Task UnsafeIdentityOrEntryIsRefused(string id, string entry)
    {
        var store = new InMemoryWorkflowStateRepository();
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => new WorkflowPublisher(store, store)
            .UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package(id, "1.0.0", new() { [entry] = [1] }))));
        Assert.Contains(error.Kind, new[] { WorkflowErrorKind.InvalidRequest, WorkflowErrorKind.ArtifactMismatch });
        Assert.Empty(await store.GetPackagesAsync());
    }

    [Fact]
    public async Task SharedConstraintsParticipateInCandidateSelection()
    {
        var store = new InMemoryWorkflowStateRepository();
        var publisher = new WorkflowPublisher(store, store);
        await publisher.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("fixture.choice", "1.0.0", new(),
            "<dependency id=\"IxIFlow.Abstractions\" version=\"[99.0.0]\" />")));
        await publisher.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("fixture.choice", "2.0.0", new())));
        await publisher.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("fixture.choice-root", "1.0.0", new(),
            "<dependency id=\"fixture.choice\" version=\"[1.0.0,3.0.0)\" />")));
        var row = await ActivityPackageFixture.PublishAsync(store, yaml: ActivityPackageFixture.Yaml(extraImport: "fixture.choice-root@1.0.0"));
        Assert.Equal("2.0.0", JsonSerializer.Deserialize<WorkflowBundleManifest>(row.ManifestJson!)!.ActivityPackages.Single(pin => pin.Id == "fixture.choice").Version);
    }

    [Fact]
    public async Task RuntimeGraphDependencyIsRequiredOnlyOnItsPlatform()
    {
        var store = new InMemoryWorkflowStateRepository();
        var publisher = new WorkflowPublisher(store, store);
        var other = OperatingSystem.IsWindows() ? "linux-x64" : "win-x64";
        var graph = JsonSerializer.Serialize(new { runtimes = new Dictionary<string, object> { [other] = new Dictionary<string, object> { ["fixture.rid-root"] = new Dictionary<string, string> { ["fixture.other"] = "[1.0.0]" } } } });
        await publisher.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("fixture.rid-root", "1.0.0", new() { ["runtime.json"] = Encoding.UTF8.GetBytes(graph) })));
        await publisher.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("fixture.other", "1.0.0", new() { [$"runtimes/{other}/native/other.bin"] = [1] })));
        var row = await ActivityPackageFixture.PublishAsync(store, yaml: ActivityPackageFixture.Yaml(extraImport: "fixture.rid-root@1.0.0"));
        var manifest = JsonSerializer.Deserialize<WorkflowBundleManifest>(row.ManifestJson!)!;
        var pin = manifest.ActivityPackages.Single(package => package.Id == "fixture.other");
        Assert.True(pin.AppliesTo(other));
        Assert.False(pin.AppliesTo(System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier));
        Assert.True(WorkflowSharedAssemblies.IsCompatible(manifest));
        using var catalog = new WorkflowCatalog(store, artifacts: store, bundles: new(Folder()));
        using var lease = await catalog.ResolveWorkflowDefinitionAsync(row.Name, 1);
        Assert.NotNull(lease.Definition);
    }

    [Fact]
    public async Task ReferenceOnlyPackageCannotUseThePublishersImplementation()
    {
        var store = new InMemoryWorkflowStateRepository();
        var implementation = File.ReadAllBytes(typeof(IxIFlow.BundleFixtures.FirstData).Assembly.Location);
        await new WorkflowPublisher(store, store).UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("fixture.ref-only", "1.0.0",
            new() { ["ref/net10.0/System.Payments.dll"] = implementation })));
        var imports = new List<string> { "fixture.ref-only@1.0.0" };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowPackageResolver(store, store, new()).ResolveAsync(imports, default));
        Assert.Contains("runtime implementation", error.Message);
    }

    [Fact]
    public async Task PrereleaseIdentityUsesOneCaseInsensitiveKey()
    {
        var store = new InMemoryWorkflowStateRepository();
        var publisher = new WorkflowPublisher(store, store);
        var first = await publisher.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("fixture.case", "1.0.0-Beta", new())));
        Assert.Equal("1.0.0-beta", first.Version);
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => publisher.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("fixture.case", "1.0.0-beta", new()))));
        Assert.Equal(WorkflowErrorKind.InvalidRequest, error.Kind);
        Assert.Equal(first.Digest, Assert.Single(await store.GetPackagesAsync()).Digest);
    }

    [Fact]
    public async Task LatestSkipsCollectedIdentitiesAndExactPinsCanRefetchMatchingContent()
    {
        var root = Folder();
        var store = new InMemoryWorkflowStateRepository();
        var publisher = new WorkflowPublisher(store, store);
        var first = ActivityPackageFixture.Package("fixture.ghost", "1.0.0", new());
        var second = ActivityPackageFixture.Package("fixture.ghost", "2.0.0", new());
        await publisher.UploadPackageAsync(new MemoryStream(first));
        var ghost = await publisher.UploadPackageAsync(new MemoryStream(second));
        await store.DeleteAsync(ghost.Digest);
        using (var selected = await new WorkflowPackageResolver(store, store, new()).ResolveAsync(new List<string> { "fixture.ghost" }, default))
            Assert.Equal("1.0.0", Assert.Single(selected.Pins).Version);
        await File.WriteAllBytesAsync(Path.Combine(root, "fixture.ghost.2.0.0.nupkg"), second);
        var config = Path.Combine(root, "NuGet.Config");
        await File.WriteAllTextAsync(config, $"<configuration><packageSources><clear/><add key=\"fixture\" value=\"{root}\"/></packageSources></configuration>");
        using var restored = await new WorkflowPackageResolver(store, store, new() { NuGetConfigPath = config })
            .ResolveAsync(new List<string> { "fixture.ghost@2.0.0" }, default);
        Assert.Equal(ghost.Digest, Assert.Single(restored.Pins).Digest);
        Assert.True(await store.ExistsAsync(ghost.Digest));
        await store.DeleteAsync(ghost.Digest);
        await File.WriteAllBytesAsync(Path.Combine(root, "fixture.ghost.2.0.0.nupkg"), ActivityPackageFixture.Package("fixture.ghost", "2.0.0", new() { ["changed.txt"] = [1] }));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowPackageResolver(store, store, new() { NuGetConfigPath = config })
            .ResolveAsync(new List<string> { "fixture.ghost@2.0.0" }, default));
        Assert.Contains("immutable recorded digest", error.Message);
    }

    [Fact]
    public async Task FeedCandidatesUseMetadataWithoutExpandingUnselectedArchives()
    {
        var root = Folder();
        foreach (var (version, entries) in new[] { ("1.0.0", new Dictionary<string, byte[]>()), ("2.0.0", new Dictionary<string, byte[]> { ["too-big.bin"] = new byte[2048] }) })
            await File.WriteAllBytesAsync(Path.Combine(root, "fixture.candidate." + version + ".nupkg"), ActivityPackageFixture.Package("fixture.candidate", version, entries));
        await File.WriteAllBytesAsync(Path.Combine(root, "fixture.root.1.0.0.nupkg"), ActivityPackageFixture.Package("fixture.root", "1.0.0", new(),
            "<dependency id=\"fixture.candidate\" version=\"[1.0.0,3.0.0)\"/>"));
        var config = Path.Combine(root, "NuGet.Config");
        await File.WriteAllTextAsync(config, $"<configuration><packageSources><clear/><add key=\"offline\" value=\"{root}\"/></packageSources></configuration>");
        var store = new InMemoryWorkflowStateRepository();
        using var selected = await new WorkflowPackageResolver(store, store, new() { NuGetConfigPath = config, PackageReadLimits = new() { MaxEntryBytes = 1024 } })
            .ResolveAsync(new List<string> { "fixture.root@1.0.0" }, default);
        Assert.Equal("1.0.0", selected.Pins.Single(pin => pin.Id == "fixture.candidate").Version);
        Assert.Equal(2, (await store.GetPackagesAsync()).Count);
    }

    [Theory]
    [InlineData("archive")]
    [InlineData("entries")]
    [InlineData("entry")]
    [InlineData("expanded")]
    public async Task PackageReadLimitsApplyAtUploadAndColdHostLoad(string bound)
    {
        var store = new InMemoryWorkflowStateRepository();
        var bytes = ActivityPackageFixture.Create().Activity;
        var limits = bound switch
        {
            "archive" => new WorkflowBundleReadLimits { MaxArchiveBytes = bytes.Length - 1 },
            "entries" => new() { MaxEntries = 1 },
            "entry" => new() { MaxEntryBytes = 1 },
            _ => new() { MaxExpandedBytes = 1 }
        };
        var upload = await Assert.ThrowsAsync<WorkflowEngineException>(() => new WorkflowPublisher(store, store, new() { PackageReadLimits = limits }).UploadPackageAsync(new MemoryStream(bytes)));
        Assert.Equal(WorkflowErrorKind.ResourceExhausted, upload.Kind);
        var pin = await new WorkflowPublisher(store, store).UploadPackageAsync(new MemoryStream(bytes));
        var packagePin = new WorkflowPackagePin(pin.Id, pin.Version, pin.Digest, pin.Entries, [], new Dictionary<string, WorkflowPackageAssets> { [""] = new([], []) }, false);
        var host = await Assert.ThrowsAsync<WorkflowEngineException>(() => new BundleCache(store, Folder(), limits, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)).ReadPackageAsync(packagePin, default));
        Assert.Equal(WorkflowErrorKind.ResourceExhausted, host.Kind);
    }

    [Fact]
    public async Task DuplicateNativeNamesAreRefusedAtPublish()
    {
        var store = new InMemoryWorkflowStateRepository();
        var publisher = new WorkflowPublisher(store, store);
        foreach (var (id, file) in new[] { ("fixture.native-one", "duplicate.dll"), ("fixture.native-two", "DUPLICATE.dll") })
            await publisher.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package(id, "1.0.0", new() { ["runtimes/win/native/" + file] = [1] })));
        await publisher.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("fixture.duplicates", "1.0.0", new(),
            "<dependency id=\"fixture.native-one\" version=\"[1.0.0]\"/><dependency id=\"fixture.native-two\" version=\"[1.0.0]\"/>")));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowPackageResolver(store, store, new()).ResolveAsync(new List<string> { "fixture.duplicates@1.0.0" }, default));
        Assert.Contains("Duplicate native", error.Message);
    }

    [Theory]
    [InlineData("unexpected")]
    [InlineData("oversized")]
    [InlineData("corrupt")]
    public async Task NativeCacheIsCheckedAsOneCompleteSetBeforeLoading(string defect)
    {
        var store = new InMemoryWorkflowStateRepository();
        await new WorkflowPublisher(store, store).UploadPackageAsync(new MemoryStream(ActivityPackageFixture.NativeActivityPackage()));
        var row = await ActivityPackageFixture.PublishAsync(store, yaml: ActivityPackageFixture.Yaml(extraImport: "fixture.native-call@1.0.0").Replace("activity: fixture.echo", "activity: fixture.native-call"));
        var manifest = JsonSerializer.Deserialize<WorkflowBundleManifest>(row.ManifestJson!)!;
        var pin = manifest.ActivityPackages.Single(package => package.Id == "fixture.native-call");
        var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        var root = Folder();
        var native = Path.Combine(root, "native", row.ArtifactDigest!.Value.Value[7..], rid);
        Directory.CreateDirectory(native);
        var file = Path.Combine(native, Path.GetFileName(Assert.Single(pin.RuntimeAssets[rid].NativeLibraries)));
        var bytes = ActivityPackageFixture.NativeLibraryBytes();
        if (defect == "oversized")
        { using var stream = File.Create(file); stream.SetLength(new WorkflowBundleReadLimits().MaxEntryBytes + 1L); }
        else if (defect == "corrupt") { bytes[0] ^= 1; await File.WriteAllBytesAsync(file, bytes); }
        else await File.WriteAllBytesAsync(Path.Combine(native, "undeclared.dll"), [1]);
        using var catalog = new WorkflowCatalog(store, artifacts: store, bundles: new(root));
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => catalog.ResolveWorkflowDefinitionAsync(row.Name, 1));
        Assert.Equal(WorkflowErrorKind.ArtifactMismatch, error.Kind);
    }

    [Fact]
    public async Task FetchBudgetIsRenewedForEveryArtifact()
    {
        var store = new InMemoryWorkflowStateRepository();
        var row = await ActivityPackageFixture.PublishAsync(store);
        using var catalog = new WorkflowCatalog(store, artifacts: new DelayedArtifacts(store), bundles: new(Folder()) { FetchTimeout = TimeSpan.FromMilliseconds(700) });
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var lease = await catalog.ResolveWorkflowDefinitionAsync(row.Name, 1);
        Assert.True(watch.Elapsed > TimeSpan.FromMilliseconds(700));
        Assert.NotNull(lease.Definition);
    }

    [Fact]
    public async Task NativeMaterializationWaitsForTheDigestLockAndReusesVerifiedFiles()
    {
        var store = new InMemoryWorkflowStateRepository();
        await new WorkflowPublisher(store, store).UploadPackageAsync(new MemoryStream(ActivityPackageFixture.NativeActivityPackage()));
        var row = await ActivityPackageFixture.PublishAsync(store, yaml: ActivityPackageFixture.Yaml(extraImport: "fixture.native-call@1.0.0").Replace("activity: fixture.echo", "activity: fixture.native-call"));
        var root = Folder();
        using var catalog = new WorkflowCatalog(store, artifacts: store, bundles: new(root));
        using (var held = new FileStream(Path.Combine(root, row.ArtifactDigest!.Value.Value[7..] + ".native.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.ResolveWorkflowDefinitionAsync(row.Name, 1, stop.Token));
            Assert.False(Directory.Exists(Path.Combine(root, "native")));
        }
        using var first = await catalog.ResolveWorkflowDefinitionAsync(row.Name, 1);
        var file = Assert.Single(Directory.GetFiles(Path.Combine(root, "native"), "*", SearchOption.AllDirectories));
        var written = File.GetLastWriteTimeUtc(file);
        using var other = new WorkflowCatalog(store, artifacts: store, bundles: new(root));
        using var second = await other.ResolveWorkflowDefinitionAsync(row.Name, 1);
        Assert.NotSame(first.Definition, second.Definition);
        Assert.Equal(written, File.GetLastWriteTimeUtc(file));
        Assert.Equal(ActivityPackageFixture.NativeLibraryBytes(), await File.ReadAllBytesAsync(file));
    }

    [Fact]
    public async Task MissingConfigAndMalformedUploadsHaveSafeDiagnostics()
    {
        var store = new InMemoryWorkflowStateRepository();
        var config = Path.Combine(Folder(), "missing.config");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowPackageResolver(store, store, new() { NuGetConfigPath = config }).ResolveAsync(new List<string> { "fixture.absent@1.0.0" }, default));
        Assert.Contains("NuGet.Config", error.Message);
        var invalid = await Assert.ThrowsAsync<WorkflowEngineException>(() => new WorkflowPublisher(store, store).UploadPackageAsync(new MemoryStream([1, 2, 3])));
        Assert.Equal(WorkflowErrorKind.InvalidRequest, invalid.Kind);
        var badNuspec = ActivityPackageFixture.Package("fixture.invalid", "not-a-version", new());
        invalid = await Assert.ThrowsAsync<WorkflowEngineException>(() => new WorkflowPublisher(store, store).UploadPackageAsync(new MemoryStream(badNuspec)));
        Assert.Equal(WorkflowErrorKind.InvalidRequest, invalid.Kind);
        Assert.Null(invalid.InnerException);
    }

    private sealed class DelayedArtifacts(IArtifactStore inner) : IArtifactStore
    {
        public async ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken token = default)
        { await Task.Delay(300, token); return await inner.OpenAsync(digest, token); }
        public ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken token = default) => inner.ExistsAsync(digest, token);
        public ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream stream, CancellationToken token = default) => inner.PutAsync(kind, stream, token);
        public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken token = default) => inner.DeleteAsync(digest, token);
        public ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> refs, DateTime cutoff, int batch, CancellationToken token = default) => inner.PurgeUnreferencedAsync(refs, cutoff, batch, token);
    }
}
