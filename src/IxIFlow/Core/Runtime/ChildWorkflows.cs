using System.Reflection;
using System.Text.Json;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// The completion a child's terminal commit appends to its parent's inbox, addressed to the parent's wait
/// for it: how the child ended, its data as saved, and its error when it failed.
/// </summary>
internal sealed class ChildCompleted
{
    public static string EventType { get; } = WorkflowTypeIdentity.StableName(typeof(ChildCompleted));

    public string ChildInstanceId { get; set; } = "";
    public WorkflowStatus Status { get; set; }
    public string DataJson { get; set; } = "";
    public SerializedException? Error { get; set; }

    /// <summary>The notice a child's terminal commit carries, or null for an instance with no parent waiting.</summary>
    public static ParentNotice? NoticeOf(WorkflowInstance instance, ExecutionCheckpoint checkpoint) =>
        instance is { ParentInstanceId: { } parent, ParentWaitId: { } wait } &&
        WorkflowStoreRules.IsTerminal(instance.Status)
            ? new ParentNotice(parent, wait, $"child-completed-{instance.InstanceId}", EventType,
                JsonSerializer.Serialize(new ChildCompleted
                {
                    ChildInstanceId = instance.InstanceId,
                    Status = instance.Status,
                    DataJson = instance.WorkflowDataJson,
                    Error = instance.Status == WorkflowStatus.Failed ? checkpoint.UnhandledError : null
                }))
            : null;

    /// <summary>
    /// The outcome the parent's invocation step takes from it: the child's data, read as
    /// <paramref name="dataType"/>, when it completed; its own error when it failed; and
    /// <see cref="ChildWorkflowEndedException"/> when it was cancelled, timed out or terminated.
    /// </summary>
    public ActivityRunResult Outcome(Type dataType) => Status switch
    {
        WorkflowStatus.Completed => ActivityRunResult.Returned(JsonSerializer.Deserialize(DataJson, dataType)),
        WorkflowStatus.Failed => ActivityRunResult.Threw(Error?.ForPropagation()
            ?? new InvalidOperationException($"Child workflow '{ChildInstanceId}' failed")),
        _ => ActivityRunResult.Threw(new ChildWorkflowEndedException(ChildInstanceId, Status))
    };
}

