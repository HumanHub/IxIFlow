using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace IxIFlow.Core;

public sealed record WorkflowCompilerSettings(string RoslynVersion, string LanguageVersion,
    string Optimization, bool Deterministic, string DebugInformationFormat);

/// <summary>The manifest is protected by the archive digest; Entries covers every other zip entry.</summary>
public sealed record WorkflowBundleManifest(
    int FormatVersion, string Name, int Version, string StructureHash, string DataType, string EntryType,
    string EngineVersion, int CheckpointContractVersion, string CombinerSignature,
    WorkflowCompilerSettings Compiler, IReadOnlyDictionary<string, string> SharedAssemblies,
    IReadOnlyDictionary<string, ArtifactDigest> Entries)
{
    public IReadOnlyList<ArtifactDigest> ActivityPackages { get; init; } = [];
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
            if (size > limits.MaxArchiveBytes) throw Limit("Bundle archive exceeds the configured size limit");
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
            if (zip.Entries.Count > limits.MaxEntries) throw Limit("Bundle entry count exceeds the configured limit");
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
                    if (output.Length + count > maximum || actualExpanded > limits.MaxExpandedBytes)
                        throw Limit("Bundle expanded size exceeds the configured limit");
                    if (output.Length + count > entry.Length) throw Mismatch("Bundle entry length differs from its declaration");
                    await output.WriteAsync(chunk.AsMemory(0, count), cancellationToken);
                }
                if (output.Length != entry.Length) throw Mismatch("Bundle entry length differs from its declaration");
                return output.ToArray();
            }
            using var manifestJson = JsonDocument.Parse(await ReadEntry(manifestEntry, limits.MaxManifestBytes));
            CheckJsonProperties(manifestJson.RootElement);
            var manifest = manifestJson.RootElement.Deserialize<WorkflowBundleManifest>()
                ?? throw Mismatch("The bundle manifest is empty");
            CheckManifest(manifest);
            if (zip.Entries.Count != manifest.Entries.Count + 1) throw Mismatch("The bundle contains unlisted or missing entries");
            var content = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
            foreach (var (path, digest) in manifest.Entries)
            {
                var entry = zip.GetEntry(path) ?? throw Mismatch($"Missing bundle entry '{path}'");
                var data = await ReadEntry(entry, limits.MaxEntryBytes);
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
            manifest.CheckpointContractVersion < 1 || manifest.Compiler == null || manifest.SharedAssemblies == null || manifest.Entries == null || manifest.ActivityPackages == null)
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

    private static WorkflowEngineException Limit(string message) =>
        new(WorkflowErrorKind.ResourceExhausted, message);

    private static WorkflowEngineException Mismatch(string message, Exception? inner = null) =>
        new(WorkflowErrorKind.ArtifactMismatch, message, inner);
}
