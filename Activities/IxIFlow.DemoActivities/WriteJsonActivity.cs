using System.Text.Json;
using IxIFlow.ActivitySdk;
using IxIFlow.Core;

namespace IxIFlow.DemoActivities;

[WorkflowActivity("demo.json.write", Name = "Write JSON file", Category = "Files", Designer = "write-json")]
public sealed class WriteJsonActivity : IAsyncActivity
{
    [WorkflowInput(Label = "File path", Required = true, Default = "orders/result.json")]
    public string Path { get; set; } = string.Empty;

    [WorkflowInput(Label = "JSON value", Control = "code", Required = true)]
    public string Content { get; set; } = string.Empty;

    [WorkflowOutput]
    public string WrittenPath { get; private set; } = string.Empty;

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Path);
        using var _ = JsonDocument.Parse(Content);
        await File.WriteAllTextAsync(Path, Content, cancellationToken);
        WrittenPath = Path;
    }
}
