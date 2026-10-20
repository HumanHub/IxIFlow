using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Core;

/// <summary>Private POSIX loader paths, removed as soon as eager loading finishes.</summary>
[UnsupportedOSPlatform("windows")]
internal sealed class NativeImageDirectory : IDisposable
{
    private readonly ILogger? _logger;
    internal string Root { get; }

    private NativeImageDirectory(string root, ILogger? logger) { Root = root; _logger = logger; }

    internal static NativeImageDirectory Create(string? temporaryRoot = null, ILogger? logger = null)
    {
        var root = Path.Combine(temporaryRoot ?? Path.GetTempPath(), "IxIFlow.Native." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new(root, logger);
    }

    public void Dispose()
    {
        string[] entries;
        try { entries = Directory.GetFileSystemEntries(Root); }
        catch (Exception error) when (error is DirectoryNotFoundException or FileNotFoundException) { return; }
        catch (Exception error) { BundleLoadContext.LogCleanupFailure(_logger, error, "private native directory enumeration"); return; }

        // Only unlink this load's flat entries; never traverse directories or links.
        foreach (var path in entries) Remove(() => File.Delete(path), "private native image unlink");
        Remove(() => Directory.Delete(Root), "private native directory removal");
    }

    private void Remove(Action remove, string phase)
    {
        try { remove(); }
        catch (Exception error) when (error is DirectoryNotFoundException or FileNotFoundException) { }
        catch (Exception error) { BundleLoadContext.LogCleanupFailure(_logger, error, phase); }
    }
}
