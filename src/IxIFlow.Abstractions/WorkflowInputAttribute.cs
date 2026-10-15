namespace IxIFlow.ActivitySdk;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class WorkflowInputAttribute : Attribute
{
    public string? Label { get; set; }
    public string Control { get; set; } = "text";
    public bool Required { get; set; }
    public string? Help { get; set; }
    public string? Default { get; set; }
}
