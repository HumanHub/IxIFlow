using System.Reflection;
using IxIFlow.Core;
using NuGet.Client;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.ContentModel;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Resolver;
using NuGet.RuntimeModel;
using NuGet.Versioning;

namespace IxIFlow.Authoring;

internal sealed class ResolvedWorkflowPackages : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "IxIFlow.Publish", Guid.NewGuid().ToString("N"));
    internal List<WorkflowPackagePin> Pins { get; } = [];
    internal List<string> References { get; } = [];
    internal Dictionary<string, byte[]> RuntimeAssemblies { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal List<byte[]> AllRuntimeAssemblies { get; } = [];
    internal HashSet<string> RuntimeNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, (string Id, string Version)> Owners { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal void Add(WorkflowPackage package, WorkflowPackagePin pin, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> entries)
    {
        Pins.Add(pin);
        var root = Path.GetFullPath(Path.Combine(_directory, package.Digest.Value[7..]));
        if (!root.StartsWith(Path.GetFullPath(_directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Package scratch path escapes its root");
        Directory.CreateDirectory(root);
        foreach (var path in pin.ReferenceAssemblies)
        {
            var name = AssemblyIdentity(entries[path]);
            if (WorkflowSharedAssemblies.IsShared(name)) continue;
            var file = Path.Combine(root, Path.GetFileName(path));
            File.WriteAllBytes(file, entries[path].ToArray());
            References.Add(file);
            if (!Owners.TryAdd(name.Name!, (package.Id, package.Version)))
                throw new InvalidOperationException($"Packages contain duplicate reference assembly '{name.Name}'");
        }
        // Build runs on the publisher's platform; no native code is executed by activity validation.
        var assets = pin.RuntimeAssets.GetValueOrDefault(System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier)
            ?? (!pin.RequiresRuntimeIdentifier ? pin.RuntimeAssets.GetValueOrDefault("") : null);
        foreach (var path in pin.RuntimeAssets.Values.SelectMany(selection => selection.ManagedAssemblies).Distinct(StringComparer.Ordinal))
        {
            AllRuntimeAssemblies.Add(entries[path].ToArray());
            RuntimeNames.Add(AssemblyIdentity(entries[path]).Name!);
        }
        foreach (var path in assets?.ManagedAssemblies ?? [])
        {
            var name = AssemblyIdentity(entries[path]);
            if (!WorkflowSharedAssemblies.IsShared(name) && !RuntimeAssemblies.TryAdd(name.Name!, entries[path].ToArray()))
                throw new InvalidOperationException($"Packages contain duplicate runtime assembly '{name.Name}'");
        }
    }

    internal static AssemblyName AssemblyIdentity(ReadOnlyMemory<byte> bytes)
    {
        using var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(bytes.ToArray()));
        var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        var definition = metadata.GetAssemblyDefinition();
        var name = new AssemblyName { Name = metadata.GetString(definition.Name), Version = definition.Version,
            CultureName = definition.Culture.IsNil ? null : metadata.GetString(definition.Culture) };
        if (!definition.PublicKey.IsNil) name.SetPublicKey(metadata.GetBlobBytes(definition.PublicKey));
        return name;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}

internal sealed class WorkflowPackageResolver(IWorkflowCatalogStore catalog, IArtifactStore artifacts, WorkflowPublisherOptions options)
{
    internal static async Task<WorkflowPackage> UploadAsync(IWorkflowCatalogStore catalog, IArtifactStore artifacts,
        Stream content, WorkflowBundleReadLimits limits, CancellationToken token)
    {
        WorkflowPackage package;
        byte[] bytes;
        try
        {
            using var buffer = new MemoryStream();
            await WorkflowBundleArchiveCopyAsync(content, buffer, limits.MaxArchiveBytes, token);
            bytes = buffer.ToArray();
            var digest = ArtifactDigest.Of(bytes);
            var entries = await WorkflowPackageArchive.ReadAsync(new MemoryStream(bytes), digest, limits, token);
            using var reader = new PackageArchiveReader(new MemoryStream(bytes));
            var identity = reader.GetIdentity();
            PackageIdValidator.ValidatePackageId(identity.Id);
            package = new WorkflowPackage(identity.Id.ToLowerInvariant(), VersionKey(identity.Version), digest,
                entries.ToDictionary(pair => pair.Key, pair => ArtifactDigest.Of(pair.Value.Span), StringComparer.Ordinal));
        }
        catch (WorkflowEngineException error) when (error.Kind == WorkflowErrorKind.ArtifactMismatch)
        { throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Invalid activity package upload"); }
        catch (Exception error) when (error is not (WorkflowEngineException or OperationCanceledException or OutOfMemoryException))
        { throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Invalid activity package upload"); }
        if (await artifacts.PutAsync(ArtifactKind.Package, new MemoryStream(bytes), token) != package.Digest)
            throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, "Artifact store returned a different package digest");
        token.ThrowIfCancellationRequested();
        await catalog.RegisterPackageAsync(package, artifacts);
        return package;
    }

    internal static string VersionKey(NuGetVersion version) => version.ToNormalizedString().ToLowerInvariant();

    internal async Task<ResolvedWorkflowPackages> ResolveAsync(IList<string> imports, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxPackageCandidates);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxResolvedPackages);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxPackageResolutionBytes);
        var result = new ResolvedWorkflowPackages();
        if (imports.Count == 0) return result;
        try
        {
            var framework = NuGetFramework.ParseFolder(options.TargetFramework);
            if (framework.IsUnsupported) throw new InvalidOperationException($"Unsupported target framework '{options.TargetFramework}'");
            var repositories = new List<SourceRepository>();
            PackageSourceMapping? mapping = null;
            if (options.NuGetConfigPath != null)
            {
                var path = Path.GetFullPath(options.NuGetConfigPath);
                if (!File.Exists(path)) throw new InvalidOperationException("The configured NuGet.Config file does not exist");
                try
                {
                    var settings = Settings.LoadSpecificSettings(Path.GetDirectoryName(path)!, Path.GetFileName(path));
                    repositories.AddRange(new SourceRepositoryProvider(new PackageSourceProvider(settings), Repository.Provider.GetCoreV3()).GetRepositories());
                    mapping = PackageSourceMapping.GetPackageSourceMapping(settings);
                }
                catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
                { throw new InvalidOperationException("The configured NuGet.Config could not be read"); }
            }
            using var credentials = await NuGetCredentialScope.CreateAsync(token);
            using var cache = new SourceCacheContext { DirectDownload = true, NoCache = true };
            var known = new Dictionary<string, IReadOnlyList<WorkflowPackage>>(StringComparer.OrdinalIgnoreCase);
            var archives = new Dictionary<PackageIdentity, (WorkflowPackage Package, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Entries, byte[] Bytes)>(PackageIdentityComparer.Default);
            var available = new Dictionary<PackageIdentity, SourcePackageDependencyInfo>(PackageIdentityComparer.Default);
            var ordinary = new Dictionary<PackageIdentity, PackageDependency[]>(PackageIdentityComparer.Default);
            var versions = new Dictionary<string, NuGetVersion[]>(StringComparer.OrdinalIgnoreCase);
            var roots = new List<PackageIdentity>();
            var queue = new Queue<PackageIdentity>();
            var scheduled = new HashSet<PackageIdentity>(PackageIdentityComparer.Default);
            long bytesRead = 0;
            using var graphStream = typeof(WorkflowPackageResolver).Assembly.GetManifestResourceStream("IxIFlow.Authoring.ActivityPackages.PortableRuntimeIdentifierGraph.json")!;
            var baseGraph = JsonRuntimeFormat.ReadRuntimeGraph(graphStream);
            var graph = baseGraph;

            void Schedule(PackageIdentity identity)
            {
                if (!scheduled.Add(identity)) return;
                if (scheduled.Count > options.MaxPackageCandidates) throw new InvalidOperationException("Package candidates exceed the configured limit");
                queue.Enqueue(identity);
            }

            IEnumerable<SourceRepository> Sources(string id) => repositories.Where(source => source.PackageSource.IsEnabled &&
                (mapping == null || !mapping.IsEnabled || mapping.GetConfiguredPackageSources(id).Contains(source.PackageSource.Name)));
            async Task<T> Feed<T>(SourceRepository source, string id, Func<Task<T>> action)
            {
                try { return await action(); }
                catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
                { throw new InvalidOperationException($"Package '{id}' could not be read from source '{source.PackageSource.Name}'"); }
            }
            void Budget(long count)
            {
                bytesRead = checked(bytesRead + count);
                if (bytesRead > options.MaxPackageResolutionBytes) throw new InvalidOperationException("Package resolution exceeds the total byte limit");
            }
            async Task<IReadOnlyList<WorkflowPackage>> Known(string id)
            {
                if (known.TryGetValue(id, out var rows)) return rows;
                rows = await catalog.GetPackagesAsync(id);
                if (rows.Count > options.MaxPackageCandidates) throw new InvalidOperationException("Package catalog candidates exceed the configured limit");
                known.Add(id, rows);
                return rows;
            }
            async Task<NuGetVersion[]> Versions(string id)
            {
                if (versions.TryGetValue(id, out var found)) return found;
                var all = new List<NuGetVersion>();
                foreach (var package in await Known(id))
                    if (await artifacts.ExistsAsync(package.Digest, token)) all.Add(NuGetVersion.Parse(package.Version));
                foreach (var source in Sources(id))
                    all.AddRange(await Feed(source, id, async () => await (await source.GetResourceAsync<FindPackageByIdResource>(token))
                        .GetAllVersionsAsync(id, cache, NullLogger.Instance, token)));
                if (all.Count > options.MaxPackageCandidates) throw new InvalidOperationException("Package version candidates exceed the configured limit");
                return versions[id] = all.Distinct().Order().ToArray();
            }
            async Task<WorkflowPackage?> Record(PackageIdentity identity) => (await Known(identity.Id)).SingleOrDefault(package => NuGetVersion.Parse(package.Version) == identity.Version);
            PackageDependency[] Dependencies(IEnumerable<PackageDependencyGroup> groups)
            {
                var array = groups.ToArray();
                var nearest = new FrameworkReducer().GetNearest(framework, array.Select(group => group.TargetFramework));
                return nearest == null ? [] : array.Single(group => group.TargetFramework.Equals(nearest)).Packages.ToArray();
            }
            bool Shared(PackageDependency dependency) => dependency.Id.Equals("NETStandard.Library", StringComparison.OrdinalIgnoreCase) || WorkflowSharedAssemblies.IsSharedName(dependency.Id);
            bool SharedSatisfied(PackageDependency dependency)
            {
                if (dependency.Id.Equals("NETStandard.Library", StringComparison.OrdinalIgnoreCase))
                    return dependency.VersionRange.Satisfies(new NuGetVersion(2, 1, 0)) || dependency.VersionRange.Satisfies(new NuGetVersion(2, 0, 3));
                var provided = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(dependency.Id));
                var info = provided.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                return dependency.VersionRange.Satisfies(NuGetVersion.TryParse(info, out var parsed) ? parsed : new NuGetVersion(provided.GetName().Version!));
            }
            async Task EnqueueDependencies(IEnumerable<PackageDependency> dependencies)
            {
                foreach (var dependency in dependencies.Where(dependency => !Shared(dependency)))
                    foreach (var version in (await Versions(dependency.Id)).Where(dependency.VersionRange.Satisfies)) Schedule(new(dependency.Id, version));
            }
            foreach (var import in imports)
            {
                var split = import.LastIndexOf('@');
                var id = (split < 0 ? import : import[..split]).ToLowerInvariant();
                PackageIdValidator.ValidatePackageId(id);
                var version = split < 0 ? (await Versions(id)).LastOrDefault(item => !item.IsPrerelease) : NuGetVersion.Parse(import[(split + 1)..]);
                if (version == null) throw new InvalidOperationException($"No package version is available for '{id}'");
                var identity = new PackageIdentity(id, version);
                if (roots.Any(root => root.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException($"Duplicate package import '{id}'");
                roots.Add(identity);
                Schedule(identity);
            }
            async Task ExpandMetadata()
            {
                while (queue.TryDequeue(out var identity))
                {
                    token.ThrowIfCancellationRequested();
                    if (ordinary.ContainsKey(identity)) continue;
                    if (ordinary.Count >= options.MaxPackageCandidates) throw new InvalidOperationException("Package candidates exceed the configured limit");
                    PackageDependency[]? dependencies = null;
                    var record = await Record(identity);
                    if (record != null && await artifacts.ExistsAsync(record.Digest, token))
                    {
                        // Uploaded content was verified on registration. Read only its bounded nuspec,
                        // without expanding or hashing every candidate's implementation assets.
                        await using var stored = await artifacts.OpenAsync(record.Digest, token);
                        if (stored != null)
                        {
                            using var reader = new PackageArchiveReader(stored, leaveStreamOpen: true);
                            using var nuspec = reader.GetNuspec();
                            using var metadata = new MemoryStream();
                            await WorkflowBundleArchiveCopyAsync(nuspec, metadata, options.PackageReadLimits.MaxManifestBytes, token);
                            Budget(metadata.Length);
                            dependencies = Dependencies(new NuspecReader(new MemoryStream(metadata.ToArray())).GetDependencyGroups());
                        }
                    }
                    if (dependencies == null)
                        foreach (var source in Sources(identity.Id))
                        {
                            var info = await Feed(source, identity.Id, async () => await (await source.GetResourceAsync<FindPackageByIdResource>(token))
                                .GetDependencyInfoAsync(identity.Id, identity.Version, cache, NullLogger.Instance, token));
                            if (info != null) { dependencies = Dependencies(info.DependencyGroups); break; }
                        }
                    if (dependencies == null) { ordinary[identity] = []; continue; } // Missing candidates cannot poison a different valid closure.
                    Budget(dependencies.Sum(dependency => (long)dependency.Id.Length * 2 + dependency.VersionRange.ToString().Length * 2));
                    ordinary[identity] = dependencies.Where(dependency => !Shared(dependency)).ToArray();
                    var incompatible = dependencies.FirstOrDefault(dependency => Shared(dependency) && !SharedSatisfied(dependency));
                    if (incompatible != null)
                    {
                        if (roots.Contains(identity, PackageIdentityComparer.Default))
                            throw new InvalidOperationException($"Package '{identity}' requires shared assembly package '{incompatible.Id}' {incompatible.VersionRange}; the profile cannot satisfy it");
                        continue;
                    }
                    available[identity] = new(identity.Id, identity.Version, ordinary[identity], true, repositories.FirstOrDefault()!);
                    await EnqueueDependencies(ordinary[identity]);
                }
            }
            async Task Acquire(PackageIdentity identity)
            {
                if (archives.ContainsKey(identity)) return;
                var record = await Record(identity);
                byte[]? bytes = null;
                if (record != null)
                {
                    await using var stored = await artifacts.OpenAsync(record.Digest, token);
                    if (stored != null)
                    {
                        using var buffer = new MemoryStream();
                        await WorkflowBundleArchiveCopyAsync(stored, buffer, Math.Min(options.PackageReadLimits.MaxArchiveBytes, options.MaxPackageResolutionBytes - bytesRead), token);
                        bytes = buffer.ToArray();
                    }
                }
                if (bytes == null)
                    foreach (var source in Sources(identity.Id))
                    {
                        using var buffer = new MemoryStream();
                        using var bounded = new BoundedPackageStream(buffer, Math.Min(options.PackageReadLimits.MaxArchiveBytes, options.MaxPackageResolutionBytes - bytesRead));
                        var found = await Feed(source, identity.Id, async () => await (await source.GetResourceAsync<FindPackageByIdResource>(token))
                            .CopyNupkgToStreamAsync(identity.Id, identity.Version, bounded, cache, NullLogger.Instance, token));
                        if (found) { bytes = buffer.ToArray(); break; }
                    }
                if (bytes == null) throw new InvalidOperationException($"Package '{identity}' has no available content in uploads or configured sources");
                Budget(bytes.Length);
                var digest = ArtifactDigest.Of(bytes);
                if (record != null && record.Digest != digest) throw new InvalidOperationException($"Package '{identity}' content differs from its immutable recorded digest");
                var remaining = options.MaxPackageResolutionBytes - bytesRead;
                if (remaining <= 0) throw new InvalidOperationException("Package resolution exceeds the total byte limit");
                var content = await WorkflowPackageArchive.ReadAsync(new MemoryStream(bytes), digest,
                    options.PackageReadLimits with { MaxExpandedBytes = Math.Min(options.PackageReadLimits.MaxExpandedBytes, remaining) }, token);
                Budget(content.Values.Sum(value => (long)value.Length));
                var hashes = content.ToDictionary(pair => pair.Key, pair => ArtifactDigest.Of(pair.Value.Span), StringComparer.Ordinal);
                if (record != null && (record.Entries.Count != hashes.Count || record.Entries.Any(pair => !hashes.TryGetValue(pair.Key, out var hash) || hash != pair.Value)))
                    throw new InvalidOperationException($"Package '{identity}' entries differ from the catalog");
                using var reader = new PackageArchiveReader(new MemoryStream(bytes));
                if (!PackageIdentityComparer.Default.Equals(identity, reader.GetIdentity())) throw new InvalidOperationException($"Package '{identity}' source returned a different identity");
                record = new(identity.Id.ToLowerInvariant(), VersionKey(identity.Version), digest, hashes);
                archives.Add(identity, (record, content, bytes));
            }
            PackageIdentity[] resolved;
            var rootVersions = roots.ToDictionary(root => root.Id, root => root.Version, StringComparer.OrdinalIgnoreCase);
            var rounds = 0;
            while (true)
            {
                if (++rounds > options.MaxPackageCandidates) throw new InvalidOperationException("Package runtime resolution did not converge within the candidate limit");
                await ExpandMetadata();
                var candidates = available.Values.Where(package => !rootVersions.TryGetValue(package.Id, out var pin) || package.Version == pin);
                var context = new PackageResolverContext(DependencyBehavior.Lowest, roots.Select(root => root.Id), roots.Select(root => root.Id), [], roots,
                    candidates, repositories.Select(source => source.PackageSource), NullLogger.Instance);
                try { resolved = new PackageResolver().Resolve(context, token).OrderBy(package => package.Id, StringComparer.OrdinalIgnoreCase).ToArray(); }
                catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
                { throw new InvalidOperationException($"NuGet could not resolve the package closure for {string.Join(", ", roots.Select(root => root.Id + "@" + VersionKey(root.Version)))}"); }
                if (resolved.Length > options.MaxResolvedPackages) throw new InvalidOperationException("Resolved package count exceeds the configured limit");
                foreach (var identity in resolved) await Acquire(identity);
                graph = baseGraph;
                foreach (var identity in resolved)
                    if (archives[identity].Entries.TryGetValue("runtime.json", out var runtime))
                        graph = RuntimeGraph.Merge(graph, JsonRuntimeFormat.ReadRuntimeGraph(new MemoryStream(runtime.ToArray())));
                var changed = false;
                foreach (var identity in resolved)
                {
                    var runtimeDependencies = graph.Runtimes.Keys.SelectMany(rid => graph.FindRuntimeDependencies(rid, identity.Id))
                        .Select(dependency => new PackageDependency(dependency.Id, dependency.VersionRange)).Distinct().ToArray();
                    var incompatible = runtimeDependencies.FirstOrDefault(dependency => Shared(dependency) && !SharedSatisfied(dependency));
                    if (incompatible != null)
                    {
                        if (roots.Contains(identity, PackageIdentityComparer.Default))
                            throw new InvalidOperationException($"Package '{identity}' has an incompatible shared assembly runtime requirement '{incompatible.Id}'");
                        available.Remove(identity);
                        changed = true;
                        continue;
                    }
                    var union = ordinary[identity].Concat(runtimeDependencies.Where(dependency => !Shared(dependency))).Distinct().ToArray();
                    if (!union.SequenceEqual(available[identity].Dependencies))
                    {
                        available[identity] = new(identity.Id, identity.Version, union, true, repositories.FirstOrDefault()!);
                        await EnqueueDependencies(union);
                        changed = true;
                    }
                }
                if (!changed) break;
            }
            var byId = resolved.ToDictionary(identity => identity.Id, StringComparer.OrdinalIgnoreCase);
            HashSet<string> Reachable(string? rid)
            {
                var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var pending = new Queue<string>(roots.Select(root => root.Id));
                while (pending.TryDequeue(out var id))
                {
                    if (!reached.Add(id)) continue;
                    var identity = byId[id];
                    foreach (var dependency in ordinary[identity]) pending.Enqueue(dependency.Id);
                    if (rid != null)
                        foreach (var dependency in graph.FindRuntimeDependencies(rid, id).Where(dependency => !WorkflowSharedAssemblies.IsSharedName(dependency.Id) && !dependency.Id.Equals("NETStandard.Library", StringComparison.OrdinalIgnoreCase)))
                            pending.Enqueue(dependency.Id);
                }
                return reached;
            }
            var neutralClosure = Reachable(null);
            var ridClosures = graph.Runtimes.Keys.ToDictionary(rid => rid, rid => Reachable(rid));
            foreach (var identity in resolved)
            {
                var (package, entries, bytes) = archives[identity];
                var pin = SelectAssets(package, entries, framework, graph) with
                { ApplicableRuntimeIdentifiers = neutralClosure.Contains(identity.Id) ? null : ridClosures.Where(pair => pair.Value.Contains(identity.Id)).Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray() };
                VerifySharedReferences(pin, entries);
                bool Private(string path) => !WorkflowSharedAssemblies.IsShared(ResolvedWorkflowPackages.AssemblyIdentity(entries[path]));
                pin = pin with { ReferenceAssemblies = pin.ReferenceAssemblies.Where(Private).ToArray(),
                    RuntimeAssets = pin.RuntimeAssets.ToDictionary(pair => pair.Key, pair => pair.Value with { ManagedAssemblies = pair.Value.ManagedAssemblies.Where(Private).ToArray() }, StringComparer.Ordinal) };
                if (pin.ReferenceAssemblies.Count != 0 && pin.RuntimeAssets.Count == 0)
                    throw new InvalidOperationException($"Package '{identity}' has reference assets without a runtime implementation");
                if (pin.ReferenceAssemblies.Count != 0)
                    foreach (var selection in pin.RuntimeAssets.Where(pair => pair.Key != "" || !pin.RequiresRuntimeIdentifier).Select(pair => pair.Value))
                        foreach (var reference in pin.ReferenceAssemblies)
                            if (!selection.ManagedAssemblies.Any(path => ResolvedWorkflowPackages.AssemblyIdentity(entries[path]).Name == ResolvedWorkflowPackages.AssemblyIdentity(entries[reference]).Name))
                                throw new InvalidOperationException($"Package '{identity}' has no runtime implementation for reference '{reference}'");
                await UploadAsync(catalog, artifacts, new MemoryStream(bytes), options.PackageReadLimits, token);
                result.Add(package, pin, entries);
            }
            foreach (var rid in graph.Runtimes.Keys.Append(""))
            {
                var names = new HashSet<string>(rid.StartsWith("win", StringComparison.Ordinal) ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                foreach (var pin in result.Pins.Where(pin => rid == "" ? pin.ApplicableRuntimeIdentifiers == null : pin.AppliesTo(rid)))
                    foreach (var path in (pin.RuntimeAssets.GetValueOrDefault(rid) ?? (!pin.RequiresRuntimeIdentifier ? pin.RuntimeAssets.GetValueOrDefault("") : null))?.NativeLibraries ?? [])
                        if (!names.Add(Path.GetFileName(path))) throw new InvalidOperationException($"Duplicate native package filename '{Path.GetFileName(path)}' for runtime '{rid}'");
            }
            imports.Clear();
            foreach (var root in roots) imports.Add(root.Id.ToLowerInvariant() + "@" + VersionKey(root.Version));
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static WorkflowPackagePin SelectAssets(WorkflowPackage package, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> entries,
        NuGetFramework framework, RuntimeGraph graph)
    {
        var conventions = new ManagedCodeConventions(graph);
        var items = new ContentItemCollection();
        items.Load(entries.Keys);
        string[] Select(string? rid, params PatternSet[] patterns) =>
            items.FindBestItemGroup(conventions.Criteria.ForFrameworkAndRuntime(framework, rid), patterns)?.Items
                .Select(item => item.Path).Where(path => !path.EndsWith("/_._", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray() ?? [];
        var references = Select(null, conventions.Patterns.CompileRefAssemblies, conventions.Patterns.CompileLibAssemblies);
        var native = entries.Keys.Any(path => path.StartsWith("runtimes/", StringComparison.Ordinal) && path.Contains("/native/", StringComparison.Ordinal) && !path.EndsWith("/_._", StringComparison.Ordinal));
        var neutral = Select(null, conventions.Patterns.RuntimeAssemblies);
        var ridManaged = neutral.Length == 0 && entries.Keys.Any(path => path.StartsWith("runtimes/", StringComparison.Ordinal) && path.Contains("/lib/", StringComparison.Ordinal) && path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        var requiresRid = native || ridManaged;
        var assets = new Dictionary<string, WorkflowPackageAssets>(StringComparer.Ordinal);
        assets.Add("", new(neutral, []));
        foreach (var rid in graph.Runtimes.Keys.Concat(entries.Keys.Where(path => path.StartsWith("runtimes/", StringComparison.Ordinal))
                     .Select(path => path.Split('/')[1])).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var libraries = Select(rid, conventions.Patterns.NativeLibraries);
            if (native && libraries.Length == 0) continue;
            var managed = Select(rid, conventions.Patterns.RuntimeAssemblies);
            if (ridManaged && managed.Length == 0) continue;
            if (requiresRid || !managed.SequenceEqual(neutral) || libraries.Length != 0) assets[rid] = new(managed, libraries);
        }
        if (references.Length == 0 && entries.Keys.Any(path => !path.EndsWith("/_._", StringComparison.Ordinal) && (path.StartsWith("lib/", StringComparison.Ordinal) || path.StartsWith("ref/", StringComparison.Ordinal))))
            throw new InvalidOperationException($"Package '{package.Id}@{package.Version}' has no compatible reference assets for {framework}");
        return new(package.Id, package.Version, package.Digest, package.Entries, references, assets, requiresRid);
    }

    private static void VerifySharedReferences(WorkflowPackagePin pin, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> entries)
    {
        foreach (var path in pin.ReferenceAssemblies.Concat(pin.RuntimeAssets.Values.SelectMany(assets => assets.ManagedAssemblies)).Distinct())
        {
            using var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(entries[path].ToArray()));
            var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            var own = ResolvedWorkflowPackages.AssemblyIdentity(entries[path]);
            if (WorkflowSharedAssemblies.Identity(own.Name!) is { } provided && (provided.Version != own.Version || !WorkflowSharedAssemblies.IsShared(own)))
                throw new InvalidOperationException($"Package '{pin.Id}' supplies shared assembly '{own.Name}' {own.Version}; profile provides {provided.Version}");
            foreach (var handle in metadata.AssemblyReferences)
            {
                var reference = metadata.GetAssemblyReference(handle);
                var name = metadata.GetString(reference.Name);
                var requested = new AssemblyName { Name = name, Version = reference.Version,
                    CultureName = reference.Culture.IsNil ? null : metadata.GetString(reference.Culture) };
                if (!reference.PublicKeyOrToken.IsNil)
                {
                    if ((reference.Flags & AssemblyFlags.PublicKey) != 0) requested.SetPublicKey(metadata.GetBlobBytes(reference.PublicKeyOrToken));
                    else requested.SetPublicKeyToken(metadata.GetBlobBytes(reference.PublicKeyOrToken));
                }
                if (WorkflowSharedAssemblies.Identity(name) is { } shared && (reference.Version > shared.Version || !WorkflowSharedAssemblies.IsShared(requested)))
                    throw new InvalidOperationException($"Package '{pin.Id}@{pin.Version}' requires shared assembly '{name}' {reference.Version}; profile provides {shared.Version}");
            }
        }
    }

    private static async Task WorkflowBundleArchiveCopyAsync(Stream input, Stream output, long maximum, CancellationToken token)
    {
        var buffer = new byte[81920];
        int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            if (output.Length + count > maximum) throw new WorkflowEngineException(WorkflowErrorKind.ResourceExhausted, "Package exceeds size limit");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
    }

    private sealed class BoundedPackageStream(Stream inner, long maximum) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override void Write(byte[] buffer, int offset, int count)
        { if (inner.Length + count > maximum) throw new WorkflowEngineException(WorkflowErrorKind.ResourceExhausted, "Package exceeds size limit"); inner.Write(buffer, offset, count); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        { if (inner.Length + count > maximum) throw new WorkflowEngineException(WorkflowErrorKind.ResourceExhausted, "Package exceeds size limit"); return inner.WriteAsync(buffer, offset, count, token); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
