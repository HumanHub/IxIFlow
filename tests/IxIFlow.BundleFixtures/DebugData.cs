using IxIFlow.Core;

namespace IxIFlow.BundleFixtures;

public sealed class DebugData
{
    public string Value { get; set; } = "";
    public string Result { get; set; } = "";
    [Sensitive]
    public string Secret { get; set; } = "";
}
