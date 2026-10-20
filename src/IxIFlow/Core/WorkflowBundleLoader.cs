using System.Reflection;
using System.Runtime.Loader;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

/// <summary>Enables published workflows on a host. Idle contexts are bounded independently of running operations.</summary>
public sealed record WorkflowBundleOptions(string CacheDirectory, int MaxCachedBundles = 16)
{
    public WorkflowBundleReadLimits ReadLimits { get; init; } = new();
    public TimeSpan FetchTimeout { get; init; } = TimeSpan.FromSeconds(30);
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

internal sealed class BundleCache(IArtifactStore artifacts, string directory, WorkflowBundleReadLimits limits)
{
    private readonly string _directory = Path.GetFullPath(directory);

    internal Task<WorkflowBundle> ReadAsync(ArtifactDigest digest, CancellationToken token) =>
        ReadArtifactAsync(digest, ".bundle", WorkflowBundleArchive.ReadVerifiedAsync, token);

    internal async Task<IReadOnlyDictionary<string, ReadOnlyMemory<byte>>> ReadPackageAsync(WorkflowPackagePin pin, CancellationToken token)
    {
        try
        {
            var entries = await ReadArtifactAsync(pin.Digest, ".nupkg", WorkflowPackageArchive.ReadVerifiedAsync, token);
            WorkflowPackageArchive.Verify(pin, entries);
            return entries;
        }
        catch (WorkflowEngineException error) when (error.Kind is WorkflowErrorKind.ArtifactMismatch or WorkflowErrorKind.ArtifactUnavailable)
        { throw new WorkflowEngineException(error.Kind, $"Activity package '{pin.Id}@{pin.Version}' ({pin.Digest}): {error.Message}", error); }
    }

