using IxIFlow.Core;
using IxIFlow.ActivitySdk;

namespace IxIFlow.BundleFixtures;

public sealed class BundleException : Exception;

public sealed class ChildFailure(string message) : Exception(message);
public sealed record MessageFault(string Message);

public sealed class Fail : IAsyncActivity
{
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
        throw new ChildFailure("published child failed");
}

public sealed class Echo : IAsyncActivity
{
    [ActivityInput, ActivityOutput]
    public string Value { get; set; } = "parent previous";
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

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
