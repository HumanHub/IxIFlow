using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using IxIFlow.Templates;

namespace IxIFlow.Core;

public sealed record WorkflowCompilerSettings(
    string RoslynVersion,
    string LanguageVersion,
    string Optimization,
    bool Deterministic,
    string DebugInformationFormat);

/// <summary>The manifest is protected by the archive digest; Entries covers every other zip entry.</summary>
public sealed record WorkflowBundleManifest(
    int FormatVersion,
    string Name,
    int Version,
    string StructureHash,
    string DataType,
    string EntryType,
    string EngineVersion,
    int CheckpointContractVersion,
    string CombinerSignature,
    string Contract,
    string ContractHash,
    WorkflowCompilerSettings Compiler,
    IReadOnlyDictionary<string, string> SharedAssemblies,
    IReadOnlyDictionary<string, ArtifactDigest> Entries)
{
    [System.Text.Json.Serialization.JsonRequired]
    public IReadOnlyList<WorkflowVersionInvocation> Invocations { get; init; } = [];

    [System.Text.Json.Serialization.JsonRequired]
    public IReadOnlyList<WorkflowInlineWorkflow> InlineWorkflows { get; init; } = [];

    [System.Text.Json.Serialization.JsonRequired]
    public IReadOnlyList<WorkflowTemplatePin> Templates { get; init; } = [];

    [System.Text.Json.Serialization.JsonRequired]
    public IReadOnlyList<WorkflowTemplateBinding> TemplateBindings { get; init; } = [];

    public string TargetFramework { get; init; } = "net10.0";
    public IReadOnlyList<WorkflowPackagePin> ActivityPackages { get; init; } = [];
}

public sealed record WorkflowBundle(WorkflowBundleManifest Manifest, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Entries);

/// <summary>Resource bounds applied before allocation and throughout archive and entry reads.</summary>
public sealed record WorkflowBundleReadLimits
{
    public long MaxArchiveBytes { get; init; } = 64 * 1024 * 1024;
    public int MaxManifestBytes { get; init; } = 1024 * 1024;
    public int MaxEntries { get; init; } = 1024;
    public int MaxEntryBytes { get; init; } = 32 * 1024 * 1024;
    public long MaxExpandedBytes { get; init; } = 256 * 1024 * 1024;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxArchiveBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxManifestBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxEntryBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxExpandedBytes);
    }
}

/// <summary>Creates and verifies archives without loading or executing any code.</summary>
public static class WorkflowBundleArchive
{
    public const string ManifestPath = "manifest.json";
    public const string AssemblyPath = "workflow.dll";
    public const string PdbPath = "workflow.pdb";
    public const string YamlPath = "workflow.yaml";
    public const string SourcePath = "workflow.g.cs";
    private static readonly string[] RequiredEntries = [AssemblyPath, PdbPath, YamlPath, SourcePath];

