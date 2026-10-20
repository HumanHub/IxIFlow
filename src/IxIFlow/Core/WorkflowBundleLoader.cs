using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using IxIFlow.Core.Runtime;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Core;

/// <summary>Enables published workflows on a host. Idle contexts are bounded independently of running operations.</summary>
public sealed record WorkflowBundleOptions(string CacheDirectory, int MaxCachedBundles = 16)
{
    public WorkflowBundleReadLimits ReadLimits { get; init; } = new();
    public TimeSpan FetchTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan CacheLockTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>Owns a definition for an operation. Dispose only after all work using its types has finished.</summary>
public sealed class WorkflowDefinitionLease : IDisposable
{
    private Action? _release;
    private Func<WorkflowDefinitionLease>? _retain;
    private WorkflowDefinition? _definition;
    public WorkflowDefinitionLease(WorkflowDefinition definition) : this(definition, null) { }
    internal WorkflowDefinitionLease(WorkflowDefinition definition, Action? release, Func<WorkflowDefinitionLease>? retain = null)
    { _definition = definition; _release = release; _retain = retain; }
    public WorkflowDefinition Definition => _definition ?? throw new ObjectDisposedException(nameof(WorkflowDefinitionLease));
    internal IDisposable EnterTypes() => WorkflowTypeIdentity.Enter(Definition.BundleContext);
    internal WorkflowDefinitionLease Retain() => _retain?.Invoke() ?? new(Definition);
    public void Dispose()
    {
        Interlocked.Exchange(ref _definition, null);
        Interlocked.Exchange(ref _retain, null);
        Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}

internal sealed class BundleCache(IArtifactStore artifacts, string directory, WorkflowBundleReadLimits limits, TimeSpan fetchTimeout,
    TimeSpan cacheLockTimeout)
{
    private readonly string _directory = Path.GetFullPath(directory);
    private readonly Lazy<string> _nativeDirectory = new(() =>
    {
        var root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        return new DirectoryInfo(root).ResolveLinkTarget(true)?.FullName ?? root;
    });

    internal Task<WorkflowBundle> ReadAsync(ArtifactDigest digest, CancellationToken token) =>
        ReadArtifactAsync(digest, ".bundle", WorkflowBundleArchive.ReadVerifiedAsync, token);

    internal async Task<IReadOnlyDictionary<string, ReadOnlyMemory<byte>>> ReadPackageAsync(WorkflowPackagePin pin, CancellationToken token)
    {
        try
        {
            return await ReadArtifactAsync(pin.Digest, ".nupkg", async (stream, bounds, stop) =>
            {
                var entries = await WorkflowPackageArchive.ReadVerifiedAsync(stream, bounds, stop);
                WorkflowPackageArchive.Verify(pin, entries);
                stop.ThrowIfCancellationRequested();
                return entries;
            }, token);
        }
        catch (WorkflowEngineException error) when (error.Kind is WorkflowErrorKind.ArtifactMismatch or WorkflowErrorKind.ArtifactUnavailable)
        { throw new WorkflowEngineException(error.Kind, $"Activity package '{pin.Id}@{pin.Version}' ({pin.Digest}): {error.Message}", error); }
    }

    internal async Task LoadNativeAsync(ArtifactDigest bundle,
        IReadOnlyDictionary<string, (ReadOnlyMemory<byte> Bytes, ArtifactDigest Hash)> native,
        BundleLoadContext context, CancellationToken token)
    {
        if (native.Count == 0) return;
        // The configured root is a trust boundary; links above it are the administrator's choice.
        var resolvedRoot = _nativeDirectory.Value;
        var root = Path.Combine(resolvedRoot, "native", bundle.Value[7..], System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier);
        await using var guard = await NativeLockAsync(Path.Combine(resolvedRoot, bundle.Value[7..] + ".native.lock"), token);
        RejectLinks(resolvedRoot, root);
        Directory.CreateDirectory(root);
        RejectLinks(resolvedRoot, root);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var expected = native.Keys.ToHashSet(comparer);
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
            if (!expected.Contains(Path.GetFileName(entry))) RemoveNativeEntry(entry);
        // Temporary names are under the digest lock, so every leftover belongs to a crashed writer.
        var opened = new Dictionary<string, FileStream>(comparer);
        string? privateRoot = null;
        try
        {
            foreach (var (name, asset) in native)
            {
                token.ThrowIfCancellationRequested();
                var path = Path.Combine(root, name);
                FileStream? stream = null;
                if (Path.Exists(path))
                {
                    if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) RemoveNativeEntry(path);
                    else
                    {
                        stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                        opened.Add(name, stream);
                        if (!await VerifyNativeAsync(stream, asset.Bytes.Length, asset.Hash, limits.MaxEntryBytes, token))
                        { stream.Dispose(); opened.Remove(name); stream = null; File.Delete(path); }
                    }
                }
                if (stream == null)
                {
                    var temporary = Path.Combine(root, Guid.NewGuid().ToString("N") + ".native.tmp");
                    try
                    {
                        await File.WriteAllBytesAsync(temporary, asset.Bytes.ToArray(), token);
                        File.Move(temporary, path);
                    }
                    finally { TryDelete(temporary); }
                    stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    opened.Add(name, stream);
                    if (!await VerifyNativeAsync(stream, asset.Bytes.Length, asset.Hash, limits.MaxEntryBytes, token))
                    { stream.Dispose(); throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, "Native cache differs from its pin"); }
                }
            }
            var paths = new Dictionary<string, string>(comparer);
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            {
                privateRoot = Directory.CreateTempSubdirectory("IxIFlow.Native.").FullName;
                File.SetUnixFileMode(privateRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            foreach (var (name, stream) in opened)
            {
                if (OperatingSystem.IsLinux()) paths.Add(name, "/proc/self/fd/" + stream.SafeFileHandle.DangerousGetHandle());
                else if (OperatingSystem.IsWindows()) paths.Add(name, stream.Name);
                else
                {
                    var path = Path.Combine(privateRoot!, name);
                    await File.WriteAllBytesAsync(path, native[name].Bytes.ToArray(), token);
                    using var verified = File.OpenRead(path);
                    if (!await VerifyNativeAsync(verified, native[name].Bytes.Length, native[name].Hash, limits.MaxEntryBytes, token))
                        throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, "Private native image differs from its pin");
                    paths.Add(name, path);
                }
            }
            context.OwnNativeFiles(opened.Values.ToArray(), privateRoot);
            opened.Clear(); privateRoot = null;
            context.LoadNative(paths, native.ToDictionary(pair => pair.Key, pair => pair.Value.Bytes, comparer));
        }
        finally
        {
            foreach (var stream in opened.Values) stream.Dispose();
            if (privateRoot != null) Directory.Delete(privateRoot, true);
        }
    }

