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
        var root = Path.Combine(_directory, package.Id);
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
            ?? pin.RuntimeAssets.GetValueOrDefault("") ?? pin.RuntimeAssets.Values.FirstOrDefault();
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
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}

internal sealed class WorkflowPackageResolver(IWorkflowCatalogStore catalog, IArtifactStore artifacts, WorkflowPublisherOptions options)
{
    internal static async Task<WorkflowPackage> UploadAsync(IWorkflowCatalogStore catalog, IArtifactStore artifacts,
        Stream content, WorkflowBundleReadLimits limits, CancellationToken token)
    {
        // Buffer with the same bounds as hosts before handing a seekable archive to NuGet.
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int count;
        while ((count = await content.ReadAsync(chunk, token)) != 0)
        {
            if (buffer.Length + count > limits.MaxArchiveBytes) throw new InvalidDataException("Package archive exceeds the configured size limit");
            await buffer.WriteAsync(chunk.AsMemory(0, count), token);
        }
        var digest = ArtifactDigest.Of(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
        buffer.Position = 0;
        var entries = await WorkflowPackageArchive.ReadAsync(buffer, digest, limits, token);
        buffer.Position = 0;
        using var reader = new PackageArchiveReader(buffer, leaveStreamOpen: true);
        var identity = reader.GetIdentity();
        var package = new WorkflowPackage(identity.Id.ToLowerInvariant(), identity.Version.ToNormalizedString(), digest,
            entries.ToDictionary(pair => pair.Key, pair => ArtifactDigest.Of(pair.Value.Span), StringComparer.Ordinal));
        buffer.Position = 0;
        if (await artifacts.PutAsync(ArtifactKind.Package, buffer, token) != digest)
            throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, "Artifact store returned a different package digest");
        token.ThrowIfCancellationRequested();
        await catalog.RegisterPackageAsync(package, artifacts);
        return package;
    }

    internal async Task<ResolvedWorkflowPackages> ResolveAsync(IList<string> imports, CancellationToken token)
    {
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
                var settings = Settings.LoadSpecificSettings(Path.GetDirectoryName(path)!, Path.GetFileName(path));
                repositories.AddRange(new SourceRepositoryProvider(new PackageSourceProvider(settings), Repository.Provider.GetCoreV3()).GetRepositories());
                mapping = PackageSourceMapping.GetPackageSourceMapping(settings);
            }
            using var cache = new SourceCacheContext();
            var known = (await catalog.GetPackagesAsync()).ToList();
            var archives = new Dictionary<PackageIdentity, (WorkflowPackage Package, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Entries, byte[] Bytes)>(PackageIdentityComparer.Default);
            var available = new Dictionary<PackageIdentity, SourcePackageDependencyInfo>(PackageIdentityComparer.Default);
            var sharedRequirements = new Dictionary<PackageIdentity, List<PackageDependency>>(PackageIdentityComparer.Default);
            var versions = new Dictionary<string, NuGetVersion[]>(StringComparer.OrdinalIgnoreCase);
            var roots = new List<PackageIdentity>();
            var queue = new Queue<PackageIdentity>();

            IEnumerable<SourceRepository> Sources(string id) => repositories.Where(source => !source.PackageSource.IsEnabled ? false :
                mapping == null || !mapping.IsEnabled || mapping.GetConfiguredPackageSources(id).Contains(source.PackageSource.Name));

            async Task<NuGetVersion[]> Versions(string id)
            {
                if (versions.TryGetValue(id, out var found)) return found;
                var all = known.Where(package => package.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).Select(package => NuGetVersion.Parse(package.Version)).ToList();
                foreach (var source in Sources(id))
                {
                    var resource = await source.GetResourceAsync<FindPackageByIdResource>(token);
                    all.AddRange(await resource.GetAllVersionsAsync(id, cache, NullLogger.Instance, token));
                }
                return versions[id] = all.Distinct().Order().ToArray();
            }

