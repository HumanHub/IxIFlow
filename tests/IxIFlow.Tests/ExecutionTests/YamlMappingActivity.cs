using IxIFlow.Core;

namespace IxIFlow.Tests.ExecutionTests;

public sealed class YamlMappingActivity : IAsyncActivity
{
    public string Value { get; set; } = "";
    public string Result { get; set; } = "";
    public bool Flag { get; set; }
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    { Result = Value; return Task.CompletedTask; }
}
