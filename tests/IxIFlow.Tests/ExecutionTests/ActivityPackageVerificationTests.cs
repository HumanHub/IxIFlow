using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using IxIFlow.Core;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.Logging;

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
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(root));
                Assert.Empty(Directory.GetFileSystemEntries(root));
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
    public void PrivateCleanupIgnoresMissingEntriesAndLogsOtherFailuresWithoutThrowing()
    {
        Directory.CreateDirectory(_root);
        var logger = new CleanupLogger();
        using var missing = NativeImageDirectory.Create(_root, logger);
        var path = Path.Combine(missing.Root, "image.so");
        File.WriteAllText(path, "removed");
        File.Delete(path);
        missing.Dispose();
        Assert.False(Directory.Exists(missing.Root));
        missing.Dispose();
        Assert.Empty(logger.Messages);

        using var blocked = NativeImageDirectory.Create(_root, logger);
        var child = Directory.CreateDirectory(Path.Combine(blocked.Root, "unexpected"));
        blocked.Dispose();
        Assert.True(child.Exists);
        Assert.NotEmpty(logger.Messages);
        Assert.All(logger.Messages, message => Assert.Contains("private native", message));
        logger.Throw = true;
        blocked.Dispose();
        Directory.Delete(child.FullName);
        blocked.Dispose();
        Assert.False(Directory.Exists(blocked.Root));
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
                .Any(module =>
                {
                    try
                    {
                        var directory = IxIFlow.TestSupport.ExistingPath.Canonicalize(Path.GetDirectoryName(module.FileName)!);
                        return directory.Equals(cache, StringComparison.OrdinalIgnoreCase) ||
                            directory.StartsWith(cache + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                    }
                    // An unrelated module may outlive its directory.
                    catch (System.ComponentModel.Win32Exception) { return false; }
                }));
        }
        if (OperatingSystem.IsLinux()) Assert.Equal(expected, File.ReadAllText("/proc/self/maps").Contains(cache, StringComparison.Ordinal));
    }

    private sealed class CleanupLogger : ILogger
    {
        internal readonly List<string> Messages = [];
        internal bool Throw;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
        {
            Messages.Add(format(state, error));
            if (Throw) throw new InvalidOperationException("cleanup logger failed");
        }
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
