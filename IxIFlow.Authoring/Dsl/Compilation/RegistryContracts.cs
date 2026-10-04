using IxIFlow.Core;

namespace IxIFlow.Dsl.Compilation;

public interface IActivityRegistry
{
    ValueTask<ActivityDescriptor?> FindAsync(string activityKey, CancellationToken cancellationToken = default);
}

public interface IWorkflowCatalog
{
    ValueTask<WorkflowDescriptor?> FindAsync(string workflowName, int? version, CancellationToken cancellationToken = default);
}

public interface IEventRegistry
{
    ValueTask<EventDescriptor?> FindAsync(string eventKey, CancellationToken cancellationToken = default);
}

public interface IDataTypeRegistry
{
    ValueTask<DataTypeDescriptor?> FindAsync(string typeKey, CancellationToken cancellationToken = default);
}

public interface IStepTemplateRegistry
{
    ValueTask<StepTemplateDescriptor?> FindAsync(string templateKey, CancellationToken cancellationToken = default);
}

public sealed record ActivityDescriptor
{
    public string Key { get; init; } = string.Empty;
    public Type ActivityType { get; init; } = typeof(object);
}

public sealed record WorkflowDescriptor
{
    public string Name { get; init; } = string.Empty;
    public int Version { get; init; }
    public WorkflowDefinition? RuntimeDefinition { get; init; }
}

public sealed record EventDescriptor
{
    public string Key { get; init; } = string.Empty;
    public Type EventType { get; init; } = typeof(object);
}

public sealed record DataTypeDescriptor
{
    public string Key { get; init; } = string.Empty;
    public Type ClrType { get; init; } = typeof(object);
}

public sealed record StepTemplateDescriptor
{
    public string Key { get; init; } = string.Empty;
}
