using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace IxIFlow.TestSupport;

public static class ExistingPath
{
    // Resolve every component: a regular leaf may still have linked ancestors
    // (for example TMPDIR) while the OS reports the physical path of a mapping.
    public static string Canonicalize(string path)
    {
        path = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows())
        {
            using var handle = OpenDirectory(path, 0, 7, 0, 3, 0x02000000, 0); // metadata, all sharing, BACKUP_SEMANTICS
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
            var buffer = new StringBuilder(32768);
            var length = FinalPath(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity) throw new Win32Exception(Marshal.GetLastPInvokeError());
            var resolved = buffer.ToString();
            if (resolved.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) resolved = @"\\" + resolved[8..];
            else if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal)) resolved = resolved[4..];
            return Path.TrimEndingDirectorySeparator(resolved);
        }
        var result = RealPath(path, 0);
        if (result == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        try { return Path.TrimEndingDirectorySeparator(Marshal.PtrToStringUTF8(result)!); }
        finally { Free(result); }
    }

    [DllImport("kernel32", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenDirectory(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint FinalPath(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
    [DllImport("libc", EntryPoint = "realpath", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern nint RealPath(string path, nint resolved);
    [DllImport("libc", EntryPoint = "free")]
    private static extern void Free(nint pointer);
}
