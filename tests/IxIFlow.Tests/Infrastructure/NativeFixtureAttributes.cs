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