            foreach (var import in imports)
            {
                var split = import.LastIndexOf('@');
                var id = (split < 0 ? import : import[..split]).ToLowerInvariant();
                var version = split < 0 ? (await Versions(id)).LastOrDefault(item => !item.IsPrerelease)
                    : NuGetVersion.Parse(import[(split + 1)..]);
                if (version == null) throw new InvalidOperationException($"No package version is available for '{id}'");
                var identity = new PackageIdentity(id, version);
                if (roots.Any(root => root.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"Duplicate package import '{id}'");
                roots.Add(identity);
                queue.Enqueue(identity);
            }

            while (queue.TryDequeue(out var identity))
            {
                token.ThrowIfCancellationRequested();
                if (available.ContainsKey(identity)) continue;
                var record = known.SingleOrDefault(package => package.Id.Equals(identity.Id, StringComparison.OrdinalIgnoreCase) && NuGetVersion.Parse(package.Version) == identity.Version);
                byte[] bytes;
                if (record != null)
                {
                    await using var stored = await artifacts.OpenAsync(record.Digest, token)
                        ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable, $"Package artifact '{identity}' is unavailable");
                    using var buffer = new MemoryStream();
                    await WorkflowBundleArchiveCopyAsync(stored, buffer, options.PackageReadLimits.MaxArchiveBytes, token);
                    bytes = buffer.ToArray();
                    if (ArtifactDigest.Of(bytes) != record.Digest) throw new InvalidDataException("Package changed while reading");
                }
                else
                {
                    using var buffer = new MemoryStream();
                    var found = false;
                    foreach (var source in Sources(identity.Id))
                    {
                        buffer.SetLength(0);
                        var resource = await source.GetResourceAsync<FindPackageByIdResource>(token);
                        using var bounded = new BoundedPackageStream(buffer, options.PackageReadLimits.MaxArchiveBytes);
                        if (await resource.CopyNupkgToStreamAsync(identity.Id, identity.Version, bounded, cache, NullLogger.Instance, token))
                        { found = true; break; }
                    }
                    if (!found) throw new InvalidOperationException($"Package '{identity}' was not found in uploads or configured feeds");
                    bytes = buffer.ToArray();
                    record = new(identity.Id.ToLowerInvariant(), identity.Version.ToNormalizedString(), ArtifactDigest.Of(bytes), new Dictionary<string, ArtifactDigest>());
                }
                var content = await WorkflowPackageArchive.ReadAsync(new MemoryStream(bytes), record.Digest, options.PackageReadLimits, token);
                var hashes = content.ToDictionary(pair => pair.Key, pair => ArtifactDigest.Of(pair.Value.Span), StringComparer.Ordinal);
                if (record.Entries.Count != 0 && (record.Entries.Count != hashes.Count || record.Entries.Any(pair => !hashes.TryGetValue(pair.Key, out var hash) || hash != pair.Value)))
                    throw new InvalidDataException($"Package '{identity}' entries differ from the catalog");
                record = record with { Entries = hashes };
                using var reader = new PackageArchiveReader(new MemoryStream(bytes));
                if (!PackageIdentityComparer.Default.Equals(identity, reader.GetIdentity())) throw new InvalidDataException("Feed package identity differs from the requested identity");
                var groups = reader.GetPackageDependencies().ToArray();
                var nearest = new FrameworkReducer().GetNearest(framework, groups.Select(group => group.TargetFramework));
                var dependencies = (nearest == null ? [] : groups.Single(group => group.TargetFramework.Equals(nearest)).Packages).ToList();
                if (content.TryGetValue("runtime.json", out var runtimeJson))
                {
                    var packageGraph = JsonRuntimeFormat.ReadRuntimeGraph(new MemoryStream(runtimeJson.ToArray()));
                    dependencies.AddRange(packageGraph.Runtimes.Values.SelectMany(runtime => runtime.RuntimeDependencySets.Values)
                        .Where(set => set.Id.Equals(identity.Id, StringComparison.OrdinalIgnoreCase))
                        .SelectMany(set => set.Dependencies.Values).Select(dependency => new PackageDependency(dependency.Id, dependency.VersionRange)));
                }
                var privateDependencies = new List<PackageDependency>();
                var sharedDependencies = new List<PackageDependency>();
                foreach (var dependency in dependencies)
                {
                    if (WorkflowSharedAssemblies.IsSharedName(dependency.Id))
                    {
                        sharedDependencies.Add(dependency);
                        continue;
                    }
                    privateDependencies.Add(dependency);
                    var candidates = (await Versions(dependency.Id)).Where(dependency.VersionRange.Satisfies).ToArray();
                    foreach (var version in candidates) queue.Enqueue(new(dependency.Id, version));
                }
                sharedRequirements.Add(identity, sharedDependencies);
                available.Add(identity, new(identity.Id, identity.Version, privateDependencies, true, repositories.FirstOrDefault()!));
                archives.Add(identity, (record, content, bytes));
            }