    internal static async Task<bool> VerifyNativeAsync(Stream stream, long length, ArtifactDigest expected, long maximum, CancellationToken token)
    {
        if (stream.Length > maximum || stream.Length != length) return false;
        var hash = await SHA256.HashDataAsync(stream, token);
        return stream.Position == length && "sha256:" + Convert.ToHexString(hash).ToLowerInvariant() == expected.Value;
    }

    private static void RemoveNativeEntry(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(path, (attributes & FileAttributes.ReparsePoint) == 0);
        else File.Delete(path);
    }

    private static void RejectLinks(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar).Contains(".."))
            throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, "Native path escapes its cache root");
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, "Native cache paths below the root must not contain links");
        }
    }

    private async Task<FileStream> NativeLockAsync(string path, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(cacheLockTimeout);
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException error) when ((error.HResult & 0xffff) is 11 or 32 or 33)
                { await Task.Delay(25, timeout.Token); }
            }
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        { throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, "Native cache lock exceeded the configured timeout", error); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, "Native cache lock is unavailable", error); }
    }

    private async Task<T> ReadArtifactAsync<T>(ArtifactDigest digest, string extension,
        Func<Stream, WorkflowBundleReadLimits, CancellationToken, Task<T>> verify, CancellationToken token)
    {
        var path = Path.Combine(_directory, digest.Value[7..] + extension);
        FileStream? cacheLock = null;
        try
        {
            // All readers/writers coordinate per digest across processes. Keep the lock file:
            // unlinking it would permit two processes to lock different file identities.
            try
            {
                using var locking = CancellationTokenSource.CreateLinkedTokenSource(token);
                locking.CancelAfter(cacheLockTimeout);
                Directory.CreateDirectory(_directory);
                while (cacheLock == null)
                {
                    locking.Token.ThrowIfCancellationRequested();
                    try { cacheLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                    catch (IOException error) when (error is not (FileNotFoundException or DirectoryNotFoundException))
                    { await Task.Delay(25, locking.Token); }
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            if (cacheLock != null)
            {
                try
                {
                    await using var cached = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (cached.Length > limits.MaxArchiveBytes)
                    {
                        // A genuine limit must preserve a valid archive; poisoned oversized files
                        // must still be classified as corruption and evicted.
                        var hash = await SHA256.HashDataAsync(cached, token);
                        if (new ArtifactDigest("sha256:" + Convert.ToHexString(hash).ToLowerInvariant()) != digest)
                            throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, "The cached artifact archive digest differs from the published digest");
                        throw new WorkflowEngineException(WorkflowErrorKind.ResourceExhausted, "Artifact archive exceeds the configured size limit");
                    }
                    await using var verifiedCache = new MemoryStream();
                    await WorkflowBundleArchive.CopyArchiveAsync(cached, verifiedCache, digest, limits, token);
                    return await verify(verifiedCache, limits, token);
                }
                catch (WorkflowEngineException error) when (error.Kind == WorkflowErrorKind.ArtifactMismatch)
                {
                    // The per-digest lock prevents another cache writer from replacing this file.
                    TryDelete(path);
                    throw;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            // Lock waiting has its own bounded lifetime; each artifact gets a fresh fetch budget.
            using var fetch = CancellationTokenSource.CreateLinkedTokenSource(token);
            fetch.CancelAfter(fetchTimeout);
            try
            {
                await using var download = await artifacts.OpenAsync(digest, fetch.Token)
                    ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, $"Published artifact {digest} is unavailable");
                await using var temporary = new FileStream(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bundle"),
                    FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                await WorkflowBundleArchive.CopyArchiveAsync(download, temporary, digest, limits, fetch.Token);
                var verified = await verify(temporary, limits, fetch.Token);
                if (cacheLock != null)
                {
                    var staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        temporary.Position = 0;
                        await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                            await temporary.CopyToAsync(output, fetch.Token);
                        File.Move(staging, path, overwrite: true);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                    finally { TryDelete(staging); }
                }
                return verified;
            }
            catch (OperationCanceledException error) when (!token.IsCancellationRequested)
            {
                throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                    fetch.IsCancellationRequested ? $"Fetching artifact {digest} exceeded the configured timeout"
                        : $"Fetching artifact {digest} was cancelled by the artifact store", error);
            }
        }
        finally { cacheLock?.Dispose(); }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}

internal sealed class BundleLoadContext : AssemblyLoadContext
{
    private readonly IReadOnlyDictionary<string, Assembly> _shared;
    private readonly Dictionary<string, ReadOnlyMemory<byte>> _private = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, nint> _native = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly object _lifetime = new();
    private bool _unloading;
    private Action<AssemblyLoadContext>? _onUnloading;

    private readonly NativeLifetime _nativeLifetime;
    private readonly ILogger? _logger;
    private readonly object _nativeGate = new();

    internal BundleLoadContext(WorkflowBundle bundle, ArtifactDigest digest,
        IEnumerable<KeyValuePair<string, ReadOnlyMemory<byte>>> packageAssemblies, IReadOnlyDictionary<string, string> native,
        ILogger? logger = null) : base(digest.Value, isCollectible: true)
    {
        _logger = logger;
        _nativeLifetime = new(this, logger);
        // Observe retirement before the context is exposed. The runtime's public event
        // permits subscriptions after it has fired, so late subscribers need this state.
        Unloading += _ =>
        {
            Action<AssemblyLoadContext>? release;
            lock (_lifetime)
            {
                _unloading = true;
                release = _onUnloading;
                _onUnloading = null;
            }
            if (release != null)
                foreach (Action<AssemblyLoadContext> registration in release.GetInvocationList())
                {
                    try { registration(this); }
                    catch (Exception error) { LogCleanupFailure(_logger, error, "unloading registration"); }
                }
        };
        _shared = WorkflowSharedAssemblies.Validate(bundle.Manifest);
        foreach (var (path, bytes) in bundle.Entries.Where(pair => pair.Key.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).Concat(packageAssemblies))
        {
            using var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(bytes.ToArray()));
            var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            var name = metadata.GetString(metadata.GetAssemblyDefinition().Name);
            if (_shared.ContainsKey(name) || !_private.TryAdd(name, bytes))
                throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, $"Duplicate or shared private assembly '{name}'");
        }
        if (native.Count != 0) throw new ArgumentException("Native images must be verified before loading", nameof(native));
    }

    internal bool TryRegisterUnloading(Action<AssemblyLoadContext> release)
    {
        lock (_lifetime)
        {
            if (_unloading) return false;
            _onUnloading += release;
            return true;
        }
    }

    internal void UnregisterUnloading(Action<AssemblyLoadContext>? release)
    {
        lock (_lifetime) _onUnloading -= release;
    }

    protected override Assembly Load(AssemblyName assemblyName)
    {
        if (_shared.TryGetValue(assemblyName.Name!, out var shared)) return shared;
        if (_private.TryGetValue(assemblyName.Name!, out var bytes))
            return LoadFromStream(new MemoryStream(bytes.ToArray(), writable: false));
        // Returning null would allow the runtime to search the default context.
        throw new FileNotFoundException($"Private bundle dependency '{assemblyName}' is missing");
    }

    internal void LoadNative(IReadOnlyDictionary<string, string> paths, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> images)
    {
        lock (_nativeGate)
        {
            var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
            WorkflowNativeLibraries.Validate(images, rid);
            var visiting = new HashSet<string>(_native.Comparer);
            void Load(string name)
            {
                if (_native.ContainsKey(name)) return;
                if (!visiting.Add(name)) throw new DllNotFoundException("Cyclic declared native imports cannot be safely preloaded");
                foreach (var import in WorkflowNativeImports.Read(images[name]).Dependencies)
                {
                    var dependency = WorkflowNativeLibraries.DependencyName(import, rid);
                    if (images.ContainsKey(dependency)) Load(dependency);
                    else LoadSystem(import);
                }
                var handle = WorkflowNativeLibraries.Load(paths[name]);
                _nativeLifetime.Handles.Add(handle); _native.Add(name, handle);
                visiting.Remove(name);
            }
            foreach (var name in images.Keys) Load(name);
        }
    }

    internal void OwnNativeFiles(IEnumerable<FileStream> files, string? privateRoot)
    { _nativeLifetime.Files.AddRange(files); _nativeLifetime.PrivateRoot = privateRoot; }

    // Unloading is only a request: delegates, objects and even their finalizers may
    // still call P/Invoke. The runtime retains the context until its loader allocator
    // dies. A long weak reference also covers resurrection/finalizer ordering.
    private sealed class NativeLifetime(BundleLoadContext context, ILogger? logger)
    {
        private readonly WeakReference<BundleLoadContext> _context = new(context, trackResurrection: true);
        internal readonly List<nint> Handles = [];
        internal readonly List<FileStream> Files = [];
        internal string? PrivateRoot;
        private int _released;

        ~NativeLifetime()
        {
            if (_context.TryGetTarget(out _))
            {
                GC.ReRegisterForFinalize(this);
                return;
            }
            Release();
        }

        private void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            for (var i = Handles.Count - 1; i >= 0; i--)
                try { System.Runtime.InteropServices.NativeLibrary.Free(Handles[i]); }
                catch (Exception error) { LogCleanupFailure(logger, error, "native module release"); }
            Handles.Clear();
            foreach (var file in Files)
                try { file.Dispose(); }
                catch (Exception error) { LogCleanupFailure(logger, error, "native file release"); }
            Files.Clear();
            if (PrivateRoot != null)
                try { Directory.Delete(PrivateRoot, true); }
                catch (Exception error) { LogCleanupFailure(logger, error, "private native directory release"); }
            PrivateRoot = null;
        }
    }

    internal static void LogCleanupFailure(ILogger? logger, Exception error, string phase)
    {
        // Cleanup, including finalizer cleanup, must also survive a failing logger.
        try { logger?.LogWarning("Bundle cleanup failed during {Phase} ({ErrorType})", phase, error.GetType().FullName); }
        catch (Exception) { }
    }

    internal static void UnloadSafely(AssemblyLoadContext context, ILogger? logger)
    {
        try { context.Unload(); }
        catch (Exception error)
        {
            LogCleanupFailure(logger, error, "runtime unload");
            // The runtime clears the public event before invoking it. If a public
            // handler throws, a second call completes the interrupted transition.
            try { context.Unload(); }
            catch (Exception retry) { LogCleanupFailure(logger, retry, "runtime unload retry"); }
        }
    }

    private nint LoadSystem(string name, HashSet<string>? loading = null)
    {
        if (_native.TryGetValue(name, out var existing)) return existing;
        var macSystemPath = OperatingSystem.IsMacOS() && WorkflowNativeLibraries.IsSystemImport(name, "osx");
        var path = macSystemPath ? name : WorkflowNativeLibraries.SystemPath(name);
        if (path == null) throw new DllNotFoundException($"Native dependency '{name}' is not in an OS system directory");
        if (OperatingSystem.IsLinux())
        {
            loading ??= new(StringComparer.Ordinal);
            if (!loading.Add(name)) return 0; // OS library cycles are already in the trusted system closure.
            var table = WorkflowNativeImports.Read(File.ReadAllBytes(path));
            // DT_NEEDED must reuse the absolute system image, rather than trigger another search.
            if (name.Contains(".so", StringComparison.Ordinal) && table.Identity != name)
                throw new DllNotFoundException($"OS native dependency '{name}' has a different SONAME");
            foreach (var import in table.Dependencies)
                if (!_native.ContainsKey(import)) LoadSystem(import, loading);
        }
        var handle = WorkflowNativeLibraries.Load(path);
        _nativeLifetime.Handles.Add(handle); _native.Add(name, handle);
        return handle;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        lock (_nativeGate)
        {
            if (unmanagedDllName.IndexOfAny(['/', '\\', ':']) >= 0)
                throw new DllNotFoundException("Native imports must be names without paths");
            foreach (var candidate in new[] { unmanagedDllName, unmanagedDllName + ".dll", unmanagedDllName + ".so", "lib" + unmanagedDllName + ".so",
                         unmanagedDllName + ".dylib", "lib" + unmanagedDllName + ".dylib" })
                if (_native.TryGetValue(candidate, out var handle)) return handle;
            return LoadSystem(unmanagedDllName);
        }
    }

    internal void VerifyClosure(Assembly root)
    {
        var pending = new Queue<Assembly>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        pending.Enqueue(root);
        while (pending.TryDequeue(out var assembly))
        {
            if (!visited.Add(assembly.FullName!)) continue;
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                var dependency = LoadFromAssemblyName(reference);
                if (GetLoadContext(dependency) == this) pending.Enqueue(dependency);
            }
        }
    }
}

