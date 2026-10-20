using System.IO.Compression;
using System.Text.Json;

namespace IxIFlow.Core;

/// <summary>An immutable, content-addressed nupkg registered in the publishing catalog.</summary>
public sealed record WorkflowPackage(string Id, string Version, ArtifactDigest Digest,
    IReadOnlyDictionary<string, ArtifactDigest> Entries);

public sealed record WorkflowPackageAssets(IReadOnlyList<string> ManagedAssemblies, IReadOnlyList<string> NativeLibraries);

/// <summary>Asset selection is performed once by the publisher, never by an executor.</summary>
public sealed record WorkflowPackagePin(string Id, string Version, ArtifactDigest Digest,
    IReadOnlyDictionary<string, ArtifactDigest> Entries, IReadOnlyList<string> ReferenceAssemblies,
    IReadOnlyDictionary<string, WorkflowPackageAssets> RuntimeAssets, bool RequiresRuntimeIdentifier);

public static class WorkflowPackageArchive
{
    public static async Task<IReadOnlyDictionary<string, ReadOnlyMemory<byte>>> ReadAsync(Stream archive,
        ArtifactDigest digest, WorkflowBundleReadLimits limits, CancellationToken cancellationToken = default)
    {
        limits.Validate();
        await using var buffer = new FileStream(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".nupkg"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        await WorkflowBundleArchive.CopyArchiveAsync(archive, buffer, digest, limits, cancellationToken);
        return await ReadVerifiedAsync(buffer, limits, cancellationToken);
    }

    internal static async Task<IReadOnlyDictionary<string, ReadOnlyMemory<byte>>> ReadVerifiedAsync(Stream archive,
        WorkflowBundleReadLimits limits, CancellationToken token)
    {
        try
        {
            using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Count > limits.MaxEntries) throw Mismatch("Package entry count exceeds the configured limit");
            var result = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long expanded = 0;
            foreach (var entry in zip.Entries)
            {
                var path = entry.FullName;
                if (!SafePath(path.TrimEnd('/')) || !names.Add(path.TrimEnd('/')))
                    throw Mismatch($"Duplicate or unsafe package entry '{path}'");
                if (entry.Length > limits.MaxEntryBytes || entry.Length > limits.MaxExpandedBytes - expanded)
                    throw Mismatch("Package expanded size exceeds the configured limit");
                if (path.EndsWith('/'))
                {
                    if (entry.Length != 0) throw Mismatch("Package directory has content");
                    continue;
                }
                using var input = entry.Open();
                using var output = new MemoryStream();
                var chunk = new byte[81920];
                int count;
                while ((count = await input.ReadAsync(chunk, token)) != 0)
                {
                    expanded += count;
                    if (output.Length + count > entry.Length || output.Length + count > limits.MaxEntryBytes || expanded > limits.MaxExpandedBytes)
                        throw Mismatch("Package expanded size exceeds the configured limit");
                    await output.WriteAsync(chunk.AsMemory(0, count), token);
                }
                if (output.Length != entry.Length) throw Mismatch("Package entry length differs from its declaration");
                result.Add(path, output.ToArray());
            }
            return result;
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or NotSupportedException)
        { throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, "Invalid package archive", error); }
    }

    internal static bool SafePath(string path) => path.Length > 0 && !path.Contains('\\') && !path.Contains(':') &&
        path.Split('/').All(part => part.Length > 0 && part is not ("." or "..") &&
            !part.EndsWith('.') && !part.EndsWith(' ') && !part.Any(char.IsControl));

    internal static void Verify(WorkflowPackagePin pin, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> entries)
    {
        if (entries.Count != pin.Entries.Count || entries.Any(pair => !pin.Entries.TryGetValue(pair.Key, out var hash) || ArtifactDigest.Of(pair.Value.Span) != hash))
            throw Mismatch($"Package '{pin.Id}@{pin.Version}' entries differ from the published pin");
    }

    private static WorkflowEngineException Mismatch(string message) => new(WorkflowErrorKind.ArtifactMismatch, message);
}

internal static class WorkflowPackageRules
{
    internal static WorkflowPackage Copy(WorkflowPackage package) => JsonSerializer.Deserialize<WorkflowPackage>(JsonSerializer.Serialize(package))!;
    internal static void Validate(WorkflowPackage package)
    {
        if (string.IsNullOrWhiteSpace(package.Id) || package.Id != package.Id.ToLowerInvariant() || package.Id.Length > 100 ||
            !package.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') ||
            string.IsNullOrWhiteSpace(package.Version) || package.Version.Length > 100 || package.Entries == null || package.Entries.Count == 0)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Invalid package catalog metadata");
        _ = new ArtifactDigest(package.Digest.Value);
        foreach (var (path, hash) in package.Entries)
        {
            if (!WorkflowPackageArchive.SafePath(path)) throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Invalid package entry path");
            _ = new ArtifactDigest(hash.Value);
        }
    }

    internal static void CheckRegistration(WorkflowPackage? existing, WorkflowPackage package)
    {
        Validate(package);
        if (existing != null && (existing.Digest != package.Digest || existing.Entries.Count != package.Entries.Count ||
            existing.Entries.Any(pair => !package.Entries.TryGetValue(pair.Key, out var hash) || hash != pair.Value)))
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, $"Package '{package.Id}@{package.Version}' is immutable");
    }
}
