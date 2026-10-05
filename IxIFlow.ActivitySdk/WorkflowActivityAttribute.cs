namespace IxIFlow.ActivitySdk;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class WorkflowActivityAttribute(string key) : Attribute
{
    public string Key { get; } = key;
}
