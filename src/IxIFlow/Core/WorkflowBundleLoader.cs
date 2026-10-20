using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using IxIFlow.Core.Runtime;

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

    internal async Task<WorkflowBundle> ReadAsync(ArtifactDigest digest, CancellationToken token)
    {
        var path = Path.Combine(_directory, digest.Value[7..] + ".bundle");
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
                            throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, "The cached bundle archive digest differs from the published digest");
                        throw new WorkflowEngineException(WorkflowErrorKind.ResourceExhausted, "Bundle archive exceeds the configured size limit");
                    }
                    return await WorkflowBundleArchive.ReadAsync(cached, digest, token, limits);
                }
                catch (WorkflowEngineException error) when (error.Kind == WorkflowErrorKind.ArtifactMismatch)
                {
                    // The per-digest lock prevents another cache writer from replacing this file.
                    TryDelete(path);
                    throw;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            // Lock contention has its own bounded lifetime. Only the download and
            // verification consume the fetch budget.
            using var fetch = CancellationTokenSource.CreateLinkedTokenSource(token);
            fetch.CancelAfter(fetchTimeout);
            try
            {
                await using var download = await artifacts.OpenAsync(digest, fetch.Token)
                    ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, $"Bundle artifact {digest} is unavailable");
                await using var temporary = new FileStream(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bundle"),
                    FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                await WorkflowBundleArchive.CopyArchiveAsync(download, temporary, digest, limits, fetch.Token);
                var verified = await WorkflowBundleArchive.ReadVerifiedAsync(temporary, limits, fetch.Token);
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
                    fetch.IsCancellationRequested ? $"Fetching bundle {digest} exceeded the configured timeout"
                        : $"Fetching bundle {digest} was cancelled by the artifact store", error);
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
    private readonly object _lifetime = new();
    private bool _unloading;
    private Action<AssemblyLoadContext>? _onUnloading;

    internal BundleLoadContext(WorkflowBundle bundle, ArtifactDigest digest) : base(digest.Value, isCollectible: true)
    {
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
            release?.Invoke(this);
        };
        _shared = WorkflowSharedAssemblies.Validate(bundle.Manifest);
        foreach (var (path, bytes) in bundle.Entries.Where(pair => pair.Key.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            using var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(bytes.ToArray()));
            var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            var name = metadata.GetString(metadata.GetAssemblyDefinition().Name);
            if (_shared.ContainsKey(name) || !_private.TryAdd(name, bytes))
                throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, $"Duplicate or shared private assembly '{name}'");
        }
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

    protected override nint LoadUnmanagedDll(string unmanagedDllName) =>
        throw new DllNotFoundException($"Native bundle dependency '{unmanagedDllName}' is not declared");

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
        try { Validate(manifest); return manifest.ActivityPackages.Count == 0; }
        catch (Exception error) when (error is WorkflowEngineException or ArgumentException or FileLoadException or BadImageFormatException) { return false; }
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

internal sealed class WorkflowBundleLoader(IArtifactStore artifacts, WorkflowBundleOptions options) : IDisposable
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
            var context = new BundleLoadContext(bundle, digest);
            try
            {
                // Refuse package imports until package artifact loading is implemented.
                if (bundle.Manifest.ActivityPackages.Count != 0)
                    throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, "Activity-package imports are not supported by bundle hosts yet");
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
            catch { WorkflowJson.Release(context); context.Unload(); throw; }
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
            entry.Stop.Cancel();
            using (ExecutionContextFlow.SuppressIfNeeded())
                _ = entry.Load.Value.ContinueWith(task =>
                {
                    if (task.IsCompletedSuccessfully)
                    {
                        WorkflowJson.Release(task.Result.Context);
                        task.Result.Context.Unload();
                    }
                    else _ = task.Exception;
                    entry.Stop.Dispose();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        Trim();
    }
}