/// <summary>The exact shared compatibility profile. Application assemblies are private bundle assets.</summary>
public static class WorkflowSharedAssemblies
{
    public static bool IsSharedName(string name) => Profile.ContainsKey(name);
    public static AssemblyName? Identity(string name) => Profile.TryGetValue(name, out var identity)
        ? identity.Version == null ? AssemblyLoadContext.Default.LoadFromAssemblyName(identity).GetName() : new AssemblyName(identity.FullName!) : null;
    private static readonly IReadOnlyDictionary<string, AssemblyName> Framework;
    private static readonly IReadOnlyDictionary<string, AssemblyName> Profile;

    static WorkflowSharedAssemblies()
    {
        Framework = RuntimeAssemblies((string?)AppContext.GetData("APP_CONTEXT_DEPS_FILES"), AppContext.BaseDirectory,
            out var available);
        HasFrameworkIdentities = available;
        Profile = CreateProfile();
    }

    /// <summary>Whether runtime framework identity discovery completed with a usable framework.</summary>
    public static bool HasFrameworkIdentities { get; }

    /// <summary>Matches an assembly's full identity against this runtime's shared frameworks.</summary>
    public static bool IsFrameworkAssembly(AssemblyName name) => IsFrameworkAssembly(name, Framework);

    internal static bool IsFrameworkAssembly(AssemblyName name, IReadOnlyDictionary<string, AssemblyName> identities) => name.Name != null &&
        identities.TryGetValue(name.Name, out var identity) && name.Version == identity.Version &&
        (name.CultureName ?? "") == (identity.CultureName ?? "") &&
        name.GetPublicKeyToken().AsSpan().SequenceEqual(identity.GetPublicKeyToken());

