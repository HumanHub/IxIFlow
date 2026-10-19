using IxIFlow.ActivitySdk;
using IxIFlow.Core;

namespace IxIFlow.Tests.ExecutionTests.StructureHashFixtures;

public sealed class StructureHashData
{
    public int Value { get; set; }
    public int Result { get; set; }
}

public sealed class StructureHashAnswer;

public sealed class StructureHashMultiply : IAsyncActivity
{
    [ActivityInput]
    public int Value { get; set; }
    [ActivityOutput]
    public int Result { get; private set; }
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        Result = Value * 2;
        return Task.CompletedTask;
    }
}
