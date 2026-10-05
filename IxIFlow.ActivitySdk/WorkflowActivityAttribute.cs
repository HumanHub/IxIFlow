namespace IxIFlow.ActivitySdk;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class WorkflowActivityAttribute(string key) : Attribute
{
    public string Key { get; } = key;
    public string Version { get; set; } = "1.0";
    public string? Name { get; set; }
    public string? Category { get; set; }
    public string? Designer { get; set; }
}