    /// <summary>
    /// Whether this process found its runtime and SDK identities. Self-contained single-file processes
    /// expose no runtime manifest, so they can neither publish nor host bundles.
    /// </summary>
    public static bool IsUsable => IsShared(typeof(WorkflowDefinition).Assembly.GetName());

    public static bool IsShared(AssemblyName name) => name.Name != null &&
        Profile.TryGetValue(name.Name, out var identity) &&
        (name.CultureName ?? "") == (identity.CultureName ?? "") &&
        name.GetPublicKeyToken().AsSpan().SequenceEqual(identity.GetPublicKeyToken());

    private static IReadOnlyDictionary<string, AssemblyName> CreateProfile()
    {
        var profile = new Dictionary<string, AssemblyName>(Framework, StringComparer.OrdinalIgnoreCase);
        // Do not let the SDK dependency walk reconstruct a partial runtime profile after
        // failed discovery (including an embedded single-file manifest).
        if (!HasFrameworkIdentities) return profile;
        AddSdkAssemblies(profile, [typeof(WorkflowDefinition).Assembly]);
        profile["IxIFlow.Templates"] = new AssemblyName("IxIFlow.Templates");
        return profile;
    }

    internal static Dictionary<string, AssemblyName> RuntimeAssemblies(string? depsFiles, string applicationDirectory)
        => RuntimeAssemblies(depsFiles, applicationDirectory, out _);

