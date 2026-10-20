using System.Reflection;
using System.Runtime.Loader;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

/// <summary>Enables published workflows on a host. Idle contexts are bounded independently of running operations.</summary>
public sealed record WorkflowBundleOptions(string CacheDirectory, int MaxCachedBundles = 16);

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

internal sealed class BundleCache(IArtifactStore artifacts, string directory)
{
    private readonly string _directory = Path.GetFullPath(directory);

    internal async Task<WorkflowBundle> ReadAsync(ArtifactDigest digest, CancellationToken token)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, digest.Value[7..] + ".bundle");
        if (File.Exists(path))
        {
            try
            {
                await using var cached = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                return await WorkflowBundleArchive.ReadAsync(cached, digest, token);
            }
            catch (WorkflowEngineException error) when (error.Kind == WorkflowErrorKind.ArtifactMismatch)
            {
                File.Delete(path);
                throw;
            }
        }
        await using var download = await artifacts.OpenAsync(digest, token)
            ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, $"Bundle artifact {digest} is unavailable");
        using var bytes = new MemoryStream();
        await download.CopyToAsync(bytes, token);
        bytes.Position = 0;
        var verified = await WorkflowBundleArchive.ReadAsync(bytes, digest, token);
        var temporary = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes.ToArray(), token);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
        return verified;
    }
}

internal sealed class BundleLoadContext : AssemblyLoadContext
{
    private readonly IReadOnlyDictionary<string, Assembly> _shared;
    private readonly Dictionary<string, ReadOnlyMemory<byte>> _private = new(StringComparer.OrdinalIgnoreCase);

    internal BundleLoadContext(WorkflowBundle bundle, ArtifactDigest digest) : base(digest.Value, isCollectible: true)
    {
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
    public static bool IsShared(AssemblyName name) => name.Name is "IxIFlow" or "IxIFlow.Abstractions" or "IxIFlow.Templates" ||
        name.Name is "netstandard" or "mscorlib" or "Microsoft.CSharp" or "Microsoft.VisualBasic" or "Microsoft.VisualBasic.Core" || name.Name!.StartsWith("System", StringComparison.Ordinal) ||
        name.Name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal);

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
        foreach (var name in new[] { "IxIFlow", "IxIFlow.Abstractions", "System.Private.CoreLib" })
            if (!result.ContainsKey(name))
                throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, $"Shared compatibility profile omits '{name}'");
        return result;
    }
}

internal sealed class LoadedBundle(BundleLoadContext context, WorkflowDefinition definition)
{
    internal BundleLoadContext Context { get; } = context;
    internal WorkflowDefinition Definition { get; } = definition;
    internal int Users { get; set; }
    internal long Used { get; set; }
}

internal sealed class WorkflowBundleLoader(IArtifactStore artifacts, WorkflowBundleOptions options) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly BundleCache _cache = new(artifacts, options.CacheDirectory);
    private readonly Dictionary<ArtifactDigest, LoadedBundle> _loaded = [];
    private long _clock;
    private bool _disposed;

    internal async Task<WorkflowDefinitionLease> AcquireAsync(ArtifactDigest digest)
    {
        var held = false;
        try
        {
            var bundle = await _cache.ReadAsync(digest, CancellationToken.None);
            await _gate.WaitAsync();
            held = true;
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Verify disk content on every acquisition, including a warm loaded version.
            if (!_loaded.TryGetValue(digest, out var loaded))
            {
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
                    loaded = new(context, definition);
                    _loaded.Add(digest, loaded);
                }
                catch { context.Unload(); throw; }
            }
            loaded.Users++;
            loaded.Used = ++_clock;
            Trim();
            return Lease(loaded);
        }
        catch (Exception error) when (error is not (WorkflowEngineException or OutOfMemoryException))
        { throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, $"Could not load bundle {digest}: {error.GetBaseException().Message}", error); }
        finally { if (held) _gate.Release(); }
    }

    private WorkflowDefinitionLease Lease(LoadedBundle bundle) => new(bundle.Definition,
        () => Release(bundle), () => Retain(bundle));

    private WorkflowDefinitionLease Retain(LoadedBundle bundle)
    {
        _gate.Wait();
        try
        {
            if (bundle.Users == 0) throw new ObjectDisposedException(nameof(WorkflowDefinitionLease));
            bundle.Users++;
            return Lease(bundle);
        }
        finally { _gate.Release(); }
    }

    private void Release(LoadedBundle bundle)
    {
        _gate.Wait();
        try { bundle.Users--; Trim(); }
        finally { _gate.Release(); }
    }

    private void Trim()
    {
        foreach (var pair in _loaded.Where(pair => pair.Value.Users == 0).OrderBy(pair => pair.Value.Used)
                     .Take(_disposed ? _loaded.Count : Math.Max(0, _loaded.Count - options.MaxCachedBundles)).ToArray())
        {
            _loaded.Remove(pair.Key);
            WorkflowJson.Release(pair.Value.Context);
            pair.Value.Context.Unload();
        }
    }

    public void Dispose()
    {
        _gate.Wait();
        try { _disposed = true; Trim(); }
        finally { _gate.Release(); }
    }
}
