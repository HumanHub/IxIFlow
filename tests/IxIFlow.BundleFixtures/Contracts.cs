namespace IxIFlow.BundleFixtures;

public sealed class BundleException : Exception;

public sealed class FirstData
{
    public string Value { get; set; } = "";
    public string Result { get; set; } = "";
    public Dictionary<string, List<int[]>> Items { get; set; } = [];
}

public sealed class SecondData
{
    public int Value { get; set; }
    public string Result { get; set; } = "";
}

public sealed class Signal
{
    public string Value { get; set; } = "";
}