    private static Dictionary<string, AssemblyName> RuntimeAssemblies(string? depsFiles, string applicationDirectory,
        out bool available)
    {
        available = false;
        var profile = new Dictionary<string, AssemblyName>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in (depsFiles ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var path = Path.GetFullPath(file, applicationDirectory);
                var directory = Path.GetDirectoryName(path)!;
                using var manifest = JsonDocument.Parse(File.ReadAllBytes(path));
                var root = manifest.RootElement;
                // The application's deps contains project/package libraries as well as
                // runtimepacks. Framework deps contains only the framework's own assets.
                var application = Path.TrimEndingDirectorySeparator(directory).Equals(
                    Path.TrimEndingDirectorySeparator(applicationDirectory), OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                    root.GetProperty("libraries").EnumerateObject().Any(library =>
                        library.Value.GetProperty("type").GetString() == "project");
                var target = root.GetProperty("targets").GetProperty(root.GetProperty("runtimeTarget").GetProperty("name").GetString()!);
                foreach (var library in target.EnumerateObject())
                {
                    if (application && !library.Name.StartsWith("runtimepack.", StringComparison.Ordinal)) continue;
                    if (!library.Value.TryGetProperty("runtime", out var assets)) continue;
                    foreach (var asset in assets.EnumerateObject())
                    {
                        if (!asset.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                        var assetPath = Path.Combine(directory, asset.Name);
                        if (!File.Exists(assetPath)) assetPath = Path.Combine(applicationDirectory, Path.GetFileName(asset.Name));
                        var identity = AssemblyName.GetAssemblyName(assetPath);
                        profile[identity.Name!] = identity;
                    }
                }
            }
            available = profile.ContainsKey("System.Private.CoreLib") && profile.ContainsKey("System.Runtime");
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        { profile.Clear(); }
        return profile;
    }

    internal static void AddSdkAssemblies(IDictionary<string, AssemblyName> profile, IEnumerable<Assembly> roots)
    {
        // An optional transitive reference cannot poison the shared profile's initializer.
        var pending = new Queue<Assembly>();
        foreach (var root in roots) pending.Enqueue(root);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.TryDequeue(out var assembly))
        {
            var identity = assembly.GetName();
            if (!visited.Add(identity.Name!)) continue;
            profile[identity.Name!] = identity;
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (profile.ContainsKey(reference.Name!)) continue;
                try { pending.Enqueue(AssemblyLoadContext.Default.LoadFromAssemblyName(reference)); }
                catch (Exception error) when (error is FileNotFoundException or FileLoadException or BadImageFormatException) { }
            }
        }
    }

