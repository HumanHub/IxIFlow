using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;

using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace IxIFlow.Tests.ExecutionTests;

[CollectionDefinition("Native package isolation", DisableParallelization = true)]
public sealed class NativePackageIsolation;

[Collection("Native package isolation")]
[Trait("Feature", "registration")]
[Trait("Spec", "F-12")]
[Trait("Spec", "F-20")]
public sealed class ActivityPackageRound2Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IxIFlow.PackageR2", Guid.NewGuid().ToString("N"));
    private readonly List<WeakReference> _contexts = [];
    private static readonly Lazy<Task<(WorkflowBundle Bundle, ArtifactDigest Digest)>> Bundle = new(async () =>
    {
        var store = new InMemoryWorkflowStateRepository();
        var row = await ActivityPackageFixture.PublishAsync(store);
        using var stream = (await store.OpenAsync(row.ArtifactDigest!.Value))!;
        return (await WorkflowBundleArchive.ReadAsync(stream, row.ArtifactDigest.Value), row.ArtifactDigest.Value);
    });

    private async Task<BundleLoadContext> Context()
    {
        var (bundle, digest) = await Bundle.Value;
        var context = new BundleLoadContext(bundle, digest, [], new Dictionary<string, string>());
        _contexts.Add(new(context, true));
        return context;
    }

    private string Folder(string name = "cache") { var path = Path.Combine(_root, name); Directory.CreateDirectory(path); return path; }
    private static string Extension => OperatingSystem.IsWindows() ? ".dll" : ".so";
    private static Dictionary<string, (ReadOnlyMemory<byte> Bytes, ArtifactDigest Hash)> Images(params string[] names) =>
        names.ToDictionary(name => name + Extension, name =>
        { var bytes = ActivityPackageFixture.NativeLibraryBytes(name); return ((ReadOnlyMemory<byte>)bytes, ArtifactDigest.Of(bytes)); });
    private static List<nint> Handles(BundleLoadContext context)
    {
        var lifetime = typeof(BundleLoadContext).GetField("_nativeLifetime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(context)!;
        return (List<nint>)lifetime.GetType().GetField("Handles", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(lifetime)!;
    }
    private static int Call(BundleLoadContext context, string library, string entry)
    {
        var compiled = WorkflowCompilation.Compile($$"""
            using System.Runtime.InteropServices;
            public static class NativeProbe { [DllImport({{JsonSerializer.Serialize(library)}}, EntryPoint={{JsonSerializer.Serialize(entry)}})] public static extern int Call(); }
            """);
        Assert.True(compiled.Success, string.Join("; ", compiled.Diagnostics));
        var assembly = context.LoadFromStream(new MemoryStream(compiled.Assembly));
        return (int)assembly.GetType("NativeProbe")!.GetMethod("Call")!.Invoke(null, null)!;
    }

    [NativeFact]
    public async Task DeclaredTransitiveImportsArePreloadedAndAllHandlesAreReleased()
    {
        await Bundle.Value;
        var cache = Folder();
        var retired = LoadAndRetire(cache);
        await Collected(retired.Context);
        Assert.Empty(retired.Handles);
        foreach (var path in retired.Paths) File.Delete(path); // Windows module and verification handles must both be closed.
        if (OperatingSystem.IsLinux()) Assert.DoesNotContain(IxIFlow.TestSupport.ExistingPath.Canonicalize(cache), await File.ReadAllTextAsync("/proc/self/maps"));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private (WeakReference Context, List<nint> Handles, string[] Paths) LoadAndRetire(string cache)
    {
        var context = Context().GetAwaiter().GetResult();
        try
        {
            new BundleCache(new InMemoryWorkflowStateRepository(), cache, new(), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5))
                .LoadNativeAsync(ArtifactDigest.Of([71]), Images("fixture_primary", "fixture_helper"), context, default).GetAwaiter().GetResult();
            Assert.Equal(2, Handles(context).Count); // Eager loading, before any P/Invoke.
            Assert.Equal(42, Call(context, "fixture_primary", "fixture_primary"));
            var paths = Directory.GetFiles(Path.Combine(cache, "native"), "*", SearchOption.AllDirectories);
            Assert.Equal(2, paths.Length);
            context.Unload();
            Assert.Equal(2, Handles(context).Count);
            return (new(context, true), Handles(context), paths);
        }
        finally { context.Unload(); }
    }

    [NativeFact]
    public async Task UndeclaredPlantedTransitiveImportIsRefusedBeforeLoading()
    {
        var previous = Environment.CurrentDirectory;
        var context = await Context();
        try
        {
            var planted = Folder("current");
            await File.WriteAllBytesAsync(Path.Combine(planted, "fixture_helper" + Extension), ActivityPackageFixture.NativeLibraryBytes("fixture_helper"));
            Environment.CurrentDirectory = planted;
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => new BundleCache(new InMemoryWorkflowStateRepository(), Folder(), new(), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5))
                .LoadNativeAsync(ArtifactDigest.Of([72]), Images("fixture_primary"), context, default));
            Assert.Contains("not declared", error.Message);
            Assert.Empty(Handles(context));
        }
        finally { Environment.CurrentDirectory = previous; context.Unload(); }
    }

    [NativeFact]
    public async Task PublishValidatesTransitiveNativeImportsForBothRids()
    {
        foreach (var rid in new[] { "win-x64", "linux-x64" })
        {
            var extension = rid == "win-x64" ? ".dll" : ".so";
            var store = new InMemoryWorkflowStateRepository();
            var publisher = new WorkflowPublisher(store, store);
            await publisher.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("fixture.transitive", "1.0.0", new()
            { [$"runtimes/{rid}/native/fixture_primary{extension}"] = ActivityPackageFixture.NativeLibraryBytes("fixture_primary", rid) })));
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => new WorkflowPackageResolver(store, store, new()).ResolveAsync(new List<string> { "fixture.transitive@1.0.0" }, default));
            Assert.Contains("fixture_helper", error.Message);
            await publisher.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("fixture.transitive", "2.0.0", new()
            {
                [$"runtimes/{rid}/native/fixture_primary{extension}"] = ActivityPackageFixture.NativeLibraryBytes("fixture_primary", rid),
                [$"runtimes/{rid}/native/fixture_helper{extension}"] = ActivityPackageFixture.NativeLibraryBytes("fixture_helper", rid)
            })));
            using var accepted = await new WorkflowPackageResolver(store, store, new()).ResolveAsync(new List<string> { "fixture.transitive@2.0.0" }, default);
            Assert.Single(accepted.Pins);
        }
    }

    [NativeFact]
    public async Task PartialEagerLoadFailureReleasesEarlierHandles()
    {
        await Bundle.Value;
        var cache = Folder();
        var retired = FailAndRetire(cache);
        await Collected(retired.Context);
        Assert.Empty(retired.Handles);
        foreach (var file in Directory.GetFiles(Path.Combine(cache, "native"), "*", SearchOption.AllDirectories)) File.Delete(file);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private (WeakReference Context, List<nint> Handles) FailAndRetire(string cache)
    {
        var context = Context().GetAwaiter().GetResult();
        try
        {
            var images = Images("fixture_native");
            var invalid = ActivityPackageFixture.NativeLibraryBytes();
            if (OperatingSystem.IsWindows()) { var pe = BitConverter.ToInt32(invalid, 0x3c); invalid[pe + 4] = 0x64; invalid[pe + 5] = 0xaa; }
            else { invalid[18] = 0xb7; invalid[19] = 0; }
            images.Add("wrong_arch" + Extension, (invalid, ArtifactDigest.Of(invalid)));
            Assert.ThrowsAny<Exception>(() => new BundleCache(new InMemoryWorkflowStateRepository(), cache, new(), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5))
                .LoadNativeAsync(ArtifactDigest.Of([73]), images, context, default).GetAwaiter().GetResult());
            return (new(context, true), Handles(context));
        }
        finally { context.Unload(); }
    }

    private static async Task Collected(WeakReference context)
    {
        for (var index = 0; index < 100 && context.IsAlive; index++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            await Task.Delay(20);
        }
        Assert.False(context.IsAlive);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    }

    [LinuxNativeFact]
    public async Task VerifiedDescriptorLoadsTheOriginalImageAfterPathReplacement()
    {
        var context = await Context();
        var bytes = ActivityPackageFixture.NativeLibraryBytes();
        var path = Path.Combine(Folder(), "fixture_native.so");
        await File.WriteAllBytesAsync(path, bytes);
        var verified = File.OpenRead(path);
        try
        {
            Assert.True(await BundleCache.VerifyNativeAsync(verified, bytes.Length, ArtifactDigest.Of(bytes), bytes.Length, default));
            var replacement = Path.Combine(Folder(), "replacement.so");
            await File.WriteAllBytesAsync(replacement, [1, 2, 3]);
            File.Move(replacement, path, true);
            context.LoadNative(new Dictionary<string, string> { ["fixture_native.so"] = "/proc/self/fd/" + verified.SafeFileHandle.DangerousGetHandle() },
                new Dictionary<string, ReadOnlyMemory<byte>> { ["fixture_native.so"] = bytes });
            context.OwnNativeFiles([verified]);
            Assert.Equal(42, Call(context, "fixture_native", "fixture_value"));
        }
        finally { context.Unload(); verified.Dispose(); }
    }

    [LinuxNativeFact]
    public async Task CacheMayHaveLinkedAncestorsButRefusesLinksBelowItsRoot()
    {
        var actual = Folder("actual");
        var link = Path.Combine(_root, "linked-parent");
        Directory.CreateSymbolicLink(link, actual);
        var context = await Context();
        try
        {
            var cache = new BundleCache(new InMemoryWorkflowStateRepository(), Path.Combine(link, "cache"), new(), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            await cache.LoadNativeAsync(ArtifactDigest.Of([75]), Images("fixture_native"), context, default);
            context.Unload();
            var below = Path.Combine(actual, "second"); Directory.CreateDirectory(below);
            Directory.CreateSymbolicLink(Path.Combine(below, "native"), Folder("outside"));
            context = await Context();
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => new BundleCache(new InMemoryWorkflowStateRepository(), below, new(), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5))
                .LoadNativeAsync(ArtifactDigest.Of([76]), Images("fixture_native"), context, default));
            Assert.Equal(WorkflowErrorKind.ArtifactMismatch, error.Kind);
            Directory.Delete(Path.Combine(below, "native"));
        }
        finally { context.Unload(); Directory.Delete(link); }
    }

    [NativeFact]
    public async Task OsLibraryPInvokeIgnoresPlantedCurrentDirectoryLibraryAndRefusesPaths()
    {
        var previous = Environment.CurrentDirectory;
        var context = await Context();
        try
        {
            var planted = Folder("current");
            var library = OperatingSystem.IsWindows() ? "kernel32" : "libc";
            var path = Path.Combine(planted, OperatingSystem.IsWindows() ? "kernel32.dll" : "libc.so");
            await File.WriteAllBytesAsync(path, ActivityPackageFixture.NativeLibraryBytes());
            Environment.CurrentDirectory = planted;
            Assert.Equal(Environment.ProcessId, Call(context, library, OperatingSystem.IsWindows() ? "GetCurrentProcessId" : "getpid"));
            Assert.Throws<TargetInvocationException>(() => Call(context, path, "fixture_value"));
        }
        finally { Environment.CurrentDirectory = previous; context.Unload(); }
    }

    [NativeFact]
    public async Task ConfiguredNativeLockTimeoutAndPermanentIoErrorsAreBounded()
    {
        var root = Folder(); var digest = ArtifactDigest.Of([74]);
        var context = await Context();
        try
        {
            var cache = new BundleCache(new InMemoryWorkflowStateRepository(), root, new(), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(100));
            var path = Path.Combine(root, digest.Value[7..] + ".native.lock");
            using (var held = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => cache.LoadNativeAsync(digest, Images("fixture_native"), context, default).WaitAsync(TimeSpan.FromSeconds(3)));
                Assert.Equal(WorkflowErrorKind.ArtifactUnavailable, error.Kind);
                Assert.Contains("timeout", error.Message);
            }
            File.Delete(path); Directory.CreateDirectory(path);
            var permanent = await Assert.ThrowsAsync<WorkflowEngineException>(() => cache.LoadNativeAsync(digest, Images("fixture_native"), context, default).WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(WorkflowErrorKind.ArtifactUnavailable, permanent.Kind);
            Assert.DoesNotContain("timeout", permanent.Message);
        }
        finally { context.Unload(); }
    }

    [Fact]
    public async Task OversizedNativeImageIsRejectedByLengthBeforeAnyRead()
    {
        using var stream = new NoReadStream(1_000_000_000);
        Assert.False(await BundleCache.VerifyNativeAsync(stream, 2048, ArtifactDigest.Of([1]), 4096, default));
        Assert.Equal(0, stream.Reads);
    }

    [Theory]
    [InlineData("/usr/lib/libSystem.B.dylib", true)]
    [InlineData("/usr/lib/../../tmp/planted.dylib", false)]
    [InlineData("/System/Library/Frameworks/Foundation.framework/Foundation", true)]
    [InlineData("/System/Library/Frameworks/../../../tmp/planted.dylib", false)]
    public void OsImportPathsCannotEscapeSystemDirectories(string path, bool allowed) =>
        Assert.Equal(allowed, WorkflowNativeLibraries.IsSystemImport(path, "osx-arm64"));

    [Fact]
    public async Task UploadedCandidateMetadataDoesNotOpenArchives()
    {
        var store = new InMemoryWorkflowStateRepository();
        var publisher = new WorkflowPublisher(store, store);
        var unselected = await publisher.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("fixture.metadata", "2.0.0", new())));
        var selected = await publisher.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("fixture.metadata", "1.0.0", new())));
        await publisher.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("fixture.root", "1.0.0", new(), "<dependency id=\"fixture.metadata\" version=\"[1.0.0,3.0.0)\"/>")));
        var artifacts = new RecordingArtifacts(store);
        using var resolved = await new WorkflowPackageResolver(store, artifacts, new()).ResolveAsync(new List<string> { "fixture.root@1.0.0" }, default);
        Assert.DoesNotContain(unselected.Digest, artifacts.Opened);
        Assert.Equal(1, artifacts.Opened.Count(digest => digest == selected.Digest));
        Assert.Equal(2, artifacts.Opened.Count);
    }

    [Fact]
    public async Task HistoricalRowsDoNotConsumeTheAvailableCandidateBudget()
    {
        var store = new InMemoryWorkflowStateRepository();
        var package = await new WorkflowPublisher(store, store).UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("fixture.history", "1.0.0", new())));
        var historical = await store.PutAsync(ArtifactKind.Package, new MemoryStream([1]));
        for (var i = 2; i < 2055; i++) await store.RegisterPackageAsync(package with { Version = i + ".0.0", Digest = historical }, store);
        await store.DeleteAsync(historical);
        foreach (var import in new[] { "fixture.history@1.0.0", "fixture.history" })
        {
            using var resolved = await new WorkflowPackageResolver(store, store, new() { MaxPackageCandidates = 1 }).ResolveAsync(new List<string> { import }, default);
            Assert.Equal("1.0.0", Assert.Single(resolved.Pins).Version);
        }
    }

    [Fact]
    public async Task RejectedRuntimeGraphDoesNotRetainItsOtherPackageConstraints()
    {
        var store = new InMemoryWorkflowStateRepository(); var publisher = new WorkflowPublisher(store, store);
        var graph = JsonSerializer.Serialize(new { runtimes = new Dictionary<string, object> { ["win-x64"] = new Dictionary<string, object>
        { ["fixture.a"] = new Dictionary<string, string> { ["IxIFlow.Abstractions"] = "[99.0.0]" }, ["fixture.b"] = new Dictionary<string, string> { ["fixture.missing"] = "[1.0.0]" } } } });
        await publisher.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("fixture.a", "1.0.0", new() { ["runtime.json"] = Encoding.UTF8.GetBytes(graph) })));
        await publisher.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("fixture.a", "2.0.0", new())));
        await publisher.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("fixture.b", "1.0.0", new())));
        await publisher.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("fixture.root", "1.0.0", new(), "<dependency id=\"fixture.a\" version=\"[1.0.0,3.0.0)\"/><dependency id=\"fixture.b\" version=\"[1.0.0]\"/>")));
        using var resolved = await new WorkflowPackageResolver(store, store, new()).ResolveAsync(new List<string> { "fixture.root@1.0.0" }, default);
        Assert.Equal("2.0.0", resolved.Pins.Single(pin => pin.Id == "fixture.a").Version);
        Assert.DoesNotContain(resolved.Pins, pin => pin.Id == "fixture.missing");
    }

    [Fact]
    public async Task ManagedRuntimeClosuresAreDisjointPerRidAndScratchUsesOnlyItsRid()
    {
        var store = new InMemoryWorkflowStateRepository(); var publisher = new WorkflowPublisher(store, store);
        foreach (var rid in new[] { "win-x64", "linux-x64" })
        {
            var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create("Platform",
                [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText("public class PlatformOnly { public string Rid => \"" + rid + "\"; }")],
                WorkflowCompilation.ReferencePaths().Select(path => Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(path)),
                new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
            using var bytes = new MemoryStream();
            Assert.True(compilation.Emit(bytes).Success);
            await publisher.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("fixture." + rid, "1.0.0", new() { ["lib/net10.0/Platform.dll"] = bytes.ToArray() })));
        }
        var graph = JsonSerializer.Serialize(new { runtimes = new Dictionary<string, object>
        {
            ["win-x64"] = new Dictionary<string, object> { ["fixture.root"] = new Dictionary<string, string> { ["fixture.win-x64"] = "[1.0.0]" } },
            ["linux-x64"] = new Dictionary<string, object> { ["fixture.root"] = new Dictionary<string, string> { ["fixture.linux-x64"] = "[1.0.0]" } }
        } });
        await publisher.UploadPackageAsync(new MemoryStream(ActivityPackageFixture.Package("fixture.root", "1.0.0", new() { ["runtime.json"] = Encoding.UTF8.GetBytes(graph) })));
        using var resolved = await new WorkflowPackageResolver(store, store, new()).ResolveAsync(new List<string> { "fixture.root@1.0.0" }, default);
        Assert.Empty(resolved.References);
        var implementation = Assert.Single(resolved.RuntimeAssemblies);
        var assembly = Assembly.Load(implementation.Value);
        Assert.Equal(RuntimeInformation.RuntimeIdentifier, assembly.GetType("PlatformOnly")!.GetProperty("Rid")!.GetValue(Activator.CreateInstance(assembly.GetType("PlatformOnly")!)));
    }

    [Fact]
    public async Task ScratchBuildCannotBindAnImplementationFromTheDefaultContext()
    {
        Assert.NotNull(typeof(IxIFlow.BundleFixtures.FirstData));
        var compiled = WorkflowCompilation.Compile("""
            public static class Entry { public static IxIFlow.Core.WorkflowDefinition Build() {
                var type = new IxIFlow.BundleFixtures.FirstData();
                return new IxIFlow.Core.WorkflowDefinition { Name = type.GetType().Name, Version = 1, WorkflowDataType = type.GetType() };
            } }
            """);
        Assert.True(compiled.Success, string.Join("; ", compiled.Diagnostics));
        var result = await WorkflowBuild.RunAsync(compiled, new Dictionary<string, byte[]>(), TimeSpan.FromSeconds(5), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, default);
        Assert.NotNull(result.Error);
        Assert.Contains("System.Payments", result.Error);
    }

    [Fact]
    public async Task NoMatchingSourceMappingNamesThePackage()
    {
        var path = Path.Combine(Folder(), "NuGet.Config");
        await File.WriteAllTextAsync(path, $"<configuration><packageSources><clear/><add key=\"offline\" value=\"{Folder("feed")}\"/></packageSources><packageSourceMapping><packageSource key=\"offline\"><package pattern=\"other.*\"/></packageSource></packageSourceMapping></configuration>");
        var store = new InMemoryWorkflowStateRepository();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowPackageResolver(store, store, new() { NuGetConfigPath = path }).ResolveAsync(new List<string> { "fixture.unmapped" }, default));
        Assert.Contains("fixture.unmapped", error.Message);
        Assert.Contains("source mapping", error.Message);
    }

    [Fact]
    public async Task FeedErrorsRedactSourcePathsAndUnderlyingExceptions()
    {
        var feed = Folder("token-very-secret");
        await File.WriteAllBytesAsync(Path.Combine(feed, "fixture.broken.1.0.0.nupkg"), [1, 2, 3]);
        var path = Path.Combine(Folder(), "NuGet.Config");
        await File.WriteAllTextAsync(path, $"<configuration><packageSources><clear/><add key=\"safe-source\" value=\"{feed}\"/></packageSources></configuration>");
        var store = new InMemoryWorkflowStateRepository();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkflowPackageResolver(store, store, new() { NuGetConfigPath = path }).ResolveAsync(new List<string> { "fixture.broken@1.0.0" }, default));
        Assert.Contains("safe-source", error.Message);
        Assert.Contains("fixture.broken", error.Message);
        Assert.DoesNotContain("very-secret", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void SeekableDownloadBudgetAppliesToWritesSeeksAndRetryTruncation()
    {
        using var memory = new MemoryStream(); using var bounded = new WorkflowPackageResolver.BoundedPackageStream(memory, 16, _ => { });
        Assert.True(bounded.CanSeek); Assert.True(bounded.CanRead);
        bounded.Write(new byte[16]); bounded.Position = 0; bounded.SetLength(0); bounded.Write(new byte[16]);
        Assert.Equal(16, memory.Length);
        Assert.Throws<WorkflowEngineException>(() => bounded.WriteByte(1));
        Assert.Throws<WorkflowEngineException>(() => bounded.SetLength(17));
        Assert.Throws<WorkflowEngineException>(() => bounded.Seek(17, SeekOrigin.Begin));
    }

    [Fact]
    public async Task CredentialScopesOwnFreshHttpResourcesAndLocalScopesNeedNoGlobalGate()
    {
        var original = HttpHandlerResourceV3.CredentialService;
        var remote = new SourceRepository(new PackageSource("https://fixture.invalid/v3/index.json", "fixture"), Repository.Provider.GetCoreV3());
        HttpSource first;
        using (var scope = await NuGetCredentialScope.CreateAsync([remote], default))
        {
            first = (await scope.Repositories[0].GetResourceAsync<HttpSourceResource>()).HttpSource;
            using var local = await NuGetCredentialScope.CreateAsync([], default).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Empty(local.Repositories);
        }
        using (var scope = await NuGetCredentialScope.CreateAsync([remote], default))
            Assert.NotSame(first, (await scope.Repositories[0].GetResourceAsync<HttpSourceResource>()).HttpSource);
        Assert.Same(original, HttpHandlerResourceV3.CredentialService);
        var credential = new NetworkCredential("name", "secret");
        var filtered = new NuGetCredentialScope.AuthenticationTypeCredentials(credential, ["Basic"]);
        Assert.Same(credential, filtered.GetCredential(new Uri("https://fixture.invalid/flatcontainer/fixture/1.0.0/fixture.nupkg"), "basic"));
        Assert.Null(filtered.GetCredential(new Uri("https://fixture.invalid/flatcontainer/"), "Negotiate"));
    }

    [Fact]
    public async Task HttpCandidateDiscoveryReadsOnlyBoundedMetadata()
    {
        using var feed = new MemoryFeed();
        var reader = new WorkflowPackageFeed(feed.Repository, feed.Cache, new() { MaxManifestBytes = 1024 }, () => 100_000, _ => { });
        Assert.Equal(2, (await reader.VersionsAsync("fixture.candidate", default)).Length);
        foreach (var version in new[] { "1.0.0", "2.0.0" })
            Assert.NotNull(await reader.DependenciesAsync(new("fixture.candidate", NuGetVersion.Parse(version)), default));
        Assert.DoesNotContain(feed.Requests, path => path.EndsWith(".nupkg", StringComparison.Ordinal));
        feed.OversizedNuspec = true;
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => reader.DependenciesAsync(new("fixture.candidate", NuGetVersion.Parse("2.0.0")), default));
        Assert.Equal(WorkflowErrorKind.ResourceExhausted, error.Kind);
        Assert.InRange(feed.PayloadReads, 1, MemoryFeed.PayloadLength - 1);
    }

    [Fact]
    public async Task SelectedFeedDownloadCannotStageAFullOversizedArchive()
    {
        using var feed = new MemoryFeed();
        var reader = new WorkflowPackageFeed(feed.Repository, feed.Cache, new(), () => 100_000, _ => { });
        using var buffer = new MemoryStream(); using var bounded = new WorkflowPackageResolver.BoundedPackageStream(buffer, 1024, _ => { });
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => reader.DownloadAsync(new("fixture.candidate", NuGetVersion.Parse("1.0.0")), bounded, default));
        Assert.Equal(WorkflowErrorKind.ResourceExhausted, error.Kind);
        Assert.True(feed.PayloadReads > 0 && feed.PayloadReads < MemoryFeed.PayloadLength, $"Read {feed.PayloadReads} bytes; requests: {string.Join(", ", feed.Requests)}");
        Assert.InRange(buffer.Length, 0, 1024);
    }

    private sealed class NoReadStream(long length) : MemoryStream
    {
        public int Reads;
        public override long Length => length;
        public override int Read(byte[] buffer, int offset, int count) { Reads++; throw new InvalidOperationException("Must check Length before Read"); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) { Reads++; throw new InvalidOperationException("Must check Length before Read"); }
    }

    private sealed class RecordingArtifacts(IArtifactStore inner) : IArtifactStore
    {
        internal readonly List<ArtifactDigest> Opened = [];
        public ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken token = default) { Opened.Add(digest); return inner.OpenAsync(digest, token); }
        public ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken token = default) => inner.ExistsAsync(digest, token);
        public ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream stream, CancellationToken token = default) => inner.PutAsync(kind, stream, token);
        public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken token = default) => inner.DeleteAsync(digest, token);
        public ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> refs, DateTime cutoff, int batch, CancellationToken token = default) => inner.PurgeUnreferencedAsync(refs, cutoff, batch, token);
    }

    private sealed class MemoryFeed : HttpMessageHandler
    {
        internal const int PayloadLength = 2 * 1024 * 1024;
        internal readonly SourceRepository Repository;
        internal readonly SourceCacheContext Cache = new() { DirectDownload = true, NoCache = true };
        internal readonly List<string> Requests = [];
        internal int PayloadReads;
        internal bool OversizedNuspec;
        private readonly HttpSource _http;

        internal MemoryFeed()
        {
            var source = new PackageSource("https://fixture.invalid/v3/index.json", "memory");
            var handler = new HttpHandlerResourceV3(new HttpClientHandler(), this);
            _http = new HttpSource(source, () => Task.FromResult<HttpHandlerResource>(handler), new NullThrottle());
            Repository = new(source, RepositoryProvider());
        }
        private IEnumerable<Lazy<INuGetResourceProvider>> RepositoryProvider() => RepositoryCore().Prepend(new(() => new FixedHttpProvider(_http)));
        private static IEnumerable<Lazy<INuGetResourceProvider>> RepositoryCore() => NuGet.Protocol.Core.Types.Repository.Provider.GetCoreV3()
            .Where(provider => provider.Value.ResourceType != typeof(HttpSourceResource));
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath; Requests.Add(path);
            var large = path.EndsWith(".nupkg", StringComparison.Ordinal) || OversizedNuspec && path.EndsWith(".nuspec", StringComparison.Ordinal);
            var text = path == "/v3/index.json" ? "{\"version\":\"3.0.0\",\"resources\":[{\"@id\":\"https://fixture.invalid/flat/\",\"@type\":\"PackageBaseAddress/3.0.0\"}]}"
                : path.EndsWith(".nuspec", StringComparison.Ordinal) ? "<package><metadata><id>fixture.candidate</id><version>1.0.0</version><dependencies/></metadata></package>"
                : "{\"versions\":[\"1.0.0\",\"2.0.0\"]}";
            Stream stream = large ? new CountedStream(new byte[PayloadLength], read => PayloadReads += read) : new MemoryStream(Encoding.UTF8.GetBytes(text));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream), RequestMessage = request });
        }
        private sealed class CountedStream(byte[] bytes, Action<int> read) : MemoryStream(bytes)
        {
            public override int Read(byte[] buffer, int offset, int count) { var n = base.Read(buffer, offset, count); read(n); return n; }
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) { var n = base.Read(buffer.Span); read(n); return ValueTask.FromResult(n); }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) { var n = base.Read(buffer, offset, count); read(n); return Task.FromResult(n); }
        }
        private sealed class FixedHttpProvider(HttpSource http) : ResourceProvider(typeof(HttpSourceResource), "MemoryHttpSource")
        {
            public override Task<Tuple<bool, INuGetResource>> TryCreate(SourceRepository source, CancellationToken token) => Task.FromResult(Tuple.Create(true, (INuGetResource)new HttpSourceResource(http)));
        }
        protected override void Dispose(bool disposing) { if (disposing) { Cache.Dispose(); } base.Dispose(disposing); }
    }

    void IDisposable.Dispose()
    {
        // Retired native contexts release their file ownership through collection.
        for (var index = 0; index < 100 && _contexts.Any(context => context.IsAlive); index++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Thread.Sleep(20);
        }
        Assert.All(_contexts, context => Assert.False(context.IsAlive));
        // The owner may have re-registered in the round that cleared the context.
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