    internal async Task<string> NativePathAsync(ArtifactDigest bundle, WorkflowPackagePin pin, string entry, ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        // Keep the selected set together so libraries using $ORIGIN / sibling DLLs can find their dependencies.
        var root = Path.Combine(_directory, "native", bundle.Value[7..], System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, Path.GetFileName(entry));
        if (File.Exists(path) && ArtifactDigest.Of(await File.ReadAllBytesAsync(path, token)) == pin.Entries[entry]) return path;
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes.ToArray(), token);
            File.Move(temporary, path, overwrite: true);
        }
        finally { TryDelete(temporary); }
        return path;
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
                Directory.CreateDirectory(_directory);
                while (cacheLock == null)
                {
                    token.ThrowIfCancellationRequested();
                    try { cacheLock = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                    catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 or 11)
                    { await Task.Delay(25, token); }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            if (cacheLock != null)
            {
                try
                {
                    await using var cached = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
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
            await using var download = await artifacts.OpenAsync(digest, token)
                ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, $"Published artifact {digest} is unavailable");
            await using var temporary = new FileStream(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bundle"),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            await WorkflowBundleArchive.CopyArchiveAsync(download, temporary, digest, limits, token);
            var verified = await verify(temporary, limits, token);
            if (cacheLock != null)
            {
                var staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    temporary.Position = 0;
                    await using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                        await temporary.CopyToAsync(output, token);
                    File.Move(staging, path, overwrite: true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                finally { TryDelete(staging); }
            }
            return verified;
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
    private readonly Dictionary<string, string> _native = new(StringComparer.Ordinal);

    internal BundleLoadContext(WorkflowBundle bundle, ArtifactDigest digest,
        IEnumerable<KeyValuePair<string, ReadOnlyMemory<byte>>> packageAssemblies, IReadOnlyDictionary<string, string> native) : base(digest.Value, isCollectible: true)
    {
        _shared = WorkflowSharedAssemblies.Validate(bundle.Manifest);
        foreach (var (path, bytes) in bundle.Entries.Where(pair => pair.Key.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).Concat(packageAssemblies))
        {
            using var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(bytes.ToArray()));
            var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            var name = metadata.GetString(metadata.GetAssemblyDefinition().Name);
            if (_shared.ContainsKey(name) || !_private.TryAdd(name, bytes))
                throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, $"Duplicate or shared private assembly '{name}'");
        }
        foreach (var pair in native) _native.Add(pair.Key, pair.Value);
    }

    protected override Assembly Load(AssemblyName assemblyName)
    {
        if (_shared.TryGetValue(assemblyName.Name!, out var shared)) return shared;
        if (_private.TryGetValue(assemblyName.Name!, out var bytes))
            return LoadFromStream(new MemoryStream(bytes.ToArray(), writable: false));
        // Returning null would allow the runtime to search the default context.
        throw new FileNotFoundException($"Private bundle dependency '{assemblyName}' is missing");
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        foreach (var candidate in new[] { unmanagedDllName, unmanagedDllName + ".dll", unmanagedDllName + ".so", "lib" + unmanagedDllName + ".so",
                     unmanagedDllName + ".dylib", "lib" + unmanagedDllName + ".dylib" })
            if (_native.TryGetValue(candidate, out var path)) return LoadUnmanagedDllFromPath(path);
        throw new DllNotFoundException($"Native bundle dependency '{unmanagedDllName}' is not declared");
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
    private static readonly IReadOnlyDictionary<string, AssemblyName> Profile = CreateProfile();
    public static bool IsSharedName(string name) => Profile.ContainsKey(name);
    public static AssemblyName? Identity(string name) => Profile.TryGetValue(name, out var identity)
        ? identity.Version == null ? AssemblyLoadContext.Default.LoadFromAssemblyName(identity).GetName() : new AssemblyName(identity.FullName!) : null;

    public static bool IsShared(AssemblyName name) => name.Name != null &&
        Profile.TryGetValue(name.Name, out var identity) &&
        (name.CultureName ?? "") == (identity.CultureName ?? "") &&
        name.GetPublicKeyToken().AsSpan().SequenceEqual(identity.GetPublicKeyToken());

    private static IReadOnlyDictionary<string, AssemblyName> CreateProfile()
    {
        var profile = new Dictionary<string, AssemblyName>(StringComparer.OrdinalIgnoreCase);
        var runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var sharedRoot = Directory.GetParent(runtime.TrimEnd(Path.DirectorySeparatorChar))?.Parent?.FullName;
        var paths = Directory.EnumerateFiles(runtime, "*.dll").Concat(
            ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator)
            .Where(path => sharedRoot != null && Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(path))) == sharedRoot));
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { var identity = AssemblyName.GetAssemblyName(path); profile[identity.Name!] = identity; }
            catch (BadImageFormatException) { }
        }
        // Only the declared SDK roots and their dependencies extend the runtime profile.
        var pending = new Queue<Assembly>();
        pending.Enqueue(typeof(WorkflowDefinition).Assembly);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.TryDequeue(out var assembly))
        {
            var identity = assembly.GetName();
            if (!visited.Add(identity.Name!)) continue;
            profile[identity.Name!] = identity;
            foreach (var reference in assembly.GetReferencedAssemblies())
                if (!profile.ContainsKey(reference.Name!)) pending.Enqueue(AssemblyLoadContext.Default.LoadFromAssemblyName(reference));
        }
        profile["IxIFlow.Templates"] = new AssemblyName("IxIFlow.Templates");
        return profile;
    }

    internal static bool IsCompatible(WorkflowBundleManifest manifest)
    {
        try
        {
            WorkflowBundleArchive.CheckManifest(manifest);
            Validate(manifest);
            return manifest.ActivityPackages.All(package => !package.RequiresRuntimeIdentifier ||
                package.RuntimeAssets.ContainsKey(System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier));
        }
        catch (Exception error) when (error is WorkflowEngineException or ArgumentException or FileLoadException or BadImageFormatException) { return false; }
    }

    internal static IReadOnlyDictionary<string, Assembly> Validate(WorkflowBundleManifest manifest)
    {
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
    private readonly BundleCache _cache = new(artifacts, options.CacheDirectory, options.ReadLimits);
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
                entry.Load = new(() => Task.Run(() => LoadAsync(digest, created.Stop.Token)));
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
            using var fetch = CancellationTokenSource.CreateLinkedTokenSource(token);
            fetch.CancelAfter(options.FetchTimeout);
            var bundle = await _cache.ReadAsync(digest, fetch.Token);
            token.ThrowIfCancellationRequested();
            var managed = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
            var native = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var pin in bundle.Manifest.ActivityPackages)
            {
                var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
                var selected = pin.RuntimeAssets.GetValueOrDefault(rid) ??
                    (!pin.RequiresRuntimeIdentifier ? pin.RuntimeAssets.GetValueOrDefault("") : null)
                    ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, $"Package '{pin.Id}@{pin.Version}' has no native assets for runtime '{rid}'");
                var entries = await _cache.ReadPackageAsync(pin, fetch.Token);
                foreach (var path in selected.ManagedAssemblies) managed.Add(pin.Id + "/" + path, entries[path]);
                foreach (var path in selected.NativeLibraries)
                {
                    var file = await _cache.NativePathAsync(digest, pin, path, entries[path], fetch.Token);
                    if (!native.TryAdd(Path.GetFileName(path), file))
                        throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, $"Duplicate native package asset '{path}'");
                }
            }
            var context = new BundleLoadContext(bundle, digest, managed, native);
            try
            {
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
