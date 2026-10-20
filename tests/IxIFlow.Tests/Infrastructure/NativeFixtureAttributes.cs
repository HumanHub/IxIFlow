using System.Runtime.InteropServices;

namespace IxIFlow.Tests.Infrastructure;

public sealed class NativeFactAttribute : FactAttribute
{
    public NativeFactAttribute()
    {
        if (RuntimeInformation.RuntimeIdentifier is not ("win-x64" or "linux-x64"))
            Skip = "Prebuilt native fixtures are available for win-x64 and linux-x64; this RID is " + RuntimeInformation.RuntimeIdentifier;
    }
}

public sealed class NativeTheoryAttribute : TheoryAttribute
{
    public NativeTheoryAttribute()
    {
        if (RuntimeInformation.RuntimeIdentifier is not ("win-x64" or "linux-x64"))
            Skip = "Prebuilt native fixtures are available for win-x64 and linux-x64; this RID is " + RuntimeInformation.RuntimeIdentifier;
    }
}

public sealed class LinuxNativeFactAttribute : FactAttribute
{
    public LinuxNativeFactAttribute()
    {
        if (RuntimeInformation.RuntimeIdentifier != "linux-x64")
            Skip = "The verified-descriptor and Unix link tests require linux-x64";
    }
}

public sealed class PosixNativeFactAttribute : FactAttribute
{
    public PosixNativeFactAttribute()
    {
        if (RuntimeInformation.RuntimeIdentifier is not ("linux-x64" or "osx-arm64"))
            Skip = "Private-copy unlink and cleanup tests require the linux-x64 or osx-arm64 fixture; Windows uses no private copies";
    }
}
