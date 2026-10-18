using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using IxIFlow.ActivitySdk;
using IxIFlow.Core;

namespace IxIFlow.DemoActivities;

[WorkflowActivity("demo.json.write", Name = "Write JSON file", Category = "Files", Icon = "file-output", Designer = "write-json")]
public sealed class WriteJsonActivity : IAsyncActivity
{
    [ActivityInput, Required, DefaultValue("orders/result.json"), Display(Name = "File path")]
    public string Path { get; set; } = string.Empty;

    [ActivityInput, UIHint("code"), Required, Display(Name = "JSON value")]
    public string Content { get; set; } = string.Empty;

    [ActivityOutput]
    public string WrittenPath { get; private set; } = string.Empty;

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Path);
        using var _ = JsonDocument.Parse(Content);
        await File.WriteAllTextAsync(Path, Content, cancellationToken);
        WrittenPath = Path;
    }
}
