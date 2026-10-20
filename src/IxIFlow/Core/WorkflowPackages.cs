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
    IReadOnlyDictionary<string, WorkflowPackageAssets> RuntimeAssets, bool RequiresRuntimeIdentifier)
{
    /// <summary>Null means all platforms; an empty list means no supported platform.</summary>
    public IReadOnlyList<string>? ApplicableRuntimeIdentifiers { get; init; }
    public bool AppliesTo(string rid) => ApplicableRuntimeIdentifiers == null || ApplicableRuntimeIdentifiers.Contains(rid, StringComparer.Ordinal);
}

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
            if (zip.Entries.Count > limits.MaxEntries) throw new WorkflowEngineException(WorkflowErrorKind.ResourceExhausted, "Package entry count exceeds the configured limit");
            var result = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long expanded = 0;
            foreach (var entry in zip.Entries)
            {
                var path = entry.FullName;
                if (!SafePath(path.TrimEnd('/')) || !names.Add(path.TrimEnd('/')))
                    throw Mismatch($"Duplicate or unsafe package entry '{path}'");
                if (entry.Length > limits.MaxEntryBytes || entry.Length > limits.MaxExpandedBytes - expanded)
                    throw new WorkflowEngineException(WorkflowErrorKind.ResourceExhausted, "Package expanded size exceeds the configured limit");
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
                        throw new WorkflowEngineException(WorkflowErrorKind.ResourceExhausted, "Package expanded size exceeds the configured limit");
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
            !ReservedName(part) && !part.EndsWith('.') && !part.EndsWith(' ') && !part.Any(char.IsControl));

    private static bool ReservedName(string part)
    {
        var stem = part.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
            (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³');
    }

    internal static void Verify(WorkflowPackagePin pin, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> entries)
    {
        if (entries.Count != pin.Entries.Count || entries.Any(pair => !pin.Entries.TryGetValue(pair.Key, out var hash) || ArtifactDigest.Of(pair.Value.Span) != hash))
            throw Mismatch($"Package '{pin.Id}@{pin.Version}' entries differ from the published pin");
    }

    private static WorkflowEngineException Mismatch(string message) => new(WorkflowErrorKind.ArtifactMismatch, message);
}

internal static class WorkflowPackageRules
{
    internal static bool ValidId(string? id) => id != null && id.Length is > 0 and <= 100 &&
        System.Text.RegularExpressions.Regex.IsMatch(id, @"\A\w+([.-]\w+)*\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static bool CanonicalVersion(string? version)
    {
        if (version == null || version.Length > 100 || version != version.ToLowerInvariant()) return false;
        var pieces = version.Split('-', 2);
        var numbers = pieces[0].Split('.');
        if (numbers.Length is not (3 or 4) || numbers.Any(number => !int.TryParse(number, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var value) || number != value.ToString(System.Globalization.CultureInfo.InvariantCulture)) ||
            numbers.Length == 4 && numbers[3] == "0") return false;
        return pieces.Length == 1 || pieces[1].Split('.').All(label => label.Length > 0 && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
    }

    internal static WorkflowPackage Copy(WorkflowPackage package) => JsonSerializer.Deserialize<WorkflowPackage>(JsonSerializer.Serialize(package))!;
    internal static void Validate(WorkflowPackage package)
    {
        if (!ValidId(package.Id) || package.Id != package.Id.ToLowerInvariant() ||
            !CanonicalVersion(package.Version) || package.Entries == null || package.Entries.Count == 0)
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
