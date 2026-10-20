using System.Text.Json;
using IxIFlow.Core;
using NuGet.Common;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace IxIFlow.Authoring;

/// <summary>Bounded, metadata-only V3 discovery. Candidate discovery never requests a nupkg.</summary>
internal sealed class WorkflowPackageFeed(SourceRepository source, SourceCacheContext cache, WorkflowBundleReadLimits limits,
    Func<long> remaining, Action<long> charge)
{
    private string? _baseAddress;

    private async Task<byte[]?> ReadAsync(string url, CancellationToken token)
    {
        var http = (await source.GetResourceAsync<HttpSourceResource>(token)).HttpSource;
        return await http.ProcessStreamAsync(new HttpSourceRequest(url, NullLogger.Instance) { IgnoreNotFounds = true }, async stream =>
        {
            if (stream == null) return null;
            using var buffer = new MemoryStream();
            var maximum = Math.Min(limits.MaxManifestBytes, remaining());
            var block = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(block, token)) != 0)
            {
                if (buffer.Length > maximum - count)
                    throw new WorkflowEngineException(WorkflowErrorKind.ResourceExhausted, "Package feed metadata exceeds the configured byte limit");
                buffer.Write(block, 0, count);
            }
            charge(buffer.Length);
            return buffer.ToArray();
        }, cache, NullLogger.Instance, token);
    }

    private async Task<string> BaseAddressAsync(CancellationToken token)
    {
        if (_baseAddress != null) return _baseAddress;
        var bytes = await ReadAsync(source.PackageSource.Source, token) ?? throw new InvalidOperationException("NuGet service index is unavailable");
        using var json = JsonDocument.Parse(bytes);
        var address = json.RootElement.GetProperty("resources").EnumerateArray()
            .First(resource => resource.GetProperty("@type").GetString()!.StartsWith("PackageBaseAddress/3.0.0", StringComparison.Ordinal))
            .GetProperty("@id").GetString()!;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("NuGet package base address is invalid");
        return _baseAddress = address.TrimEnd('/') + "/";
    }

    internal async Task<NuGetVersion[]> VersionsAsync(string id, CancellationToken token)
    {
        var bytes = await ReadAsync(await BaseAddressAsync(token) + id.ToLowerInvariant() + "/index.json", token);
        if (bytes == null) return [];
        using var json = JsonDocument.Parse(bytes);
        return json.RootElement.GetProperty("versions").EnumerateArray().Select(value => NuGetVersion.Parse(value.GetString()!)).ToArray();
    }

    internal async Task<IEnumerable<PackageDependencyGroup>?> DependenciesAsync(PackageIdentity identity, CancellationToken token)
    {
        var id = identity.Id.ToLowerInvariant(); var version = WorkflowPackageResolver.VersionKey(identity.Version);
        var bytes = await ReadAsync(await BaseAddressAsync(token) + id + "/" + version + "/" + id + ".nuspec", token);
        return bytes == null ? null : new NuspecReader(new MemoryStream(bytes)).GetDependencyGroups().ToArray();
    }

    internal async Task<bool> DownloadAsync(PackageIdentity identity, Stream destination, CancellationToken token)
    {
        var id = identity.Id.ToLowerInvariant(); var version = WorkflowPackageResolver.VersionKey(identity.Version);
        var url = await BaseAddressAsync(token) + id + "/" + version + "/" + id + "." + version + ".nupkg";
        var http = (await source.GetResourceAsync<HttpSourceResource>(token)).HttpSource;
        return await http.ProcessStreamAsync(new HttpSourceRequest(url, NullLogger.Instance) { IgnoreNotFounds = true }, async stream =>
        {
            if (stream == null) return false;
            destination.Position = 0; destination.SetLength(0);
            var block = new byte[81920];
            int count;
            while ((count = await stream.ReadAsync(block, token)) != 0)
                await destination.WriteAsync(block.AsMemory(0, count), token);
            return true;
        }, cache, NullLogger.Instance, token);
    }
}
