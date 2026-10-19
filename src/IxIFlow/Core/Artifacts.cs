using System.Security.Cryptography;

namespace IxIFlow.Core;

/// <summary>SHA-256 of the exact stored bytes, in canonical lowercase hexadecimal.</summary>
public readonly record struct ArtifactDigest
{
    [System.Text.Json.Serialization.JsonConstructor]
    public ArtifactDigest(string value)
    {
        if (value == null || value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal) ||
            value.AsSpan(7).ContainsAnyExcept("0123456789abcdef"))
            throw new ArgumentException("An artifact digest must be sha256: followed by 64 lowercase hexadecimal digits", nameof(value));
        Value = value;
    }

    public string Value { get; }
    public override string ToString() => Value;
    public static ArtifactDigest Of(ReadOnlySpan<byte> bytes) =>
        new("sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)));
}

public enum ArtifactKind { Bundle, Package, Template }

public interface IArtifactStore
{
    ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken cancellationToken = default);
    ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken cancellationToken = default);
    ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken cancellationToken = default);
    ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken cancellationToken = default);
    /// <summary>Collects one bounded page while the caller holds the catalog reference lock.</summary>
    ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime olderThanUtc,
        int batchSize, CancellationToken cancellationToken = default);
}

// DI must not acquire disposal ownership of an externally supplied state repository a second time.
internal sealed class RepositoryArtifactStore(IWorkflowStateRepository repository) : IArtifactStore
{
    public ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken cancellationToken = default) =>
        repository.PutAsync(kind, content, cancellationToken);
    public ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) => repository.OpenAsync(digest, cancellationToken);
    public ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) => repository.ExistsAsync(digest, cancellationToken);
    public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) => repository.DeleteAsync(digest, cancellationToken);
    public ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime olderThanUtc,
        int batchSize, CancellationToken cancellationToken = default) =>
        repository.PurgeUnreferencedAsync(references, olderThanUtc, batchSize, cancellationToken);
}

/// <summary>Immutable, atomically installed files for a single host.</summary>
public sealed class FileSystemArtifactStore : IArtifactStore
{
    private readonly string _root;
    // Providers for the same directory serialize installation, refresh and conditional deletion.
    private sealed class DirectoryState
    {
        public HashSet<string> Uploads { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DirectoryState> Gates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly DirectoryState _gate;
    private static readonly TimeSpan StagingRetention = TimeSpan.FromDays(1);

    public FileSystemArtifactStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _root = Path.GetFullPath(directory);
        _gate = Gates.GetOrAdd(_root, _ => new DirectoryState());
        Directory.CreateDirectory(_root);
    }

    private string PathOf(ArtifactDigest digest) => Path.Combine(_root, new ArtifactDigest(digest.Value).Value[7..] + ".artifact");

    public async ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        ArgumentNullException.ThrowIfNull(content);
        var temporary = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".tmp");
        lock (_gate) _gate.Uploads.Add(temporary);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920];
                int count;
                while ((count = await content.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    hash.AppendData(buffer, 0, count);
                    await file.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
                await file.FlushAsync(cancellationToken);
                file.Flush(flushToDisk: true);
            }
            var digest = new ArtifactDigest("sha256:" + Convert.ToHexStringLower(hash.GetHashAndReset()));
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                try { File.Move(temporary, PathOf(digest)); }
                catch (IOException) when (File.Exists(PathOf(digest))) { }
                File.SetLastWriteTimeUtc(PathOf(digest), DateTime.UtcNow);
            }
            return digest;
        }
        finally
        {
            lock (_gate)
            {
                try { File.Delete(temporary); }
                finally { _gate.Uploads.Remove(temporary); }
            }
        }
    }

    public ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return ValueTask.FromResult<Stream?>(new FileStream(PathOf(digest), FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan));
        }
        catch (FileNotFoundException) { return ValueTask.FromResult<Stream?>(null); }
    }

    public ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(File.Exists(PathOf(digest)));
    }

    public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) File.Delete(PathOf(digest));
        return ValueTask.CompletedTask;
    }

    public ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime olderThanUtc,
        int batchSize, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        lock (_gate)
        {
            // An active writer holds FileShare.None, including during a long stalled upload.
            foreach (var path in Directory.EnumerateFiles(_root, "*.tmp")
                .Where(path => !_gate.Uploads.Contains(path) && File.GetLastWriteTimeUtc(path) < DateTime.UtcNow - StagingRetention)
                .OrderBy(path => path, StringComparer.Ordinal).Take(batchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var abandoned = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
                        FileShare.None, 1, FileOptions.DeleteOnClose);
                }
                catch (IOException) { } // Still being written, or already reclaimed.
            }
            var candidates = Directory.EnumerateFiles(_root, "*.artifact")
                .Select(path => (Path: path, Name: Path.GetFileNameWithoutExtension(path)))
                .Where(file => file.Name.Length == 64 && !file.Name.AsSpan().ContainsAnyExcept("0123456789abcdef"))
                .Select(file => (file.Path, Digest: new ArtifactDigest("sha256:" + file.Name), Created: File.GetLastWriteTimeUtc(file.Path)))
                .Where(file => file.Created < olderThanUtc && !references.Contains(file.Digest))
                .OrderBy(file => file.Created).ThenBy(file => file.Digest.Value, StringComparer.Ordinal)
                .Take(batchSize).ToArray();
            var removed = 0;
            foreach (var file in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.GetLastWriteTimeUtc(file.Path) >= olderThanUtc) continue;
                File.Delete(file.Path);
                removed++;
            }
            return ValueTask.FromResult(removed);
        }
    }
}
