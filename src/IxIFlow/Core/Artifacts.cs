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
}

/// <summary>Provider maintenance, called while the catalog holds its reference lock.</summary>
public interface IArtifactRetentionStore
{
    ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime olderThanUtc,
        int batchSize, CancellationToken cancellationToken = default);
}

// DI must not acquire disposal ownership of an externally supplied state repository a second time.
internal sealed class RepositoryArtifactStore(IWorkflowStateRepository repository) : IArtifactStore
{
    internal static IArtifactStore Unwrap(IArtifactStore artifacts) =>
        artifacts is RepositoryArtifactStore provider ? provider.Repository : artifacts;
    private IWorkflowStateRepository Repository => repository;
    public ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken cancellationToken = default) =>
        repository.PutAsync(kind, content, cancellationToken);
    public ValueTask<Stream?> OpenAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) => repository.OpenAsync(digest, cancellationToken);
    public ValueTask<bool> ExistsAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) => repository.ExistsAsync(digest, cancellationToken);
    public ValueTask DeleteAsync(ArtifactDigest digest, CancellationToken cancellationToken = default) => repository.DeleteAsync(digest, cancellationToken);
}

/// <summary>Immutable, atomically installed files for a single host.</summary>
public sealed class FileSystemArtifactStore : IArtifactStore, IArtifactRetentionStore
{
    private readonly string _root;

    public FileSystemArtifactStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _root = Path.GetFullPath(directory);
        Directory.CreateDirectory(_root);
    }

    private string PathOf(ArtifactDigest digest) => Path.Combine(_root, new ArtifactDigest(digest.Value).Value[7..] + ".artifact");

    public async ValueTask<ArtifactDigest> PutAsync(ArtifactKind kind, Stream content, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        ArgumentNullException.ThrowIfNull(content);
        var temporary = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".tmp");
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
            try { File.Move(temporary, PathOf(digest)); }
            catch (IOException) when (File.Exists(PathOf(digest))) { }
            return digest;
        }
        finally { File.Delete(temporary); }
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
        File.Delete(PathOf(digest));
        return ValueTask.CompletedTask;
    }

    public ValueTask<int> PurgeUnreferencedAsync(IReadOnlySet<ArtifactDigest> references, DateTime olderThanUtc,
        int batchSize, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(_root, "*.artifact"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var digest = new ArtifactDigest("sha256:" + Path.GetFileNameWithoutExtension(file));
            if (references.Contains(digest) || File.GetLastWriteTimeUtc(file) >= olderThanUtc) continue;
            File.Delete(file);
            if (++removed == batchSize) break;
        }
        return ValueTask.FromResult(removed);
    }
}
