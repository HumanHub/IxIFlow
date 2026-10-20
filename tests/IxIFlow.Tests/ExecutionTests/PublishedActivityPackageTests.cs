using System.Runtime.Loader;
using System.Text.Json;
using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Spec", "F-12")]
[Trait("Spec", "F-20")]
[Trait("Spec", "F-04")]
[Trait("Feature", "registration")]
public sealed class PublishedActivityPackageTests
{
    private static string Folder()
    {
        var path = Path.Combine(Path.GetTempPath(), "IxIFlow.PackageTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UploadedPackageActivityRunsAfterRecoveryOnAnotherHost(bool sqlite)
    {
        var root = Folder();
        var memory = new InMemoryWorkflowStateRepository();
        var connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "state.db") }.ConnectionString;
        await ActivityPackageFixture.CrossHostAsync(() => sqlite ? new SqliteWorkflowStateRepository(connection) : memory, root);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UploadAndLocalFeedPinTheSameTransitiveClosure(bool feed)
    {
        var store = new InMemoryWorkflowStateRepository();
        WorkflowPublisherOptions? options = null;
        if (feed)
        {
            var root = Folder();
            var fixtures = ActivityPackageFixture.Create();
            await File.WriteAllBytesAsync(Path.Combine(root, "fixture.activities.1.0.0.nupkg"), fixtures.Activity);
            await File.WriteAllBytesAsync(Path.Combine(root, "fixture.dependency.1.0.0.nupkg"), fixtures.Dependency);
            var config = Path.Combine(root, "NuGet.Config");
            await File.WriteAllTextAsync(config, "<configuration><packageSources><clear/><add key=\"fixtures\" value=\".\"/></packageSources></configuration>");
            options = new() { NuGetConfigPath = config };
        }
        var version = await ActivityPackageFixture.PublishAsync(store, options: options, upload: !feed);
        var manifest = JsonSerializer.Deserialize<WorkflowBundleManifest>(version.ManifestJson!)!;
        Assert.Equal(new[] { "fixture.activities", "fixture.dependency" }, manifest.ActivityPackages.Select(package => package.Id));
        Assert.All(manifest.ActivityPackages, package => Assert.Equal("1.0.0", package.Version));
        Assert.All(manifest.ActivityPackages, package => Assert.NotEmpty(package.Entries));
        Assert.All(manifest.ActivityPackages, package => Assert.StartsWith("ref/", Assert.Single(package.ReferenceAssemblies)));
        var catalog = await store.GetPackagesAsync();
        Assert.Equal(2, catalog.Count);
        foreach (var pin in manifest.ActivityPackages)
        {
            Assert.Equal(catalog.Single(package => package.Id == pin.Id).Digest, pin.Digest);
            Assert.Contains(pin.Digest, version.ArtifactReferences!);
            Assert.True(await store.ExistsAsync(pin.Digest));
        }
        await using var stream = (await store.OpenAsync(version.ArtifactDigest!.Value))!;
        var bundle = await WorkflowBundleArchive.ReadAsync(stream, version.ArtifactDigest.Value);
        Assert.DoesNotContain(bundle.Entries.Keys, path => path.Contains("Package.Activities") || path.Contains("Package.Dependency") || path.EndsWith(".nupkg"));
        if (feed)
        {
            var root = Path.GetDirectoryName(options!.NuGetConfigPath)!;
            File.Delete(options.NuGetConfigPath!);
            File.Delete(Path.Combine(root, "fixture.activities.1.0.0.nupkg"));
            File.Delete(Path.Combine(root, "fixture.dependency.1.0.0.nupkg"));
        }
        // Once published, execution succeeds with only the artifact store, even after feeds disappear.
        await using var host = PublishedBundleScenario.Host(store, Folder());
        using var definition = await host.GetRequiredService<IWorkflowCatalog>().ResolveWorkflowDefinitionAsync(version.Name, 1);
        Assert.Equal("Packaged.Data", definition.Definition.WorkflowDataType!.FullName);
        Assert.DoesNotContain(AssemblyLoadContext.Default.Assemblies, assembly => assembly.GetName().Name == "Package.Activities");
    }

    [Fact]
    public async Task LatestIsResolvedAndCanonicalYamlIsPinned()
    {
        var store = new InMemoryWorkflowStateRepository();
        var publisher = new WorkflowPublisher(store, store);
        foreach (var version in new[] { 1, 2 })
        {
            var fixture = ActivityPackageFixture.Create(version);
            await publisher.UploadPackageAsync(new MemoryStream(fixture.Activity));
            await publisher.UploadPackageAsync(new MemoryStream(fixture.Dependency));
        }
        var yaml = ActivityPackageFixture.Yaml().Replace("fixture.activities@1.0.0", "fixture.activities");
        var versionRow = await ActivityPackageFixture.PublishAsync(store, upload: false, yaml: yaml);
        var manifest = JsonSerializer.Deserialize<WorkflowBundleManifest>(versionRow.ManifestJson!)!;
        Assert.All(manifest.ActivityPackages, package => Assert.Equal("2.0.0", package.Version));
        await using var stream = (await store.OpenAsync(versionRow.ArtifactDigest!.Value))!;
        var bundle = await WorkflowBundleArchive.ReadAsync(stream, versionRow.ArtifactDigest.Value);
        Assert.Contains("fixture.activities@2.0.0", System.Text.Encoding.UTF8.GetString(bundle.Entries[WorkflowBundleArchive.YamlPath].Span));
    }

    [Theory]
    [InlineData("<dependency id=\"IxIFlow.Abstractions\" version=\"[99.0.0]\" />")]
    [InlineData("<dependency id=\"fixture.unavailable\" version=\"[99.0.0]\" />")]
    public async Task AnUnselectedTransitiveCandidateDoesNotInvalidateTheResolvedClosure(string unusableDependency)
    {
        var store = new InMemoryWorkflowStateRepository();
        var publisher = new WorkflowPublisher(store, store);
        await publisher.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("fixture.selection", "1.0.0", new(),
            "<dependency id=\"fixture.candidate\" version=\"[1.0.0,3.0.0)\" />")));
        await publisher.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("fixture.candidate", "1.0.0", new())));
        await publisher.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("fixture.candidate", "2.0.0", new(), unusableDependency)));
        var version = await ActivityPackageFixture.PublishAsync(store, yaml: ActivityPackageFixture.Yaml(extraImport: "fixture.selection@1.0.0"));
        var manifest = JsonSerializer.Deserialize<WorkflowBundleManifest>(version.ManifestJson!)!;
        Assert.Equal("1.0.0", manifest.ActivityPackages.Single(package => package.Id == "fixture.candidate").Version);
        Assert.DoesNotContain(manifest.ActivityPackages, package => package.Id == "fixture.unavailable");
    }

    [Theory]
    [InlineData("[99.0.0]")]
    [InlineData("[0.1.0,0.2.0)")]
    public async Task SharedVersionConflictIsRefusedBeforePublication(string range)
    {
        var store = new InMemoryWorkflowStateRepository();
        var publisher = new WorkflowPublisher(store, store);
        var fixture = ActivityPackageFixture.Create();
        await publisher.UploadPackageAsync(new MemoryStream(fixture.Activity));
        await publisher.UploadPackageAsync(new MemoryStream(fixture.Dependency));
        await publisher.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("fixture.conflict", "1.0.0", new(),
            $"<dependency id=\"IxIFlow.Abstractions\" version=\"{range}\"/>")));
        var draft = await store.SaveDefinitionAsync(new("PackageLifecycle", 1, "workflow",
            ActivityPackageFixture.Yaml(extraImport: "fixture.conflict@1.0.0")), 0);
        var result = await publisher.PublishAsync(draft.Id);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("shared assembly") && diagnostic.Line > 1);
        Assert.Null(await store.GetWorkflowVersionAsync(draft.Name, 1));
        Assert.NotNull(await store.GetDefinitionAsync(draft.Name, 1));
    }

    [Fact]
    public async Task AnAssemblyReferenceToADifferentSharedVersionIsRefused()
    {
        var store = new InMemoryWorkflowStateRepository();
        var publisher = new WorkflowPublisher(store, store);
        var fixture = ActivityPackageFixture.Create();
        await publisher.UploadPackageAsync(new MemoryStream(fixture.Activity));
        await publisher.UploadPackageAsync(new MemoryStream(fixture.Dependency));
        await publisher.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.SharedAssemblyConflictPackage()));
        var draft = await store.SaveDefinitionAsync(new("PackageLifecycle", 1, "workflow", ActivityPackageFixture.Yaml(extraImport: "fixture.assembly-conflict@1.0.0")), 0);
        var result = await publisher.PublishAsync(draft.Id);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Message.Contains("IxIFlow.Abstractions") && diagnostic.Message.Contains("99.0.0.0"));
        Assert.Null(await store.GetWorkflowVersionAsync(draft.Name, 1));
    }

    [Theory]
    [InlineData("target: Text", "target: Unmarked")]
    [InlineData("source: Result\n", "source: Text\n")]
    [InlineData("target: Text", "target: Missing")]
    public async Task PackageMetadataValidatesMarkersAndMembers(string from, string to)
    {
        var store = new InMemoryWorkflowStateRepository();
        var publisher = new WorkflowPublisher(store, store);
        var fixture = ActivityPackageFixture.Create();
        await publisher.UploadPackageAsync(new MemoryStream(fixture.Activity));
        await publisher.UploadPackageAsync(new MemoryStream(fixture.Dependency));
        var draft = await store.SaveDefinitionAsync(new("PackageLifecycle", 1, "workflow", ActivityPackageFixture.Yaml().Replace(from, to)), 0);
        var result = await publisher.PublishAsync(draft.Id);
        Assert.False(result.Success);
        Assert.Null(await store.GetWorkflowVersionAsync(draft.Name, 1));
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public async Task TwoPackageVersionsRunSideBySideInSeparateContexts()
    {
        var store = new InMemoryWorkflowStateRepository();
        var one = await ActivityPackageFixture.PublishAsync(store);
        var two = await ActivityPackageFixture.PublishAsync(store, 2);
        await using var host = PublishedBundleScenario.Host(store, Folder());
        var client = host.GetRequiredService<IWorkflowClient>();
        var catalog = host.GetRequiredService<IWorkflowCatalog>();
        using var first = await catalog.ResolveWorkflowDefinitionAsync(one.Name, 1);
        using var second = await catalog.ResolveWorkflowDefinitionAsync(two.Name, 2);
        Assert.NotSame(first.Definition.WorkflowDataType, second.Definition.WorkflowDataType);
        Assert.NotSame(AssemblyLoadContext.GetLoadContext(first.Definition.WorkflowDataType!.Assembly), AssemblyLoadContext.GetLoadContext(second.Definition.WorkflowDataType!.Assembly));
        foreach (var (version, definition) in new[] { (1, first), (2, second) })
        {
            var id = await client.StartAsync(one.Name, version, new { Value = "before" });
            await PublishedBundleScenario.UntilAsync(store, id, WorkflowStatus.Suspended);
            var signal = Activator.CreateInstance(definition.Definition.WorkflowDataType!.Assembly.GetType("Packaged.Signal")!)!;
            signal.GetType().GetProperty("Value")!.SetValue(signal, "ok");
            var result = await client.ResumeAsync(id, signal);
            Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
            Assert.Equal($"package-v{version}:ok", result.WorkflowData!.GetType().GetProperty("Result")!.GetValue(result.WorkflowData));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MissingOrCorruptPackageKeepsTheCheckpoint(bool sqlite, bool corrupt)
    {
        var root = Folder();
        IWorkflowStateRepository store = sqlite ? new SqliteWorkflowStateRepository(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "state.db") }.ConnectionString)
            : new InMemoryWorkflowStateRepository();
        try
        {
            var version = await ActivityPackageFixture.PublishAsync(store);
            string id;
            await using (var first = PublishedBundleScenario.Host(store, Path.Combine(root, "first")))
            {
                id = await first.GetRequiredService<IWorkflowClient>().StartAsync(version.Name, 1, new { Value = "before" });
                await PublishedBundleScenario.UntilAsync(store, id, WorkflowStatus.Suspended);
            }
            var package = JsonSerializer.Deserialize<WorkflowBundleManifest>(version.ManifestJson!)!.ActivityPackages.First();
            var before = (await store.GetWorkflowInstanceAsync(id))!;
            var faults = new PackageFaults(store, package.Digest, corrupt);
            await using var host = PublishedBundleScenario.Host(store, Path.Combine(root, "second"), artifacts: faults);
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => host.GetRequiredService<IWorkflowClient>().RecoverAsync(id));
            Assert.Equal(corrupt ? WorkflowErrorKind.ArtifactMismatch : WorkflowErrorKind.ArtifactUnavailable, error.Kind);
            Assert.True(error.IsInterruption);
            var after = (await store.GetWorkflowInstanceAsync(id))!;
            Assert.Equal(before.Status, after.Status);
            Assert.Equal(before.ExecutionStateJson, after.ExecutionStateJson);
            Assert.Equal(before.Revision, after.Revision);
            faults.Failing = false;
            using var recovered = await host.GetRequiredService<IWorkflowCatalog>().ResolveWorkflowDefinitionAsync(version.Name, 1);
            Assert.Equal("Packaged.Data", recovered.Definition.WorkflowDataType!.FullName);
            var signal = Activator.CreateInstance(recovered.Definition.WorkflowDataType.Assembly.GetType("Packaged.Signal")!)!;
            signal.GetType().GetProperty("Value")!.SetValue(signal, "restored");
            var completed = await host.GetRequiredService<IWorkflowClient>().ResumeAsync(id, signal);
            Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
            Assert.Equal("package-v1:restored", completed.WorkflowData!.GetType().GetProperty("Result")!.GetValue(completed.WorkflowData));
        }
        finally { if (store is IAsyncDisposable disposable) await disposable.DisposeAsync(); }
    }

    [Fact]
    public async Task HostVerifiesPackageEntriesAfterVerifyingTheArchiveDigest()
    {
        var store = new InMemoryWorkflowStateRepository();
        var version = await ActivityPackageFixture.PublishAsync(store);
        await using var source = (await store.OpenAsync(version.ArtifactDigest!.Value))!;
        var bundle = await WorkflowBundleArchive.ReadAsync(source, version.ArtifactDigest.Value);
        var pins = bundle.Manifest.ActivityPackages.ToArray();
        var hashes = pins[0].Entries.ToDictionary();
        hashes[hashes.Keys.First()] = ArtifactDigest.Of([99]);
        pins[0] = pins[0] with { Entries = hashes };
        var bytes = WorkflowBundleArchive.Create(bundle.Manifest with { ActivityPackages = pins }, bundle.Entries.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()));
        var digest = await store.PutAsync(ArtifactKind.Bundle, new MemoryStream(bytes));
        using var catalog = new WorkflowCatalog(store, artifacts: store, bundles: new(Folder()));
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => catalog.AcquireWorkflowDefinitionAsync(version.Name, 1, digest));
        Assert.Equal(WorkflowErrorKind.ArtifactMismatch, error.Kind);
        Assert.Contains("entries differ", error.Message);
    }

    [Fact]
    public async Task LoadedPackagesSurviveCacheChangesAndColdLoadsRejectCorruption()
    {
        var store = new InMemoryWorkflowStateRepository();
        var version = await ActivityPackageFixture.PublishAsync(store);
        var pin = JsonSerializer.Deserialize<WorkflowBundleManifest>(version.ManifestJson!)!.ActivityPackages.First();
        var cache = Folder();
        var path = Path.Combine(cache, pin.Digest.Value[7..] + ".nupkg");
        using (var catalog = new WorkflowCatalog(store, artifacts: store, bundles: new(cache)))
        {
            using var first = await catalog.ResolveWorkflowDefinitionAsync(version.Name, 1);
            await File.WriteAllTextAsync(path, "corrupted cached package");
            using var stillLoaded = await catalog.ResolveWorkflowDefinitionAsync(version.Name, 1);
            Assert.Same(first.Definition, stillLoaded.Definition);
        }
        using var cold = new WorkflowCatalog(store, artifacts: store, bundles: new(cache));
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => cold.ResolveWorkflowDefinitionAsync(version.Name, 1));
        Assert.Equal(WorkflowErrorKind.ArtifactMismatch, error.Kind);
        Assert.False(File.Exists(path));
        using var restored = await cold.ResolveWorkflowDefinitionAsync(version.Name, 1);
        Assert.Equal("Packaged.Data", restored.Definition.WorkflowDataType!.FullName);
    }

    [Fact]
    public async Task NativeAssetsAreSelectedAtPublishUsingRidFallbackAndPlacementUsesThePin()
    {
        var store = new InMemoryWorkflowStateRepository();
        var publisher = new WorkflowPublisher(store, store);
        var native = ActivityPackageFixture.Package("fixture.native", "1.0.0", new()
        {
            ["runtimes/win/native/fixture.dll"] = ActivityPackageFixture.NativeLibraryBytes(),
            ["runtimes/linux-x64/native/libfixture.so"] = ActivityPackageFixture.NativeLibraryBytes(),
            ["runtimes/osx-arm64/native/libfixture.dylib"] = ActivityPackageFixture.NativeLibraryBytes()
        });
        await publisher.UploadPackageAsync(new MemoryStream(native));
        var version = await ActivityPackageFixture.PublishAsync(store, yaml: ActivityPackageFixture.Yaml(extraImport: "fixture.native@1.0.0"));
        var manifest = JsonSerializer.Deserialize<WorkflowBundleManifest>(version.ManifestJson!)!;
        var pin = manifest.ActivityPackages.Single(package => package.Id == "fixture.native");
        Assert.True(pin.RequiresRuntimeIdentifier);
        Assert.Equal("runtimes/win/native/fixture.dll", Assert.Single(pin.RuntimeAssets["win-x64"].NativeLibraries));
        Assert.Equal("runtimes/linux-x64/native/libfixture.so", Assert.Single(pin.RuntimeAssets["linux-x64"].NativeLibraries));
        Assert.False(pin.RuntimeAssets.ContainsKey("linux-arm64"));
        Assert.Equal("runtimes/osx-arm64/native/libfixture.dylib", Assert.Single(pin.RuntimeAssets["osx-arm64"].NativeLibraries));
        var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        Assert.True(WorkflowSharedAssemblies.IsCompatible(manifest));
        var incompatible = manifest with { ActivityPackages = manifest.ActivityPackages.Select(package => package == pin
            ? package with { RuntimeAssets = pin.RuntimeAssets.Where(pair => pair.Key != rid).ToDictionary() } : package).ToArray() };
        Assert.False(WorkflowSharedAssemblies.IsCompatible(incompatible));
        var cache = Folder();
        using var compatibleCatalog = new WorkflowCatalog(store, artifacts: store, bundles: new(cache));
        using var definition = await compatibleCatalog.ResolveWorkflowDefinitionAsync(version.Name, 1);
        Assert.NotNull(definition.Definition);
        var nativeFiles = Directory.GetFiles(Path.Combine(cache, "native"), "*", SearchOption.AllDirectories);
        Assert.Single(nativeFiles);
        Assert.Equal(ActivityPackageFixture.NativeLibraryBytes(), await File.ReadAllBytesAsync(nativeFiles[0]));

        await publisher.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("fixture.remote", "1.0.0", new()
        { ["runtimes/test-other-platform/native/remote.bin"] = [4] })));
        await ActivityPackageFixture.PublishAsync(store, 2, yaml: ActivityPackageFixture.Yaml(2, "fixture.remote@1.0.0"));
        await using var host = PublishedBundleScenario.Host(store, Folder());
        var capabilities = await host.GetRequiredService<HostSettings>().GetCapabilitiesAsync(host.GetRequiredService<IWorkflowCatalog>());
        Assert.Contains("workflow:PackageLifecycle@1", capabilities.Keys);
        Assert.DoesNotContain("workflow:PackageLifecycle@2", capabilities.Keys);
    }

    [Fact]
    public async Task PackageActivityCallsItsRidSelectedNativeLibrary()
    {
        var store = new InMemoryWorkflowStateRepository();
        await new WorkflowPublisher(store, store).UploadPackageAsync(new MemoryStream(ActivityPackageFixture.NativeActivityPackage()));
        var yaml = ActivityPackageFixture.Yaml(extraImport: "fixture.native-call@1.0.0").Replace("activity: fixture.echo", "activity: fixture.native-call");
        var version = await ActivityPackageFixture.PublishAsync(store, yaml: yaml);
        var cache = Folder();
        await using var host = PublishedBundleScenario.Host(store, cache);
        var client = host.GetRequiredService<IWorkflowClient>();
        using var definition = await host.GetRequiredService<IWorkflowCatalog>().ResolveWorkflowDefinitionAsync(version.Name, 1);
        var id = await client.StartAsync(version.Name, 1, new { Value = "before" });
        await PublishedBundleScenario.UntilAsync(store, id, WorkflowStatus.Suspended);
        var signal = Activator.CreateInstance(definition.Definition.WorkflowDataType!.Assembly.GetType("Packaged.Signal")!)!;
        signal.GetType().GetProperty("Value")!.SetValue(signal, "native");
        var result = await client.ResumeAsync(id, signal);
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("native:True", result.WorkflowData!.GetType().GetProperty("Result")!.GetValue(result.WorkflowData));
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.Combine(cache, "native"), "*", SearchOption.AllDirectories), path => path.EndsWith("not-selected.bin", StringComparison.Ordinal));
    }

    private sealed class PackageFaults(IArtifactStore inner, ArtifactDigest package, bool corrupt) : IArtifactStore
    {
        public bool Failing { get; set; } = true;
        public ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken token = default) => Failing && digest == package
            ? ValueTask.FromResult<Stream?>(corrupt ? new MemoryStream([1, 2, 3]) : null) : inner.OpenAsync(digest, token);
        public ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream stream, CancellationToken token = default) => inner.PutAsync(kind, stream, token);
        public ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken token = default) => inner.ExistsAsync(digest, token);
        public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken token = default) => inner.DeleteAsync(digest, token);
        public ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime cutoff, int batch, CancellationToken token = default) => inner.PurgeUnreferencedAsync(references, cutoff, batch, token);
    }
}
