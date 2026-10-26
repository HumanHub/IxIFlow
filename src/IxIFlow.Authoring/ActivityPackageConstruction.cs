using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using IxIFlow.Core;
using IxIFlow.Generators;
using NuGet.Packaging;
using NuGet.Versioning;

namespace IxIFlow.Authoring;

internal sealed record ActivityPackageMetadata(WorkflowDraftId Draft, WorkflowCompilerSettings Compiler, string EngineVersion,
    string TargetFramework, IReadOnlyDictionary<string, string> SharedAssemblies, IReadOnlyList<WorkflowPackagePin> ActivityPackages);

internal static class ActivityPackageConstruction
{
    private static readonly Lazy<IReadOnlySet<string>> SdkReferences = new(() =>
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<Assembly>();
        pending.Enqueue(typeof(WorkflowDefinition).Assembly);
        while (pending.TryDequeue(out var assembly))
        {
            if (!names.Add(assembly.GetName().Name!)) continue;
            foreach (var reference in assembly.GetReferencedAssemblies())
                if (!names.Contains(reference.Name!))
                {
                    try { pending.Enqueue(System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyName(reference)); }
                    catch (Exception error) when (error is FileNotFoundException or FileLoadException or BadImageFormatException) { }
                }
        }
        return names;
    });

    internal static async Task<PackageUploadCandidate> CreateAsync(WorkflowDraft draft, ActivityPackageIdentity identity,
        WorkflowCompilationResult compilation, ResolvedWorkflowPackages packages, IReadOnlyDictionary<string, string> shared,
        WorkflowPublisherOptions options, CancellationToken token)
    {
        var dependencies = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pin in packages.Pins.Where(pin => pin.ApplicableRuntimeIdentifiers == null))
            dependencies.Add(pin.Id, pin.Version);
        var contracts = options.SharedProfile.Contracts.Select(assembly => assembly.GetName().Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var frameworks = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var fullName in shared.Keys.Append(typeof(IAsyncActivity).Assembly.FullName!).Distinct(StringComparer.Ordinal))
        {
            var name = new AssemblyName(fullName);
            if (contracts.Contains(name.Name!)) continue;
            var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyName(name);
            if (WorkflowSharedAssemblies.IsFrameworkAssembly(name))
            {
                // A publishing host may also reference AspNetCore.App. It can supply
                // SDK DLLs such as DependencyInjection, but a plain NuGet consumer
                // still needs those SDK package dependencies. CoreCLR runtime DLLs
                // come from the target framework instead.
                if (Path.GetDirectoryName(assembly.Location) == Path.GetDirectoryName(typeof(object).Assembly.Location)) continue;
                if (!SdkReferences.Value.Contains(name.Name!))
                {
                    var framework = Directory.GetParent(Path.GetDirectoryName(assembly.Location)!);
                    if (framework?.Parent?.Name == "shared")
                    {
                        frameworks.Add(framework.Name);
                        continue;
                    }
                }
            }
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
                ?? name.Version!.ToString();
            dependencies[name.Name!] = NuGetVersion.Parse(version).ToNormalizedString();
        }
        XNamespace ns = "http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd";
        var nuspec = new XDocument(new XElement(ns + "package", new XElement(ns + "metadata",
            new XElement(ns + "id", identity.PackageId), new XElement(ns + "version", identity.PackageVersion),
            new XElement(ns + "authors", "IxIFlow"), new XElement(ns + "description", "Published activity " + identity.PackageId),
            new XElement(ns + "dependencies", new XElement(ns + "group", new XAttribute("targetFramework", options.TargetFramework),
                dependencies.Select(pair => new XElement(ns + "dependency", new XAttribute("id", pair.Key), new XAttribute("version", "[" + pair.Value + "]"))))),
            frameworks.Count == 0 ? null : new XElement(ns + "frameworkReferences", new XElement(ns + "group", new XAttribute("targetFramework", options.TargetFramework),
                frameworks.Select(name => new XElement(ns + "frameworkReference", new XAttribute("name", name))))))));
        var metadata = new ActivityPackageMetadata(draft.Id, WorkflowCompilation.Settings, typeof(WorkflowDefinition).Assembly.GetName().Version!.ToString(),
            options.TargetFramework, shared.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(), packages.Pins.OrderBy(pin => pin.Id, StringComparer.Ordinal).ToArray());
        var entries = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [identity.PackageId + ".nuspec"] = Encoding.UTF8.GetBytes(nuspec.ToString(SaveOptions.DisableFormatting)),
            ["lib/net10.0/" + identity.PackageId + ".dll"] = compilation.Assembly,
            ["lib/net10.0/" + identity.PackageId + ".pdb"] = compilation.Pdb,
            [ActivityCompilation.SourceResourceName] = Encoding.UTF8.GetBytes(draft.Source),
            ["ixiflow/activity.json"] = JsonSerializer.SerializeToUtf8Bytes(metadata)
        };
        var runtimes = new SortedDictionary<string, SortedDictionary<string, SortedDictionary<string, string>>>(StringComparer.Ordinal);
        foreach (var pin in packages.Pins.Where(pin => pin.ApplicableRuntimeIdentifiers != null))
            foreach (var rid in pin.ApplicableRuntimeIdentifiers!)
            {
                if (!runtimes.TryGetValue(rid, out var roots)) runtimes.Add(rid, roots = new(StringComparer.Ordinal));
                if (!roots.TryGetValue(identity.PackageId, out var selected)) roots.Add(identity.PackageId, selected = new(StringComparer.Ordinal));
                selected.Add(pin.Id, "[" + pin.Version + "]");
            }
        if (runtimes.Count != 0) entries.Add("runtime.json", JsonSerializer.SerializeToUtf8Bytes(new { runtimes }));
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, bytes) in entries)
            {
                token.ThrowIfCancellationRequested();
                var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
                entry.LastWriteTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                entry.ExternalAttributes = 0;
                using var content = entry.Open(); content.Write(bytes);
            }
        var archive = output.ToArray();
        using (var reader = new PackageArchiveReader(new MemoryStream(archive, writable: false)))
        {
            var actual = reader.GetIdentity();
            if (actual.Id != identity.PackageId || actual.Version.ToNormalizedString() != identity.PackageVersion ||
                !reader.GetFiles().Order(StringComparer.Ordinal).SequenceEqual(entries.Keys))
                throw new WorkflowPublishException("PUB023", "Constructed activity package identity or content differs", new(1, 1));
            foreach (var (path, expected) in entries)
            {
                using var content = reader.GetStream(path);
                using var bytes = new MemoryStream(); await content.CopyToAsync(bytes, token);
                if (!bytes.ToArray().AsSpan().SequenceEqual(expected))
                    throw new WorkflowPublishException("PUB023", "Constructed activity package content differs", new(1, 1));
            }
        }
        return await WorkflowPackageResolver.ReadAsync(new MemoryStream(archive, writable: false), options.PackageReadLimits, token);
    }

    internal static void ValidateProfile(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> entries)
    {
        var metadata = ReadMetadata(entries);
        if (metadata == null) return;
        if (metadata.EngineVersion != typeof(WorkflowDefinition).Assembly.GetName().Version!.ToString())
            throw new WorkflowPublishException("PUB008", "The activity package requires a different engine profile", new(1, 1));
        foreach (var (fullName, version) in metadata.SharedAssemblies)
        {
            var name = new AssemblyName(fullName);
            if (!WorkflowSharedAssemblies.IsShared(name) || WorkflowSharedAssemblies.Identity(name.Name!)?.Version?.ToString() != version || name.Version?.ToString() != version)
                throw new WorkflowPublishException("PUB008", "The activity package requires a different shared assembly profile", new(1, 1));
        }
    }

    internal static void ValidateClosure(ResolvedWorkflowPackages packages)
    {
        foreach (var entries in packages.Contents.Values)
        {
            var metadata = ReadMetadata(entries);
            if (metadata == null) continue;
            foreach (var required in metadata.ActivityPackages)
            {
                var actual = packages.Pins.SingleOrDefault(pin => pin.Id == required.Id);
                if (actual == null || actual.Version != required.Version || actual.Digest != required.Digest)
                    throw new WorkflowPublishException("PUB020", "An activity package dependency differs from its recorded digest pin", new(1, 1));
            }
        }
    }

    private static ActivityPackageMetadata? ReadMetadata(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> entries)
    {
        if (!entries.TryGetValue("ixiflow/activity.json", out var bytes)) return null;
        try
        {
            var metadata = JsonSerializer.Deserialize<ActivityPackageMetadata>(bytes.Span);
            if (metadata?.SharedAssemblies == null || metadata.ActivityPackages == null || metadata.ActivityPackages.Any(pin => pin == null))
                throw new JsonException();
            return metadata;
        }
        catch (JsonException)
        { throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Invalid activity package metadata"); }
    }
}
