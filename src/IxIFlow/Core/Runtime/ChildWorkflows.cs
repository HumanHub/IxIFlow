using System.Reflection;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// The completion a child's terminal commit appends to its parent's inbox, addressed to the parent's wait
/// for it (D-17): how the child ended, its data as saved, and its error when it failed.
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
        instance.Status is WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.Cancelled or
            WorkflowStatus.Terminated or WorkflowStatus.TimedOut
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
/// Starts child workflows as instances of their own (D-17): resolves the child's definition, registered
/// so any host can run it, and builds its initial instance the way a client start does.
/// </summary>
internal sealed class ChildWorkflows(IServiceProvider services, IWorkflowVersionRegistry registry, TimeProvider time)
{
    /// <summary>
    /// The definition an invocation step invokes. To start a child it must be active, as for a client's start,
    /// and it is registered under its name and version, so any host can run the child.
    /// </summary>
    public async Task<WorkflowDefinition> ResolveAsync(WorkflowStep step, bool starting)
    {
        WorkflowDefinition definition;
        if (step.WorkflowType != null)
        {
            var registration = typeof(RegisteredWorkflowDefinition<>).MakeGenericType(step.WorkflowType);
            definition = (services.GetService(registration) as IRegisteredWorkflowDefinition)?.Definition
                         ?? await BuildAsync(step);
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
        if (definition.WorkflowDataType == null)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"Workflow {definition.Name} has no data type");
        if (!starting)
            return definition;
        var registered = await registry.GetWorkflowDefinitionAsync(definition.Name, definition.Version);
        if (registered?.Metadata.TryGetValue("IsActive", out var isActive) == true && isActive is false)
            throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                $"Workflow {definition.Name} v{definition.Version} is deactivated");
        await registry.RegisterWorkflowAsync(definition);
        return definition;
    }

    // A workflow class with no registration is built from the class, and must match what is registered
    // under its name and version, if anything is.
    private async Task<WorkflowDefinition> BuildAsync(WorkflowStep step)
    {
        var workflowType = step.WorkflowType!;
        var contract = workflowType.GetInterfaces().FirstOrDefault(item =>
                           item.IsGenericType && item.GetGenericTypeDefinition() == typeof(IWorkflow<>))
                       ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                           $"Workflow type {workflowType} does not implement IWorkflow<>");
        var dataType = contract.GetGenericArguments()[0];
        var workflow = ActivatorUtilities.GetServiceOrCreateInstance(services, workflowType);
        var version = (int)(contract.GetProperty("Version")!.GetValue(workflow)
                            ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                                $"Workflow {workflowType} has no version"));
        var builderType = typeof(WorkflowBuilder<>).MakeGenericType(dataType);
        var builder = Activator.CreateInstance(builderType, step.WorkflowName ?? workflowType.Name,
            step.WorkflowVersion ?? version);
        (workflowType.GetMethod("Build") ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"Workflow {workflowType} does not have a Build method"))
            .Invoke(workflow, [builder]);
        var definition = (WorkflowDefinition)builderType.GetMethod("Build",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(builder, null)!;
        var published = await registry.GetWorkflowDefinitionAsync(definition.Name, definition.Version);
        if (published == null)
            return definition;
        if (new WorkflowScopeCatalog(published).Fingerprint != new WorkflowScopeCatalog(definition).Fingerprint)
            throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                $"Workflow {definition.Name} v{definition.Version} differs from its registered definition");
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
        var (child, checkpoint, scopes) = InstanceHost.NewInstance(definition, data, new WorkflowOptions
        {
            InstanceId = childId,
            CorrelationId = parent.CorrelationId,
            ParentInstanceId = parent.InstanceId
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

/// <summary>The data a child invocation starts its child with (D-17).</summary>
internal static class ChildInputs
{
    /// <summary>
    /// New data for a child of <paramref name="definition"/>, with the invocation's input mappings applied
    /// from the parent's <paramref name="context"/>.
    /// </summary>
    public static object NewData(WorkflowDefinition definition, WorkflowStep step, object context)
    {
        var data = Activator.CreateInstance(definition.WorkflowDataType!)
                   ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                       $"Failed to create instance of {definition.WorkflowDataType}");
        foreach (var mapping in step.InputMappings.Where(item => item.Direction == PropertyMappingDirection.Input))
        {
            var target = data.GetType().GetProperty(mapping.TargetProperty);
            if (target is { CanWrite: true })
                target.SetValue(data, Convert(mapping.SourceFunction(context), target.PropertyType));
        }
        return data;
    }

    private static object? Convert(object? value, Type targetType)
    {
        if (value == null || targetType.IsInstanceOfType(value))
            return value;
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        try
        {
            return System.Convert.ChangeType(value, underlying);
        }
        catch (Exception error) when (error is InvalidCastException or FormatException or OverflowException)
        {
            return value;
        }
    }
}
