namespace IxIFlow.ActivitySdk;

/// <summary>Marks a property as an output of a workflow.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class WorkflowOutputAttribute : Attribute;
