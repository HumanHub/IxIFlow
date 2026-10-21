namespace IxIFlow.ActivitySdk;

/// <summary>Marks a property as both an input and an output of a workflow.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class WorkflowInputOutputAttribute : Attribute;