    internal static bool IsCompatible(WorkflowBundleManifest manifest)
    {
        try
        {
            WorkflowBundleArchive.CheckManifest(manifest);
            Validate(manifest);
            return manifest.ActivityPackages.All(package => !package.AppliesTo(System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier) || !package.RequiresRuntimeIdentifier ||
                package.RuntimeAssets.ContainsKey(System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier));
        }
        // An incompatible profile is an ordinary answer; a corrupt manifest reaches the caller's memo, which logs it once.
        catch (Exception error) when (error is WorkflowEngineException { Kind: not WorkflowErrorKind.ArtifactMismatch } or ArgumentException or FileLoadException or BadImageFormatException) { return false; }
    }

    internal static IReadOnlyDictionary<string, Assembly> Validate(WorkflowBundleManifest manifest)
    {
        if (!Profile.ContainsKey("System.Private.CoreLib"))
            throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, "Shared compatibility profile omits 'System.Private.CoreLib'");
        if (manifest.EngineVersion != typeof(WorkflowDefinition).Assembly.GetName().Version!.ToString() ||
            manifest.CheckpointContractVersion != WorkflowDefinitionValidator.CheckpointContractVersion)
            throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, "Bundle engine/checkpoint compatibility profile differs from this host");
        var result = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        foreach (var (identity, version) in manifest.SharedAssemblies)
        {
            var requested = new AssemblyName(identity);
            if (!IsShared(requested))
                throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, $"Assembly '{identity}' is outside the shared compatibility profile");
            Assembly assembly;
            try { assembly = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(requested.Name!)); }
            catch (Exception error) when (error is FileNotFoundException or FileLoadException)
            { throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, $"Shared assembly '{identity}' is unavailable", error); }
            var actual = assembly.GetName();
            if (actual.Version?.ToString() != version || requested.Version?.ToString() != version ||
                !requested.GetPublicKeyToken().AsSpan().SequenceEqual(actual.GetPublicKeyToken()) ||
                (requested.CultureName ?? "") != (actual.CultureName ?? "") || !result.TryAdd(actual.Name!, assembly))
                throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                    $"Shared assembly profile mismatch for '{requested.Name}': bundle requires {identity} ({version}); host has {actual}");
        }
        foreach (var name in new[] { "IxIFlow", "System.Private.CoreLib", "System.Runtime" })
            if (!result.ContainsKey(name))
                throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, $"Shared compatibility profile omits '{name}'");
        return result;
    }
}

