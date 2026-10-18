using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using IxIFlow.ActivitySdk;
using IxIFlow.Core;

namespace IxIFlow.DemoActivities;

[WorkflowActivity("demo.json.read", Name = "Read JSON file", Category = "Files", Icon = "file-input", Designer = "read-json")]
public sealed class ReadJsonActivity : IAsyncActivity
{
    [ActivityInput, Required, DefaultValue("orders/incoming.json"), Display(Name = "File path")]
    public string Path { get; set; } = string.Empty;

    [ActivityOutput]
    public string Content { get; private set; } = string.Empty;

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Path);
        Content = await File.ReadAllTextAsync(Path, cancellationToken);
    }
}
