using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace IxIFlow.Core;

/// <summary>Private POSIX loader paths, removed as soon as eager loading finishes.</summary>
[UnsupportedOSPlatform("windows")]
internal sealed class NativeImageDirectory : IDisposable
{
    private const string Prefix = "IxIFlow.Native.";
    private const string OwnerFile = ".owner";
    private readonly FileStream _owner;
    internal string Root { get; }

    private NativeImageDirectory(string root, FileStream owner) { Root = root; _owner = owner; }

    internal static NativeImageDirectory Create(string? temporaryRoot = null)
    {
        temporaryRoot ??= Path.GetTempPath();
        Recover(temporaryRoot);
        var root = Path.Combine(temporaryRoot, Prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try { return new(root, new FileStream(Path.Combine(root, OwnerFile), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)); }
        catch { Directory.Delete(root); throw; }
    }

    internal static void Recover(string temporaryRoot)
    {
        foreach (var root in Directory.EnumerateDirectories(temporaryRoot, Prefix + "*"))
        {
            try
            {
                if (!IsOwnedDirectory(root) || File.GetUnixFileMode(root) !=
                    (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)) continue;
                var marker = Path.Combine(root, OwnerFile);
                // Older unmarked directories cannot be proved idle: a previous
                // engine may still be between copying and dlopen. Leave them alone.
                if (!File.Exists(marker) || (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0) continue;
                using var owner = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                // Fail closed if .NET file locking is disabled or unsupported.
                if (!EnforcesLock(marker)) continue;
                DeleteCopies(root);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or EntryPointNotFoundException) { }
        }
    }

    private static bool EnforcesLock(string marker)
    {
        try { using var probe = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (IOException error) when ((error.HResult & 0xffff) is 11 or 35) { return true; }
    }

    private static void DeleteCopies(string root)
    {
        var entries = Directory.GetFileSystemEntries(root);
        // The loader writes only flat regular files. Never traverse planted links
        // or directories, even in a candidate owned by the current user.
        if (entries.Any(path => (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0))
            throw new IOException("Private native directory contains an unexpected entry");
        foreach (var path in entries.Where(path => Path.GetFileName(path) != OwnerFile)) File.Delete(path);
        File.Delete(Path.Combine(root, OwnerFile));
        Directory.Delete(root);
    }

    public void Dispose()
    {
        try { DeleteCopies(Root); }
        finally { _owner.Dispose(); }
    }

    internal static bool IsOwnedDirectory(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            // statx has a fixed ABI across Linux architectures. Do not follow links.
            return Statx(-100, path, 0x100, 0x9, out var status) == 0 &&
                (status.Mask & 0x9) == 0x9 && (status.Mode & 0xf000) == 0x4000 && status.Uid == GetEffectiveUid();
        }
        if (OperatingSystem.IsMacOS())
        {
            var attributes = new DarwinAttributes { Count = 5, Common = 0x8008 }; // OBJTYPE | OWNERID
            return GetAttributes(path, ref attributes, out var status, 12, 1) == 0 &&
                status.Length == 12 && status.Type == 2 && status.Uid == GetEffectiveUid(); // FSOPT_NOFOLLOW, VDIR
        }
        // No guessed stat layouts on other Unix platforms: recovery is optional.
        return false;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatus
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(20)] internal uint Uid;
        [FieldOffset(28)] internal ushort Mode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DarwinAttributes
    {
        internal ushort Count, Reserved;
        internal uint Common, Volume, Directory, File, Fork;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DarwinStatus { internal uint Length, Type, Uid; }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUid();
    [DllImport("libc", EntryPoint = "statx", CharSet = CharSet.Ansi)]
    private static extern int Statx(int directory, string path, int flags, uint mask, out LinuxStatus status);
    [DllImport("libc", EntryPoint = "getattrlist", CharSet = CharSet.Ansi)]
    private static extern int GetAttributes(string path, ref DarwinAttributes attributes, out DarwinStatus status, nuint size, uint options);
}