    public static byte[] Create(WorkflowBundleManifest manifest, IReadOnlyDictionary<string, byte[]> entries)
    {
        var hashes = entries.ToDictionary(pair => pair.Key, pair => ArtifactDigest.Of(pair.Value), StringComparer.Ordinal);
        manifest = manifest with { Entries = hashes };
        CheckManifest(manifest);
        using var output = new MemoryStream();

        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                Write(zip, path, content);

            Write(zip, ManifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest));
        }

        return output.ToArray();
    }

    public static async Task<WorkflowBundle> ReadAsync(Stream archive, ArtifactDigest expectedDigest,
        CancellationToken cancellationToken = default, WorkflowBundleReadLimits? limits = null)
    {
        limits ??= new();
        limits.Validate();

        await using var buffer = new FileStream(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".bundle"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);

        await CopyArchiveAsync(archive, buffer, expectedDigest, limits, cancellationToken);

        return await ReadVerifiedAsync(buffer, limits, cancellationToken);
    }

    internal static async Task CopyArchiveAsync(Stream archive, Stream destination, ArtifactDigest expectedDigest,
        WorkflowBundleReadLimits limits, CancellationToken token)
    {
        if (archive.CanSeek && archive.Length - archive.Position > limits.MaxArchiveBytes)
            throw Limit("Bundle archive exceeds the configured size limit");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long size = 0;
        int read;

        while ((read = await archive.ReadAsync(buffer, token)) != 0)
        {
            size += read;

            if (size > limits.MaxArchiveBytes)
                throw Limit("Bundle archive exceeds the configured size limit");

            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), token);
        }

        if ("sha256:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != expectedDigest.Value)
            throw Mismatch("The bundle archive digest differs from the published digest");

        destination.Position = 0;
    }

    internal static async Task<WorkflowBundle> ReadVerifiedAsync(Stream buffer, WorkflowBundleReadLimits limits,
        CancellationToken cancellationToken)
    {
        try
        {
            using var zip = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);

            if (zip.Entries.Count > limits.MaxEntries)
                throw Limit("Bundle entry count exceeds the configured limit");

            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long expanded = 0;

            foreach (var entry in zip.Entries)
            {
                if (!SafePath(entry.FullName) || !paths.Add(entry.FullName))
                    throw Mismatch($"Duplicate or unsafe bundle path '{entry.FullName}'");

                var maximum = entry.FullName == ManifestPath ? limits.MaxManifestBytes : limits.MaxEntryBytes;

                if (entry.Length > maximum || entry.Length > limits.MaxExpandedBytes - expanded)
                    throw Limit("Bundle expanded size exceeds the configured limit");

                expanded += entry.Length;
            }

            var manifestEntry = zip.GetEntry(ManifestPath) ?? throw Mismatch("The bundle has no manifest");
            long actualExpanded = 0;

            async Task<byte[]> ReadEntry(ZipArchiveEntry entry, int maximum)
            {
                using var input = entry.Open();
                using var output = new MemoryStream();
                var chunk = new byte[81920];
                int count;

                while ((count = await input.ReadAsync(chunk, cancellationToken)) != 0)
                {
                    actualExpanded += count;

                    if (output.Length + count > entry.Length)
                        throw Mismatch("Bundle entry length differs from its declaration");

                    if (output.Length + count > maximum || actualExpanded > limits.MaxExpandedBytes)
                        throw Limit("Bundle expanded size exceeds the configured limit");

                    await output.WriteAsync(chunk.AsMemory(0, count), cancellationToken);
                }

                if (output.Length != entry.Length)
                    throw Mismatch("Bundle entry length differs from its declaration");

                return output.ToArray();
            }

            using var manifestJson = JsonDocument.Parse(await ReadEntry(manifestEntry, limits.MaxManifestBytes));
            CheckJsonProperties(manifestJson.RootElement);

            var manifest = manifestJson.RootElement.Deserialize<WorkflowBundleManifest>()
                ?? throw Mismatch("The bundle manifest is empty");

            CheckManifest(manifest);

            if (zip.Entries.Count != manifest.Entries.Count + 1)
                throw Mismatch("The bundle contains unlisted or missing entries");

            var content = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);

            foreach (var (path, digest) in manifest.Entries)
            {
                var entry = zip.GetEntry(path) ?? throw Mismatch($"Missing bundle entry '{path}'");
                var data = await ReadEntry(entry, limits.MaxEntryBytes);

                if (ArtifactDigest.Of(data) != digest)
                    throw Mismatch($"Hash mismatch for bundle entry '{path}'");

                content.Add(path, data);
            }

            return new(manifest, new ReadOnlyDictionary<string, ReadOnlyMemory<byte>>(content));
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException or NotSupportedException)
        {
            throw Mismatch("Invalid bundle archive or manifest", error);
        }
    }

    internal static void CheckManifest(WorkflowBundleManifest manifest)
    {
        if (manifest.FormatVersion != 6 || string.IsNullOrWhiteSpace(manifest.Name) || manifest.Version < 1 ||
            string.IsNullOrWhiteSpace(manifest.StructureHash) || string.IsNullOrWhiteSpace(manifest.DataType) ||
            string.IsNullOrWhiteSpace(manifest.EntryType) || string.IsNullOrWhiteSpace(manifest.EngineVersion) ||
            manifest.CheckpointContractVersion < 1 || manifest.Compiler == null || manifest.SharedAssemblies == null ||
            manifest.Entries == null || manifest.ActivityPackages == null ||
            manifest.Templates == null || manifest.TemplateBindings == null || manifest.InlineWorkflows == null ||
            manifest.Invocations == null || manifest.Invocations.Any(invocation => invocation == null ||
                string.IsNullOrWhiteSpace(invocation.Name) || invocation.Name != invocation.Name.Trim() || invocation.Version < 1 ||
                string.IsNullOrWhiteSpace(invocation.Step) || string.IsNullOrWhiteSpace(invocation.SourceLocation)))
            throw Mismatch("Invalid bundle manifest metadata");

        try
        {
            var contract = WorkflowContract.Parse(manifest.Contract);

            if (contract.ToJson() != manifest.Contract || contract.Hash != manifest.ContractHash)
                throw new ArgumentException("Contract JSON or hash differs");
        }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException)
        {
            throw Mismatch("Invalid bundle workflow contract or contract hash", error);
        }

        var packageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pin in manifest.ActivityPackages)
        {
            if (pin == null)
                throw Mismatch("Invalid null package pin");

            try
            {
                WorkflowPackageRules.Validate(new(pin.Id, pin.Version, pin.Digest, pin.Entries, []));
            }
            catch (Exception error) when (error is WorkflowEngineException or ArgumentException)
            {
                throw Mismatch("Invalid package pin", error);
            }

            if (!packageIds.Add(pin.Id) || pin.ReferenceAssemblies == null || pin.RuntimeAssets == null ||
                (!pin.RequiresRuntimeIdentifier && !pin.RuntimeAssets.ContainsKey("")) ||
                pin.RuntimeAssets.Values.Any(assets =>
                    assets == null || assets.ManagedAssemblies == null || assets.NativeLibraries == null) ||
                pin.ReferenceAssemblies
                    .Concat(pin.RuntimeAssets.Values.SelectMany(assets => assets.ManagedAssemblies.Concat(assets.NativeLibraries)))
                    .Any(path => !pin.Entries.ContainsKey(path)))
                throw Mismatch("Invalid package asset pin");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ManifestPath };

        foreach (var (path, digest) in manifest.Entries)
        {
            if (!SafePath(path) || !paths.Add(path))
                throw Mismatch($"Duplicate or unsafe manifest path '{path}'");

            _ = new ArtifactDigest(digest.Value);
        }

        if (RequiredEntries.Any(path => !manifest.Entries.ContainsKey(path)))
            throw Mismatch("Missing required bundle content");

        var templates = new HashSet<TemplateReference>();

        foreach (var pin in manifest.Templates)
        {
            if (pin == null)
                throw Mismatch("Invalid null template pin");

            TemplateReference reference;

            try
            {
                reference = new(pin.Key, pin.Version);
            }
            catch (ArgumentException error)
            {
                throw Mismatch("Invalid template key or version", error);
            }

            if (string.IsNullOrWhiteSpace(pin.ModelTypeId) ||
                pin.Entry != $"templates/{pin.Key}@{pin.Version}.json" ||
                !templates.Add(reference) || !manifest.Entries.ContainsKey(pin.Entry))
                throw Mismatch("Invalid, duplicate or missing template pin");
        }

        if (manifest.Entries.Keys.Where(path => path.StartsWith("templates/", StringComparison.Ordinal))
            .Any(path => !manifest.Templates.Any(pin => pin.Entry == path)))
            throw Mismatch("Unpinned template payload");

        var bindingSites = new HashSet<string>(StringComparer.Ordinal);

        foreach (var binding in manifest.TemplateBindings)
        {
            if (binding == null || string.IsNullOrWhiteSpace(binding.Step) || !bindingSites.Add(binding.Step) ||
                binding.Templates == null || binding.Templates.Count == 0 ||
                binding.Templates.Distinct().Count() != binding.Templates.Count ||
                binding.Templates.Any(reference => reference == null || !templates.Contains(reference)))
                throw Mismatch("Invalid or unpinned template binding");
        }

        var recipes = new HashSet<(string, int)>();
        var sites = new HashSet<string>(StringComparer.Ordinal);

        foreach (var inline in manifest.InlineWorkflows)
        {
            if (inline == null || string.IsNullOrWhiteSpace(inline.Name) || inline.Name != inline.Name.Trim() || inline.Version < 1 ||
                string.IsNullOrWhiteSpace(inline.StructureHash) || string.IsNullOrWhiteSpace(inline.ContractHash) ||
                string.IsNullOrWhiteSpace(inline.EntryType) || string.IsNullOrWhiteSpace(inline.AssemblyName) ||
                !recipes.Add((inline.Name, inline.Version)) || inline.Sites == null || inline.Sites.Count == 0 ||
                inline.Sites.Any(site => string.IsNullOrWhiteSpace(site) || !sites.Add(site)))
                throw Mismatch("Invalid or duplicate inline workflow recipe or site");

            if (!SafePath(inline.AssemblyName + ".dll") || inline.AssemblyName.Contains('/'))
                throw Mismatch("Invalid inline workflow assembly name");

            if (inline.BundleDigest is { } digest)
            {
                _ = new ArtifactDigest(digest.Value);
                var prefix = WorkflowBundleIdentity.InlineSourceDirectory(inline.Name, inline.Version);

                if (inline.AssemblyName != WorkflowBundleIdentity.AssemblyName(inline.Name, inline.Version) ||
                    !manifest.Entries.ContainsKey(prefix + YamlPath) || !manifest.Entries.ContainsKey(prefix + SourcePath))
                    throw Mismatch("Missing inline workflow canonical sources or invalid assembly identity");
            }

            var inClosure =
                manifest.SharedAssemblies.Keys.Any(key => new System.Reflection.AssemblyName(key).Name == inline.AssemblyName) ||
                inline.AssemblyName == WorkflowBundleIdentity.AssemblyName(manifest.Name, manifest.Version) ||
                manifest.ActivityPackages.Any(package => package.RuntimeAssets.Values.Any(assets =>
                    assets.ManagedAssemblies.Any(path => Path.GetFileNameWithoutExtension(path) == inline.AssemblyName)));

            if (!manifest.Entries.ContainsKey("private/" + inline.AssemblyName + ".dll") &&
                (inline.BundleDigest != null || !inClosure))
                throw Mismatch("Missing inline workflow executable payload");
        }
    }

    private static bool SafePath(string path) => path.Length > 0 && !path.Contains('\\') && !path.Contains(':') &&
        path.Split('/').All(part => part.Length > 0 && part is not ("." or "..") &&
            !part.EndsWith('.') && !part.EndsWith(' ') && !part.Any(char.IsControl));

    private static void CheckJsonProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw Mismatch($"Duplicate manifest property '{property.Name}'");

                CheckJsonProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
                CheckJsonProperties(item);
    }

    private static void Write(ZipArchive zip, string path, byte[] content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var stream = entry.Open();
        stream.Write(content);
    }

    private static WorkflowEngineException Limit(string message) =>
        new(WorkflowErrorKind.ResourceExhausted, message);

    private static WorkflowEngineException Mismatch(string message, Exception? inner = null) =>
        new(WorkflowErrorKind.ArtifactMismatch, message, inner);
}