            // NuGet's resolver chooses one coherent transitive closure. Root pins are immutable constraints.
            var rootVersions = roots.ToDictionary(root => root.Id, root => root.Version, StringComparer.OrdinalIgnoreCase);
            var candidatesForResolution = available.Values.Where(package => !rootVersions.TryGetValue(package.Id, out var pin) || package.Version == pin);
            var context = new PackageResolverContext(DependencyBehavior.Lowest, roots.Select(root => root.Id), roots.Select(root => root.Id),
                [], roots, candidatesForResolution, repositories.Select(source => source.PackageSource), NullLogger.Instance);
            var resolved = new PackageResolver().Resolve(context, token).OrderBy(package => package.Id, StringComparer.OrdinalIgnoreCase).ToArray();
            using var graphStream = typeof(WorkflowPackageResolver).Assembly.GetManifestResourceStream("IxIFlow.Authoring.ActivityPackages.PortableRuntimeIdentifierGraph.json")!;
            var graph = JsonRuntimeFormat.ReadRuntimeGraph(graphStream);
            foreach (var identity in resolved)
                if (archives[identity].Entries.TryGetValue("runtime.json", out var runtime))
                    graph = RuntimeGraph.Merge(graph, JsonRuntimeFormat.ReadRuntimeGraph(new MemoryStream(runtime.ToArray())));
            foreach (var identity in resolved)
            {
                foreach (var dependency in sharedRequirements[identity])
                {
                    var provided = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(dependency.Id));
                    var informational = provided.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                    var packageVersion = NuGetVersion.TryParse(informational, out var parsed) ? parsed : new NuGetVersion(provided.GetName().Version!);
                    if (!dependency.VersionRange.Satisfies(packageVersion))
                        throw new InvalidOperationException($"Package '{identity}' requires shared assembly package '{dependency.Id}' {dependency.VersionRange}; profile provides {packageVersion}");
                }
                var (package, entries, bytes) = archives[identity];
                var pin = SelectAssets(package, entries, framework, graph);
                VerifySharedReferences(pin, entries);
                bool Private(string path) => !WorkflowSharedAssemblies.IsShared(ResolvedWorkflowPackages.AssemblyIdentity(entries[path]));
                pin = pin with { ReferenceAssemblies = pin.ReferenceAssemblies.Where(Private).ToArray(),
                    RuntimeAssets = pin.RuntimeAssets.ToDictionary(pair => pair.Key, pair => pair.Value with { ManagedAssemblies = pair.Value.ManagedAssemblies.Where(Private).ToArray() }, StringComparer.Ordinal) };
                await UploadAsync(catalog, artifacts, new MemoryStream(bytes), options.PackageReadLimits, token);
                result.Add(package, pin, entries);
            }
            imports.Clear();
            foreach (var root in roots) imports.Add(root.Id.ToLowerInvariant() + "@" + root.Version.ToNormalizedString());
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
        if (!requiresRid) assets.Add("", new(neutral, []));
        foreach (var rid in graph.Runtimes.Keys.Concat(entries.Keys.Where(path => path.StartsWith("runtimes/", StringComparison.Ordinal))
                     .Select(path => path.Split('/')[1])).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var libraries = Select(rid, conventions.Patterns.NativeLibraries);
            if (native && libraries.Length == 0) continue;
            var managed = Select(rid, conventions.Patterns.RuntimeAssemblies);
            if (ridManaged && managed.Length == 0) continue;
            assets[rid] = new(managed, libraries);
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
                if (WorkflowSharedAssemblies.Identity(name) is { } shared && (shared.Version != reference.Version || !WorkflowSharedAssemblies.IsShared(requested)))
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
            if (output.Length + count > maximum) throw new InvalidDataException("Package exceeds size limit");
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
        { if (inner.Length + count > maximum) throw new InvalidDataException("Package exceeds size limit"); inner.Write(buffer, offset, count); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        { if (inner.Length + count > maximum) throw new InvalidDataException("Package exceeds size limit"); return inner.WriteAsync(buffer, offset, count, token); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
