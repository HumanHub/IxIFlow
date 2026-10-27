using System.Reflection;
using System.Text;
using IxIFlow.Core;
using IxIFlow.Dsl.Documents;

namespace IxIFlow.Authoring;

/// <summary>Owns verified callee references for one publish; nothing is read from a warm load context.</summary>
internal sealed class WorkflowInlinePayload : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "IxIFlow.InlinePublish", Guid.NewGuid().ToString("N"));
    internal Dictionary<string, byte[]> Entries { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, string> Shared { get; } = new(StringComparer.Ordinal);
    internal List<string> References { get; } = [];
    private readonly Dictionary<(string, int), WorkflowInlineWorkflow> _pins = [];
    private readonly Dictionary<string, WorkflowCallee> _origins = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(WorkflowInlineWorkflow Root, IReadOnlyList<WorkflowInlineWorkflow> Nested, WorkflowCallee Callee)> _bodies = [];

    internal void Prepare(IReadOnlyList<WorkflowCallee> callees, ResolvedWorkflowPackages packages)
    {
        foreach (var callee in callees.Where(callee => callee.Inline))
        {
            var bundle = callee.Bundle!;
            var manifest = bundle.Manifest;
            CheckClosure(callee, packages);
            var identity = ResolvedWorkflowPackages.AssemblyIdentity(bundle.Entries[WorkflowBundleArchive.AssemblyPath]);
            using var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(bundle.Entries[WorkflowBundleArchive.AssemblyPath].ToArray()));
            var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            if (identity.Name != WorkflowBundleIdentity.AssemblyName(manifest.Name, manifest.Version) ||
                !metadata.TypeDefinitions.Any(handle =>
                {
                    var type = metadata.GetTypeDefinition(handle);
                    return metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name) == manifest.EntryType &&
                        (metadata.GetString(type.Name) == WorkflowBundleIdentity.ClassName(manifest.Name, manifest.Version) ||
                         metadata.GetString(type.Name).EndsWith("_" + WorkflowBundleIdentity.ClassName(manifest.Name, manifest.Version), StringComparison.Ordinal));
                }))
                throw new WorkflowPublishException("PUB009", $"Callee '{manifest.Name}@{manifest.Version}' has an incompatible executable identity; republish the callee", callee.Location);
            var prefix = WorkflowBundleIdentity.InlineSourceDirectory(manifest.Name, manifest.Version);
            string yaml;
            try
            {
                yaml = new UTF8Encoding(false, true).GetString(bundle.Entries[WorkflowBundleArchive.YamlPath].Span);
                var document = WorkflowYamlSerializer.Read(yaml);
                if (WorkflowYamlSerializer.Write(document) != yaml || document.Workflow.Name != manifest.Name || document.Workflow.Version != manifest.Version)
                    throw new WorkflowPublishException("PUB013", $"Callee '{manifest.Name}@{manifest.Version}' has noncanonical inline source", callee.Location);
            }
            catch (Exception error) when (error is YamlDotNet.Core.YamlException or DecoderFallbackException)
            { throw new WorkflowPublishException("PUB013", $"Callee '{manifest.Name}@{manifest.Version}' has invalid inline source", callee.Location, error); }
            var root = new WorkflowInlineWorkflow(manifest.Name, manifest.Version, callee.Version.ArtifactDigest, manifest.StructureHash, manifest.ContractHash,
                manifest.EntryType, identity.Name!, []);
            AddPin(root, callee);
            _bodies.Add((root, manifest.InlineWorkflows, callee));
            foreach (var pin in manifest.InlineWorkflows) AddPin(pin, callee);
            foreach (var pair in manifest.SharedAssemblies) Shared.TryAdd(pair.Key, pair.Value);
            AddAssembly(bundle.Entries[WorkflowBundleArchive.AssemblyPath], callee);
            Add("private/" + identity.Name + ".pdb", bundle.Entries[WorkflowBundleArchive.PdbPath], callee);
            Add(prefix + WorkflowBundleArchive.YamlPath, bundle.Entries[WorkflowBundleArchive.YamlPath], callee);
            Add(prefix + WorkflowBundleArchive.SourcePath, bundle.Entries[WorkflowBundleArchive.SourcePath], callee);
            foreach (var (path, bytes) in bundle.Entries.Where(pair => pair.Key.StartsWith("private/", StringComparison.Ordinal) || pair.Key.StartsWith("inline/", StringComparison.Ordinal)))
            {
                if (path.StartsWith("private/", StringComparison.Ordinal) && path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) AddAssembly(bytes, callee);
                else Add(path, bytes, callee);
            }
        }
        var selected = WorkflowCompilation.ReferencePaths().Concat(packages.References)
            .GroupBy(path => AssemblyName.GetAssemblyName(path).Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        Directory.CreateDirectory(_directory);
        foreach (var (path, bytes) in Entries.Where(pair => pair.Key.StartsWith("private/", StringComparison.Ordinal) && pair.Key.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            var identity = ResolvedWorkflowPackages.AssemblyIdentity(bytes);
            if (WorkflowSharedAssemblies.IsShared(identity))
                throw new WorkflowPublishException("PUB010", $"Inline private assembly '{identity.Name}' conflicts with the shared publishing profile", null);
            if (packages.RuntimeNames.Contains(identity.Name!))
            {
                if (packages.AllRuntimeAssemblies.Where(item => ResolvedWorkflowPackages.AssemblyIdentity(item).Name!.Equals(identity.Name, StringComparison.OrdinalIgnoreCase))
                    .Any(item => !bytes.AsSpan().SequenceEqual(item))) Conflict(identity.Name!);
                Entries.Remove(path);
                Entries.Remove(Path.ChangeExtension(path, ".pdb"));
                continue;
            }
            if (selected.TryGetValue(identity.Name!, out var paths) && paths.Any(item => !bytes.AsSpan().SequenceEqual(File.ReadAllBytes(item)))) Conflict(identity.Name!);
            var reference = Path.Combine(_directory, identity.Name + ".dll");
            File.WriteAllBytes(reference, bytes);
            References.Add(reference);
        }
    }

    internal void Verify(IReadOnlyList<WorkflowInlineWorkflow> actual)
    {
        if (actual.GroupBy(pin => (pin.Name, pin.Version)).Any(group => group.Count() > 1) ||
            actual.Any(pin => string.IsNullOrWhiteSpace(pin.EntryType) || string.IsNullOrWhiteSpace(pin.AssemblyName)))
            throw new WorkflowPublishException("PUB013", "Inline workflows require one consistent executable pin per identity", null);
        foreach (var pin in actual.Where(pin => pin.BundleDigest != null))
            if (!_pins.TryGetValue((pin.Name, pin.Version), out var expected) || !SamePin(pin, expected))
                throw new WorkflowPublishException("PUB013", $"Inline workflow '{pin.Name}@{pin.Version}' differs from the verified callee pin", null);
        foreach (var (root, nested, callee) in _bodies)
        {
            // Unused templates are resolved too; only a body actually grafted into this caller has sites.
            var graft = actual.SingleOrDefault(pin => SamePin(pin, root));
            if (graft == null) continue;
            foreach (var site in graft.Sites)
            {
                var prefix = site + "/inline";
                if (nested.SelectMany(pin => pin.Sites).Any(address => !address.StartsWith("root/", StringComparison.Ordinal)))
                    throw new WorkflowPublishException("PUB013", "Callee inline sites must use its runtime catalog addresses", callee.Location);
                var expected = nested.Select(pin => pin with { Sites = pin.Sites.Select(address => prefix + address[4..]).ToArray() }).ToArray();
                var rebuilt = actual.Select(pin => pin with { Sites = pin.Sites.Where(address => address.StartsWith(prefix + "/", StringComparison.Ordinal)).ToArray() })
                    .Where(pin => pin.Sites.Count != 0).ToArray();
                if (!WorkflowDefinitionValidator.SameInlineWorkflows(expected, rebuilt))
                    throw new WorkflowPublishException("PUB013", $"Callee '{root.Name}@{root.Version}' rebuilt inline inventory differs from its verified manifest", callee.Location);
            }
        }
    }

    private void AddPin(WorkflowInlineWorkflow pin, WorkflowCallee callee)
    {
        if (_pins.TryGetValue((pin.Name, pin.Version), out var prior) && !SamePin(pin, prior))
            throw new WorkflowPublishException("PUB013", $"Conflicting inline pins for '{pin.Name}@{pin.Version}'", callee.Location);
        _pins.TryAdd((pin.Name, pin.Version), pin);
    }

    private static bool SamePin(WorkflowInlineWorkflow left, WorkflowInlineWorkflow right) =>
        left.Name == right.Name && left.Version == right.Version && left.BundleDigest == right.BundleDigest &&
        left.StructureHash == right.StructureHash && left.ContractHash == right.ContractHash &&
        left.EntryType == right.EntryType && left.AssemblyName == right.AssemblyName;

    private static void CheckClosure(WorkflowCallee callee, ResolvedWorkflowPackages packages)
    {
        var bundle = callee.Bundle!;
        var images = bundle.Entries.Where(pair => pair.Key == WorkflowBundleArchive.AssemblyPath ||
                pair.Key.StartsWith("private/", StringComparison.Ordinal) && pair.Key.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value).Concat(bundle.Manifest.ActivityPackages.SelectMany(pin =>
                pin.RuntimeAssets.Values.SelectMany(assets => assets.ManagedAssemblies).Distinct(StringComparer.Ordinal)
                    .Select(path => packages.Contents[pin.Id][path]))).ToArray();
        var names = images.Select(image => ResolvedWorkflowPackages.AssemblyIdentity(image).Name!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        names.UnionWith(bundle.Manifest.SharedAssemblies.Keys.Select(key => new AssemblyName(key).Name!));
        foreach (var image in images)
        {
            using var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(image.ToArray()));
            var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
            foreach (var handle in metadata.AssemblyReferences)
            {
                var name = metadata.GetString(metadata.GetAssemblyReference(handle).Name);
                if (!names.Contains(name))
                    throw new WorkflowPublishException("PUB009", $"Callee '{callee.Version.Name}@{callee.Version.Version}' is missing executable dependency '{name}'; republish the callee", callee.Location);
            }
        }
    }

    private void AddAssembly(ReadOnlyMemory<byte> bytes, WorkflowCallee callee)
    {
        var identity = ResolvedWorkflowPackages.AssemblyIdentity(bytes);
        if (identity.Name is not { Length: > 0 } name || name.IndexOfAny(['/', '\\', ':']) >= 0 ||
            name.Any(char.IsControl) || name.EndsWith('.') || name.EndsWith(' '))
            throw new WorkflowPublishException("PUB013", "An inline private assembly has an unsafe identity", callee.Location);
        Add("private/" + identity.Name + ".dll", bytes, callee);
        _origins.TryAdd(identity.Name!, callee);
    }

    private void Add(string path, ReadOnlyMemory<byte> bytes, WorkflowCallee callee)
    {
        var existing = Entries.Keys.FirstOrDefault(key => key.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            if (!Entries[existing].AsSpan().SequenceEqual(bytes.Span))
                throw new WorkflowPublishException("PUB010", $"Callee '{callee.Version.Name}@{callee.Version.Version}' has conflicting inline payload '{path}'", callee.Location);
            return;
        }
        Entries.Add(path, bytes.ToArray());
    }

    private void Conflict(string name) => throw new WorkflowPublishException("PUB010", $"Inline assembly '{name}' conflicts with the publishing reference closure", _origins.GetValueOrDefault(name)?.Location);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
