namespace IxIFlow.Tests;

/// <summary>Reports native Windows callback coverage as skipped on unsupported platforms.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "This test requires the Windows native message pump";
    }
}