/// <summary>
/// Starts child workflows as instances of their own: resolves the child's definition, registered
/// so any host can run it, and builds its initial instance the way a client start does.
/// </summary>
internal sealed class ChildWorkflows(IServiceProvider services, IWorkflowVersionRegistry registry, TimeProvider time)
{
    /// <summary>
    /// The definition an invocation step invokes. To start a child it must be active, as for a client's start,
    /// and already registered under its name and version, so any host can run the child.
    /// </summary>
    public async Task<WorkflowDefinition> ResolveAsync(WorkflowStep step, bool starting)
    {
        WorkflowDefinition definition;
        if (step.WorkflowType != null)
        {
            definition = await ResolveRegisteredTypeAsync(step);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(step.WorkflowName) || step.WorkflowVersion == null)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                    $"Workflow reference is incomplete for step {step.Id}");
            definition = await registry.GetWorkflowDefinitionAsync(step.WorkflowName, step.WorkflowVersion.Value)
                         ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                             $"Workflow definition not found: {step.WorkflowName} v{step.WorkflowVersion}");
        }
        if (definition.Name != step.WorkflowName || definition.Version != step.WorkflowVersion)
            throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                $"Workflow '{step.WorkflowName}' v{step.WorkflowVersion} resolved to " +
                $"'{definition.Name}' v{definition.Version}");
        if (definition.WorkflowDataType == null)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"Workflow {definition.Name} has no data type");
        if (starting && definition.Metadata.TryGetValue("IsActive", out var isActive) && isActive is false)
            throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                $"Workflow {definition.Name} v{definition.Version} is deactivated");
        _ = WorkflowDefinitionValidator.ForExecution(definition);
        return definition;
    }

    private async Task<WorkflowDefinition> ResolveRegisteredTypeAsync(WorkflowStep step)
    {
        var definition = step.Runtime.ChildDefinition!(services);
        if (definition.Name != step.WorkflowName || definition.Version != step.WorkflowVersion)
            throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                $"Workflow type '{step.WorkflowType}' is registered as '{definition.Name}' v{definition.Version}, " +
                $"but the invocation requires '{step.WorkflowName}' v{step.WorkflowVersion}");
        var published = await registry.GetWorkflowDefinitionAsync(step.WorkflowName!, step.WorkflowVersion!.Value)
            ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                $"Workflow definition not found: {definition.Name} v{definition.Version}");
        var registeredHash = await registry.GetWorkflowStructureHashAsync(step.WorkflowName!, step.WorkflowVersion!.Value)
            ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                $"Registered structure hash not found: {definition.Name} v{definition.Version}");
        var loadedHash = WorkflowDefinitionValidator.ForExecution(definition).StructureHash;
        if (registeredHash != loadedHash)
            throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                $"Workflow {definition.Name} v{definition.Version} differs from its registered definition: " +
                $"registered structure hash '{registeredHash}', loaded structure hash '{loadedHash}'");
        return published;
    }

    /// <summary>
    /// The start of a child: its initial instance, as a client start would save it, for the parent's
    /// commit to carry. A child whose initial checkpoint is over <paramref name="limits"/> is refused with
    /// <see cref="CheckpointLimitExceededException"/>.
    /// </summary>
    public ChildStart Start(WorkflowDefinition definition, object data, WorkflowInstance parent, string childId,
        string parentWaitId, ExecutionLimits limits)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var (child, checkpoint, scopes) = WorkflowHost.InstanceOperations.NewInstance(definition, data, new WorkflowOptions
        {
            InstanceId = childId,
            CorrelationId = parent.CorrelationId,
            ParentInstanceId = parent.InstanceId,
            Demands = WorkflowDemands.Saved(parent).Where(demand => demand.Inherit).ToList()
        }, now);
        child.ParentWaitId = parentWaitId;
        var size = ExecutionCheckpointWriter.Prepare(child, checkpoint, data, scopes, now).Bytes;
        if (size > limits.MaxCheckpointBytes)
            throw new CheckpointLimitExceededException(size, limits.MaxCheckpointBytes);
        // The store saves a child's start whole, as one instance with no parts.
        child.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
        return new ChildStart(childId, JsonSerializer.Serialize(child), parentWaitId);
    }
}

/// <summary>The data a child invocation starts its child with.</summary>
internal static class ChildInputs
{
    /// <summary>
    /// New data for a child of <paramref name="definition"/>, with the invocation's input mappings applied
    /// from the parent's <paramref name="context"/>. An input the child's data cannot take is an invalid definition,
    /// never skipped.
    /// </summary>
    public static object NewData(WorkflowDefinition definition, WorkflowStep step, object context)
    {
        var data = Activator.CreateInstance(definition.WorkflowDataType!)
                   ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                       $"Failed to create instance of {definition.WorkflowDataType}");
        foreach (var mapping in step.InputMappings.Where(item => item.Direction == PropertyMappingDirection.Input))
        {
            var target = data.GetType().GetProperty(mapping.TargetProperty);
            if (target is not { CanWrite: true })
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                    $"Child workflow input '{data.GetType().Name}.{mapping.TargetProperty}' is " +
                    (target == null ? "not a property of its data" : "not writable"));
            target.SetValue(data, Convert(mapping.SourceFunction(context), target, data.GetType()));
        }
        return data;
    }

    private static object? Convert(object? value, PropertyInfo target, Type dataType)
    {
        if (value == null || target.PropertyType.IsInstanceOfType(value))
            return value;
        var underlying = Nullable.GetUnderlyingType(target.PropertyType) ?? target.PropertyType;
        try
        {
            // A whole number takes an enum property as reflection assigns it.
            return underlying.IsEnum && Type.GetTypeCode(value.GetType()) is >= TypeCode.SByte and <= TypeCode.UInt64
                ? Enum.ToObject(underlying, value)
                : System.Convert.ChangeType(value, underlying);
        }
        catch (Exception error) when (error is InvalidCastException or FormatException or OverflowException)
        {
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"Child workflow input '{dataType.Name}.{target.Name}' takes {target.PropertyType.Name}, " +
                $"not {value.GetType().Name}", error);
        }
    }
}
