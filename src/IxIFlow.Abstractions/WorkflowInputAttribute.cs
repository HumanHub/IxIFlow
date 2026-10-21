namespace IxIFlow.ActivitySdk;

/// <summary>Marks a property as an input of a workflow.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class WorkflowInputAttribute : Attribute;
