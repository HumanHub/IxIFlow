using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.ExecutionTests;

[Collection("Native package isolation")]
[Trait("Feature", "registration")]
[Trait("Spec", "F-12")]
[Trait("Spec", "F-20")]
public sealed class ActivityPackageVerificationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IxIFlow.PackageVerification", Guid.NewGuid().ToString("N"));

    [NativeTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImplicitUnloadKeepsNativeCallsAndFileOwnershipUntilThePackageObjectIsGone(bool resurrect)
    {
        var store = new InMemoryWorkflowStateRepository();
        var row = await ActivityPackageFixture.PublishAsync(store);
        using var input = (await store.OpenAsync(row.ArtifactDigest!.Value))!;
        var bundle = await WorkflowBundleArchive.ReadAsync(input, row.ArtifactDigest.Value);
        var retired = RetainAndCallAfterImplicitUnload(bundle, row.ArtifactDigest.Value, _root, resurrect);
        for (var round = 0; round < 100 && retired.Context.IsAlive; round++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            await Task.Delay(20);
        }
        Assert.False(retired.Context.IsAlive);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.Empty(retired.Handles);
        AssertMapped(_root, false);
        using (File.Open(retired.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        File.Delete(retired.Path);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Context, List<nint> Handles, string Path) RetainAndCallAfterImplicitUnload(
        WorkflowBundle bundle, ArtifactDigest digest, string cache, bool resurrect)
    {
        var state = new Resurrection();
        var retained = LoadWithoutUnloading(bundle, digest, cache, resurrect, state);
        for (var round = 0; round < 20; round++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Assert.Equal(1, state.Unloading);
        if (resurrect) Assert.NotNull(state.Context);
        Assert.True(retained.Context.IsAlive);
        Assert.Single(retained.Handles);
        Assert.Equal(42, retained.Data.GetType().GetProperty("NativeValue")!.GetValue(retained.Data));
        AssertMapped(cache, true);
        if (OperatingSystem.IsWindows())
            Assert.Throws<IOException>(() => { using var file = File.Open(retained.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); });
        // An image mapping alone does not deny an exclusive reader. This probe
        // specifically proves the verification handle survived finalization.
        Assert.Throws<IOException>(() => { using var file = File.Open(retained.Path, FileMode.Open, FileAccess.Read, FileShare.None); });
        state.Context = null;
        GC.KeepAlive(retained.Data);
        return (retained.Context, retained.Handles, retained.Path);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (object Data, WeakReference Context, List<nint> Handles, string Path) LoadWithoutUnloading(
        WorkflowBundle bundle, ArtifactDigest digest, string cache, bool resurrect, Resurrection state)
    {
        var context = new BundleLoadContext(bundle, digest, [], new Dictionary<string, string>());
        context.Unloading += value => { state.Unloading++; if (resurrect) state.Context = value; };
        var bytes = ActivityPackageFixture.NativeLibraryBytes();
        var name = "fixture_native" + (OperatingSystem.IsWindows() ? ".dll" : ".so");
        new BundleCache(new InMemoryWorkflowStateRepository(), cache, new(), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5))
            .LoadNativeAsync(digest, new Dictionary<string, (ReadOnlyMemory<byte>, ArtifactDigest)> { [name] = (bytes, ArtifactDigest.Of(bytes)) }, context, default)
            .GetAwaiter().GetResult();
        using var package = new ZipArchive(new MemoryStream(ActivityPackageFixture.NativeActivityPackage()));
        using var assemblyBytes = package.GetEntry("lib/net10.0/NativeCall.dll")!.Open();
        using var assembly = new MemoryStream();
        assemblyBytes.CopyTo(assembly); assembly.Position = 0;
        var type = context.LoadFromStream(assembly).GetType("NativeFixture.Data")!;
        var data = Activator.CreateInstance(type)!;
        Assert.Equal(42, type.GetProperty("NativeValue")!.GetValue(data));
        var owner = typeof(BundleLoadContext).GetField("_nativeLifetime", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(context)!;
        var handles = (List<nint>)owner.GetType().GetField("Handles", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
        return (data, new(context, true), handles, Assert.Single(Directory.GetFiles(Path.Combine(cache, "native"), "*", SearchOption.AllDirectories)));
        // Deliberately no Unload(): finalization must be safe for implicit retirement.
    }

    private sealed class Resurrection { internal AssemblyLoadContext? Context; internal int Unloading; }

    [PosixNativeFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void PrivateNativePathsDisappearWhileTheLoadedImageRemainsCallable()
    {
        Directory.CreateDirectory(_root);
        nint handle = 0;
        string root;
        try
        {
            using (var copies = NativeImageDirectory.Create(_root))
            {
                root = copies.Root;
                var path = Path.Combine(root, "fixture_native.so");
                File.WriteAllBytes(path, ActivityPackageFixture.NativeLibraryBytes());
                handle = System.Runtime.InteropServices.NativeLibrary.Load(path);
            }
            Assert.False(Directory.Exists(root));
            var call = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<NativeValue>(
                System.Runtime.InteropServices.NativeLibrary.GetExport(handle, "fixture_value"));
            Assert.Equal(42, call());
        }
        finally { if (handle != 0) System.Runtime.InteropServices.NativeLibrary.Free(handle); }
    }

    [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
    private delegate int NativeValue();

    [PosixNativeFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void PrivateRecoveryRemovesOnlyIdleOwnedMarkedDirectoriesAndNeverTraversesLinks()
    {
        Directory.CreateDirectory(_root);
        using var active = NativeImageDirectory.Create(_root);
        File.WriteAllText(Path.Combine(active.Root, "active.so"), "active");
        var stale = Candidate("stale", marked: true);
        var legacy = Candidate("legacy", marked: false);
        var linked = Path.Combine(_root, "IxIFlow.Native.link");
        Directory.CreateSymbolicLink(linked, legacy);
        var planted = Candidate("planted", marked: true);
        File.CreateSymbolicLink(Path.Combine(planted, "image.so"), Path.Combine(legacy, "image.so"));
        var shared = Candidate("shared", marked: true);
        File.SetUnixFileMode(shared, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupWrite);
        Assert.True(NativeImageDirectory.IsOwnedDirectory(stale));
        Assert.False(NativeImageDirectory.IsOwnedDirectory(linked));
        try
        {
            NativeImageDirectory.Recover(_root);
            Assert.False(Directory.Exists(stale));
            Assert.True(File.Exists(Path.Combine(active.Root, "active.so")));
            Assert.True(Directory.Exists(legacy));
            Assert.True(Directory.Exists(linked));
            Assert.True(Directory.Exists(planted));
            Assert.True(Directory.Exists(shared));
            Assert.Equal("legacy", File.ReadAllText(Path.Combine(legacy, "image.so")));
        }
        finally { File.Delete(Path.Combine(planted, "image.so")); Directory.Delete(linked); }
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private string Candidate(string name, bool marked)
    {
        var path = Path.Combine(_root, "IxIFlow.Native." + name);
        Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (marked) File.WriteAllText(Path.Combine(path, ".owner"), "");
        if (name != "planted") File.WriteAllText(Path.Combine(path, "image.so"), name);
        return path;
    }

    [LinuxNativeFact]
    public void NativeMapComparisonResolvesMultipleLinkedAncestors()
    {
        var physical = Path.Combine(_root, "physical");
        var cache = Path.Combine(physical, "cache");
        Directory.CreateDirectory(cache);
        var first = Path.Combine(_root, "first");
        var second = Path.Combine(_root, "second");
        Directory.CreateSymbolicLink(first, physical);
        Directory.CreateSymbolicLink(second, first);
        var path = Path.Combine(cache, "fixture_native.so");
        File.WriteAllBytes(path, ActivityPackageFixture.NativeLibraryBytes());
        var handle = System.Runtime.InteropServices.NativeLibrary.Load(path);
        try
        {
            Assert.Equal(IxIFlow.TestSupport.ExistingPath.Canonicalize(cache), IxIFlow.TestSupport.ExistingPath.Canonicalize(Path.Combine(second, "cache")));
            AssertMapped(Path.Combine(second, "cache"), true);
        }
        finally
        {
            System.Runtime.InteropServices.NativeLibrary.Free(handle);
            Directory.Delete(second); Directory.Delete(first);
        }
    }

    private static void AssertMapped(string cache, bool expected)
    {
        cache = IxIFlow.TestSupport.ExistingPath.Canonicalize(cache);
        if (OperatingSystem.IsWindows())
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            Assert.Equal(expected, process.Modules.Cast<System.Diagnostics.ProcessModule>()
                .Any(module => module.FileName.StartsWith(cache + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
        }
        if (OperatingSystem.IsLinux()) Assert.Equal(expected, File.ReadAllText("/proc/self/maps").Contains(cache, StringComparison.Ordinal));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
