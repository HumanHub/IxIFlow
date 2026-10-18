using Microsoft.Data.Sqlite;

namespace IxIFlow.Core;

/// <summary>
/// Holds an exclusive OS handle on the database's .ixiflow-owner file for the repository's lifetime.
/// A crash closes the handle, so a leftover file never prevents restart. Coordinators are unsupported.
/// Unix requires working flock support and enabled .NET file locking; startup probes enforcement.
/// Network file systems are unsupported because SQLite WAL requires local shared memory.
/// </summary>
internal sealed class SqliteHostOwnership : IDisposable
{
    private readonly FileStream _owner;

    public SqliteHostOwnership(string connectionString)
    {
        var source = new SqliteConnectionStringBuilder(connectionString);
        var path = Path.GetFullPath(source.DataSource);
        // On Unix, DriveType queries the directory's file system, including mounted network volumes.
        var drive = new DriveInfo(Path.GetDirectoryName(path)!);
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) ||
            drive.DriveType == DriveType.Network)
            throw new ArgumentException("SQLite requires a local database file; UNC and network paths are unsupported",
                nameof(connectionString));
        var file = path + ".ixiflow-owner";
        try
        {
            _owner = new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error) when (IsSharingViolation(error))
        {
            throw new InvalidOperationException(
                $"SQLite database '{source.DataSource}' is already owned. SQLite accepts one host and no coordinator.",
                error);
        }
        try
        {
            if (!EnforcesLock(file))
                throw new InvalidOperationException(
                    $"The file system does not enforce SQLite ownership for '{source.DataSource}'. Enable file locking on a local file system.");
        }
        catch
        {
            _owner.Dispose();
            throw;
        }
    }

    private static bool EnforcesLock(string file)
    {
        try
        {
            using var probe = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException error) when (IsSharingViolation(error))
        {
            return true;
        }
    }

    private static bool IsSharingViolation(IOException error) => OperatingSystem.IsWindows()
        ? error.HResult == unchecked((int)0x80070020) // ERROR_SHARING_VIOLATION
        : error.HResult == (OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD() ? 35 : 11); // EWOULDBLOCK/EAGAIN

    public void Dispose() => _owner.Dispose();
}
