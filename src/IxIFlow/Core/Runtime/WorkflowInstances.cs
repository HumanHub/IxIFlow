using System.Text.Json;

namespace IxIFlow.Core.Runtime;

internal static class WorkflowInstances
{
    /// <summary>
    /// A new instance of <paramref name="definition"/> with <paramref name="workflowData"/>, as a start
    /// saves it, and its initial checkpoint: a client's start, or a child a parent's commit starts.
    /// </summary>
    public static (WorkflowInstance Instance, ExecutionCheckpoint Checkpoint, WorkflowScopeCatalog Scopes)
        NewInstance(WorkflowDefinition definition, object workflowData, WorkflowOptions options, DateTime now)
    {
        WorkflowDefinitionValidator.Validate(definition);
        var instance = new WorkflowInstance
        {
            InstanceId = options.InstanceId ?? Guid.NewGuid().ToString("N"),
            WorkflowName = definition.Name,
            WorkflowVersion = definition.Version,
            WorkflowDataType = workflowData.GetType().AssemblyQualifiedName!,
            WorkflowDataJson = BranchData.Snapshot(workflowData),
            Status = WorkflowStatus.Running,
            CorrelationId = options.CorrelationId ?? Guid.NewGuid().ToString("N"),
            ParentInstanceId = options.ParentInstanceId,
            CreatedAt = now,
            StartedAt = now,
            TracingEnabled = options.EnableTracing,
            Properties = options.Properties.ToDictionary(pair => pair.Key, pair => JsonSerializer.SerializeToElement(pair.Value))
        };
        var scopes = WorkflowDefinitionValidator.ForExecution(definition);
        var checkpoint = new ExecutionCheckpoint
        {
            StructureHash = scopes.StructureHash,
            // The store stamps the execution deadline when the start is committed (D-35).
            ExecutionTimeout = options.ExecutionTimeout,
            ExecutionTimerId = options.ExecutionTimeout != null ? Guid.NewGuid().ToString("N") : null,
            ExecutionDeadlineUtc = instance.StartedAt + options.ExecutionTimeout,
            Continuations =
            [
                new ContinuationState
                {
                    Stack = [new ScopePosition { ScopeId = "root" }]
                }
            ]
        };
        return (instance, checkpoint, scopes);
    }

    /// <summary>
    /// The start of a child: its initial instance, as a client start would save it, for the parent's
    /// commit to carry. The host validates the definition and checkpoint size before accepting it.
    /// </summary>
    public static ChildStart Start(WorkflowDefinition definition, object data, WorkflowInstance parent, string childId,
        string parentWaitId, DateTime now)
    {
        var (child, checkpoint, scopes) = WorkflowInstances.NewInstance(definition, data, new WorkflowOptions
        {
            InstanceId = childId,
            CorrelationId = parent.CorrelationId,
            ParentInstanceId = parent.InstanceId
        }, now);
        child.ParentWaitId = parentWaitId;
        ExecutionCheckpointWriter.Prepare(child, checkpoint, data, scopes, now);
        // The store saves a child's start whole, as one instance with no parts.
        child.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
        return new ChildStart(childId, JsonSerializer.Serialize(child), parentWaitId);
    }
}
