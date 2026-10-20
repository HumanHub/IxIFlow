using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Text.Json;

namespace IxIFlow.Core;

public sealed record WorkflowCompilerSettings(string RoslynVersion, string LanguageVersion,
    string Optimization, bool Deterministic, string DebugInformationFormat);

/// <summary>The manifest is protected by the archive digest; Entries covers every other zip entry.</summary>
public sealed record WorkflowBundleManifest(
    int FormatVersion, string Name, int Version, string StructureHash, string DataType, string EntryType,
    string EngineVersion, int CheckpointContractVersion, string CombinerSignature,
    WorkflowCompilerSettings Compiler, IReadOnlyDictionary<string, string> SharedAssemblies,
    IReadOnlyDictionary<string, ArtifactDigest> Entries);

public sealed record WorkflowBundle(WorkflowBundleManifest Manifest, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Entries);

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
        CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await archive.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        if (ArtifactDigest.Of(bytes) != expectedDigest) throw Mismatch("The bundle archive digest differs from the published digest");
        try
        {
            using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
                if (!SafePath(entry.FullName) || !paths.Add(entry.FullName))
                    throw Mismatch($"Duplicate or unsafe bundle path '{entry.FullName}'");
            var manifestEntry = zip.GetEntry(ManifestPath) ?? throw Mismatch("The bundle has no manifest");
            using var manifestStream = manifestEntry.Open();
            using var manifestJson = await JsonDocument.ParseAsync(manifestStream, cancellationToken: cancellationToken);
            CheckJsonProperties(manifestJson.RootElement);
            var manifest = manifestJson.RootElement.Deserialize<WorkflowBundleManifest>()
                ?? throw Mismatch("The bundle manifest is empty");
            CheckManifest(manifest);
            if (zip.Entries.Count != manifest.Entries.Count + 1) throw Mismatch("The bundle contains unlisted or missing entries");
            var content = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
            foreach (var (path, digest) in manifest.Entries)
            {
                var entry = zip.GetEntry(path) ?? throw Mismatch($"Missing bundle entry '{path}'");
                using var input = entry.Open();
                using var entryBuffer = new MemoryStream();
                await input.CopyToAsync(entryBuffer, cancellationToken);
                var data = entryBuffer.ToArray();
                if (ArtifactDigest.Of(data) != digest) throw Mismatch($"Hash mismatch for bundle entry '{path}'");
                content.Add(path, data);
            }
            return new(manifest, new ReadOnlyDictionary<string, ReadOnlyMemory<byte>>(content));
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException or NotSupportedException)
        {
            throw Mismatch("Invalid bundle archive or manifest", error);
        }
    }

    private static void CheckManifest(WorkflowBundleManifest manifest)
    {
        if (manifest.FormatVersion != 1 || string.IsNullOrWhiteSpace(manifest.Name) || manifest.Version < 1 ||
            string.IsNullOrWhiteSpace(manifest.StructureHash) || string.IsNullOrWhiteSpace(manifest.DataType) ||
            string.IsNullOrWhiteSpace(manifest.EntryType) || string.IsNullOrWhiteSpace(manifest.EngineVersion) ||
            manifest.CheckpointContractVersion < 1 || manifest.Compiler == null || manifest.SharedAssemblies == null || manifest.Entries == null)
            throw Mismatch("Invalid bundle manifest metadata");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ManifestPath };
        foreach (var (path, digest) in manifest.Entries)
        {
            if (!SafePath(path) || !paths.Add(path)) throw Mismatch($"Duplicate or unsafe manifest path '{path}'");
            _ = new ArtifactDigest(digest.Value);
        }
        if (RequiredEntries.Any(path => !manifest.Entries.ContainsKey(path))) throw Mismatch("Missing required bundle content");
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
                if (!names.Add(property.Name)) throw Mismatch($"Duplicate manifest property '{property.Name}'");
                CheckJsonProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckJsonProperties(item);
    }

    private static void Write(ZipArchive zip, string path, byte[] content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var stream = entry.Open();
        stream.Write(content);
    }

    private static WorkflowEngineException Mismatch(string message, Exception? inner = null) =>
        new(WorkflowErrorKind.ArtifactMismatch, message, inner);
}
