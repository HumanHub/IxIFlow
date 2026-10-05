using IxIFlow.ActivitySdk;
using IxIFlow.Core;

namespace IxIFlow.DemoActivities;

[WorkflowActivity("demo.json.read", Name = "Read JSON file", Category = "Files", Designer = "read-json")]
public sealed class ReadJsonActivity : IAsyncActivity
{
    [WorkflowInput(Label = "File path", Required = true, Default = "orders/incoming.json")]
    public string Path { get; set; } = string.Empty;

    [WorkflowOutput]
    public string Content { get; private set; } = string.Empty;

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Path);
        Content = await File.ReadAllTextAsync(Path, cancellationToken);
    }
}