internal sealed class LoadedBundle(BundleLoadContext context, WorkflowDefinition definition)
{
    internal BundleLoadContext Context { get; } = context;
    internal WorkflowDefinition Definition { get; } = definition;
}

internal sealed class WorkflowBundleLoader(IArtifactStore artifacts, WorkflowBundleOptions options, ILogger? logger = null) : IDisposable
{
    private readonly object _gate = new();
    private readonly BundleCache _cache = new(artifacts, options.CacheDirectory, options.ReadLimits, options.FetchTimeout, options.CacheLockTimeout);
    private readonly Dictionary<ArtifactDigest, Entry> _loaded = [];
    private long _clock;
    private bool _disposed;

    private sealed class Entry
    {
        internal readonly CancellationTokenSource Stop = new();
        internal required Lazy<Task<LoadedBundle>> Load;
        internal int Users;
        internal long Used;
    }

    internal async Task<WorkflowDefinitionLease> AcquireAsync(ArtifactDigest digest, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Entry entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_loaded.TryGetValue(digest, out entry!))
            {
                entry = new Entry { Load = null! };
                var created = entry;
                entry.Load = new(() =>
                {
                    // A nested acquisition must not give the cached load its caller's bundle scope.
                    using (ExecutionContextFlow.SuppressIfNeeded())
                        return Task.Run(() => LoadAsync(digest, created.Stop.Token));
                });
                _loaded.Add(digest, entry);
            }
            Interlocked.Increment(ref entry.Users);
            entry.Used = Interlocked.Increment(ref _clock);
        }
        try
        {
            var loaded = await entry.Load.Value.WaitAsync(token);
            Trim();
            return Lease(entry, loaded);
        }
        catch { Release(entry); throw; }
    }

    private async Task<LoadedBundle> LoadAsync(ArtifactDigest digest, CancellationToken token)
    {
        try
        {
            var bundle = await _cache.ReadAsync(digest, token);
            token.ThrowIfCancellationRequested();
            var managed = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
            var native = new Dictionary<string, (ReadOnlyMemory<byte> Bytes, ArtifactDigest Hash)>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var pin in bundle.Manifest.ActivityPackages.Where(pin => pin.AppliesTo(System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier)))
            {
                var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
                var selected = pin.RuntimeAssets.GetValueOrDefault(rid) ??
                    (!pin.RequiresRuntimeIdentifier ? pin.RuntimeAssets.GetValueOrDefault("") : null)
                    ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, $"Package '{pin.Id}@{pin.Version}' has no native assets for runtime '{rid}'");
                var entries = await _cache.ReadPackageAsync(pin, token);
                foreach (var path in selected.ManagedAssemblies) managed.Add(pin.Id + "/" + path, entries[path]);
                foreach (var path in selected.NativeLibraries)
                {
                    if (!native.TryAdd(Path.GetFileName(path), (entries[path], pin.Entries[path])))
                        throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, $"Duplicate native package asset '{path}'");
                }
            }
            var context = new BundleLoadContext(bundle, digest, managed, new Dictionary<string, string>(), logger);
            try
            {
                await _cache.LoadNativeAsync(digest, native, context, token);
                using var assembly = new MemoryStream(bundle.Entries[WorkflowBundleArchive.AssemblyPath].ToArray());
                using var pdb = new MemoryStream(bundle.Entries[WorkflowBundleArchive.PdbPath].ToArray());
                var entryAssembly = context.LoadFromStream(assembly, pdb);
                context.VerifyClosure(entryAssembly);
                var entryType = entryAssembly.GetType(bundle.Manifest.EntryType, throwOnError: true)!;
                using var types = WorkflowTypeIdentity.Enter(context);
                var build = entryType.GetMethod("Build", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes)
                    ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, "Bundle entry has no public static Build()");
                var definition = (WorkflowDefinition)build.Invoke(null, null)!;
                if (definition.Name != bundle.Manifest.Name || definition.Version != bundle.Manifest.Version ||
                    WorkflowDefinitionValidator.Inspect(definition) != bundle.Manifest.StructureHash ||
                    WorkflowTypeIdentity.SavedName(definition.WorkflowDataType!) != bundle.Manifest.DataType ||
                    WorkflowDefinitionValidator.GetCombinerSignature(definition) != bundle.Manifest.CombinerSignature)
                    throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, "Built definition differs from the verified bundle manifest");
                definition.BundleDigest = digest;
                definition.BundleContext = context;
                definition.BundleSources = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [WorkflowBundleArchive.YamlPath] = System.Text.Encoding.UTF8.GetString(bundle.Entries[WorkflowBundleArchive.YamlPath].Span),
                    [WorkflowBundleArchive.SourcePath] = System.Text.Encoding.UTF8.GetString(bundle.Entries[WorkflowBundleArchive.SourcePath].Span)
                };
                token.ThrowIfCancellationRequested();
                return new(context, definition);
            }
            catch { Retire(context); throw; }
        }
        catch (Exception error) when (error is not (WorkflowEngineException or OperationCanceledException or OutOfMemoryException))
        { throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, $"Could not load bundle {digest}: {error.GetBaseException().Message}", error); }
    }

    private WorkflowDefinitionLease Lease(Entry entry, LoadedBundle bundle) => new(bundle.Definition,
        () => Release(entry), () => Retain(entry, bundle));

    private WorkflowDefinitionLease Retain(Entry entry, LoadedBundle bundle)
    {
        Interlocked.Increment(ref entry.Users);
        return Lease(entry, bundle);
    }

    private void Release(Entry entry)
    {
        if (Interlocked.Decrement(ref entry.Users) == 0) Trim();
    }

    private void Trim()
    {
        var retired = new List<Entry>();
        lock (_gate)
        {
            foreach (var pair in _loaded.Where(pair => Volatile.Read(ref pair.Value.Users) == 0)
                         .OrderBy(pair => pair.Value.Used).ToArray())
            {
                var loading = pair.Value.Load;
                if (!_disposed && loading.IsValueCreated && loading.Value.IsCompletedSuccessfully && _loaded.Count <= options.MaxCachedBundles)
                    continue;
                _loaded.Remove(pair.Key);
                retired.Add(pair.Value);
            }
        }
        foreach (var entry in retired)
        {
            try { entry.Stop.Cancel(); }
            catch (Exception error) { BundleLoadContext.LogCleanupFailure(logger, error, "load cancellation"); }
            using (ExecutionContextFlow.SuppressIfNeeded())
                _ = entry.Load.Value.ContinueWith(task =>
                {
                    try
                    {
                        if (task.IsCompletedSuccessfully) Retire(task.Result.Context);
                        else _ = task.Exception;
                    }
                    catch (Exception error) { BundleLoadContext.LogCleanupFailure(logger, error, "bundle retirement"); }
                    finally { entry.Stop.Dispose(); }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private void Retire(BundleLoadContext context)
    {
        try { WorkflowJson.Release(context); }
        catch (Exception error) { BundleLoadContext.LogCleanupFailure(logger, error, "JSON profile release"); }
        BundleLoadContext.UnloadSafely(context, logger);
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        Trim();
    }
}
