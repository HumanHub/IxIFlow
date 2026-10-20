using System.Reflection;
using System.Runtime.InteropServices;

namespace IxIFlow.Core;

internal static class WorkflowNativeLibraries
{
    private static readonly HashSet<string> WindowsSystem = new(StringComparer.OrdinalIgnoreCase)
    {
        "kernel32.dll", "kernelbase.dll", "ntdll.dll", "advapi32.dll", "user32.dll", "gdi32.dll", "gdiplus.dll",
        "ole32.dll", "oleaut32.dll", "combase.dll", "shell32.dll", "shlwapi.dll", "ws2_32.dll", "bcrypt.dll",
        "crypt32.dll", "secur32.dll", "rpcrt4.dll", "ucrtbase.dll", "msvcrt.dll", "version.dll", "winmm.dll",
        "normaliz.dll", "psapi.dll", "dbghelp.dll", "iphlpapi.dll", "dnsapi.dll", "winhttp.dll", "setupapi.dll"
    };
    private static readonly HashSet<string> UnixSystem = new(StringComparer.Ordinal)
    {
        "libc.so.6", "libm.so.6", "libdl.so.2", "libpthread.so.0", "librt.so.1", "libgcc_s.so.1", "libstdc++.so.6",
        "ld-linux-x86-64.so.2", "ld-linux-aarch64.so.1", "ld-linux.so.2", "libc.musl-x86_64.so.1", "libc.musl-aarch64.so.1"
    };

    internal static string DependencyName(string import, string rid)
    {
        if (rid.StartsWith("osx", StringComparison.Ordinal) && import.StartsWith("@loader_path/", StringComparison.Ordinal))
            return import[13..];
        return import;
    }

    internal static bool IsSystemImport(string name, string rid)
    {
        if (rid.StartsWith("osx", StringComparison.Ordinal))
            return !name.Contains('\\') && name.Split('/').Skip(1).All(segment => segment.Length > 0 && segment is not ("." or "..")) &&
                (name.StartsWith("/usr/lib/", StringComparison.Ordinal) || name.StartsWith("/System/Library/Frameworks/", StringComparison.Ordinal));
        if (name.IndexOfAny(['/', '\\', ':']) >= 0) return false;
        if (rid.StartsWith("win", StringComparison.Ordinal))
            return WindowsSystem.Contains(name) || name.StartsWith("api-ms-win-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("ext-ms-win-", StringComparison.OrdinalIgnoreCase) ||
                OperatingSystem.IsWindows() && File.Exists(Path.Combine(Environment.SystemDirectory, name));
        return UnixSystem.Contains(name) || OperatingSystem.IsLinux() && SystemPath(name) != null;
    }

    internal static void Validate(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> images, string rid)
    {
        var comparer = rid.StartsWith("win", StringComparison.Ordinal) ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var imports = images.ToDictionary(pair => pair.Key, pair => WorkflowNativeImports.Read(pair.Value), comparer);
        foreach (var (_, table) in imports)
            foreach (var import in table.Dependencies)
            {
                var name = DependencyName(import, rid);
                if (imports.TryGetValue(name, out var dependency))
                {
                    if (rid.StartsWith("linux", StringComparison.Ordinal) && dependency.Identity != name)
                        throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, $"Native dependency '{name}' must declare that SONAME");
                }
                else if (!IsSystemImport(import, rid))
                    throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch, $"Native import '{import}' is not declared for runtime '{rid}'");
            }
    }

    internal static string? SystemPath(string name)
    {
        if (name.IndexOfAny(['/', '\\', ':']) >= 0) return null;
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.SystemDirectory, name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name : name + ".dll");
        // These images may exist only in dyld's shared cache, without files in /usr/lib.
        if (OperatingSystem.IsMacOS())
            return name is "libSystem" or "libSystem.B.dylib" or "libc" or "c" ? "/usr/lib/libSystem.B.dylib"
                : Path.Combine("/usr/lib", name.EndsWith(".dylib", StringComparison.Ordinal) ? name : name + ".dylib");
        var architecture = RuntimeInformation.ProcessArchitecture switch
        { Architecture.X64 => "x86_64", Architecture.Arm64 => "aarch64", Architecture.X86 => "i386", Architecture.Arm => "arm", _ => "" };
        var directories = OperatingSystem.IsMacOS() ? new[] { "/usr/lib" } :
            new[] { $"/lib/{architecture}-linux-gnu", $"/usr/lib/{architecture}-linux-gnu", "/lib64", "/usr/lib64", "/lib", "/usr/lib" };
        var candidates = name switch
        {
            "libc" or "c" => new[] { "libc.so.6", "libc.dylib" },
            "libSystem" => new[] { "libSystem.B.dylib" },
            _ => new[] { name, name + ".so", "lib" + name + ".so", name + ".dylib", "lib" + name + ".dylib" }
        };
        return directories.SelectMany(directory => candidates.Select(file => Path.Combine(directory, file))).FirstOrDefault(File.Exists);
    }

    internal static nint Load(string path) => NativeLibrary.Load(path, typeof(WorkflowNativeLibraries).Assembly,
        DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
}
