using JsonSerializer = IxIFlow.Core.Runtime.WorkflowJson;
using System.Reflection;
using System.Text.Json;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// The completion a child's terminal commit appends to its parent's inbox, addressed to the parent's wait
/// for it: how the child ended, its data as saved, and its error when it failed.
/// </summary>
internal sealed class ChildCompleted
{
    public static string EventType { get; } = WorkflowTypeIdentity.SavedName(typeof(ChildCompleted));

    public string ChildInstanceId { get; set; } = "";
    public WorkflowStatus Status { get; set; }
    public StoredJson DataJson { get; set; } = StoredJson.Inline("");
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
                    DataJson = instance.Status != WorkflowStatus.Completed ? StoredJson.Inline("")
                        : instance.WorkflowDataJson,
                    Error = instance.Status == WorkflowStatus.Failed ? checkpoint.UnhandledError : null
                })) { PayloadDependencies = instance.Status == WorkflowStatus.Completed && instance.WorkflowDataJson.Reference is { } reference ? [reference] : [] }
            : null;

    /// <summary>
    /// The outcome the parent's invocation step takes from it: the child's data, read as
    /// <paramref name="dataType"/>, when it completed; its own error when it failed; and
    /// <see cref="ChildWorkflowEndedException"/> when it was cancelled, timed out or terminated.
    /// </summary>
    public ActivityRunResult Outcome(Type dataType) => Status switch
    {
        WorkflowStatus.Completed => ActivityRunResult.Returned(JsonSerializer.Deserialize(DataJson.InlineText, dataType)),
        WorkflowStatus.Failed => ActivityRunResult.Threw(Error?.ForPropagation()
            ?? new InvalidOperationException($"Child workflow '{ChildInstanceId}' failed")),
        _ => ActivityRunResult.Threw(new ChildWorkflowEndedException(ChildInstanceId, Status))
    };
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
        var contract = WorkflowDefinitionValidator.GetContract(definition);
        var assigned = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var member in contract.Inputs.Where(member => member.HasDefault &&
                     !step.InputMappings.Any(mapping => mapping.Direction == PropertyMappingDirection.Input && mapping.TargetProperty == member.Name)))
        {
            var target = WorkflowContract.FindProperty(data.GetType(), member.Name) ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                $"Child workflow input '{data.GetType().Name}.{member.Name}' is not a property of its data");
            var value = WorkflowJson.Deserialize(member.DefaultJson!, target.PropertyType);
            target.SetValue(data, value);
            assigned[member.Name] = value;
        }
        foreach (var mapping in step.InputMappings.Where(item => item.Direction == PropertyMappingDirection.Input))
        {
            var target = WorkflowContract.FindProperty(data.GetType(), mapping.TargetProperty);
            if (target is not { CanWrite: true })
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                    $"Child workflow input '{data.GetType().Name}.{mapping.TargetProperty}' is " +
                    (target == null ? "not a property of its data" : "not writable"));
            var value = Convert(mapping.SourceFunction(context), target, data.GetType());
            target.SetValue(data, value);
            assigned[target.Name] = value;
        }
        try { contract.ValidateRequired(data, data.GetType(), assigned); }
        catch (WorkflowEngineException error) when (error.Kind == WorkflowErrorKind.InvalidRequest)
        { throw WorkflowEngineException.FromWorkflowCode(error); }
        return data;
    }

    internal static void CheckContract(WorkflowDefinition definition, WorkflowStep step, WorkflowContract contract)
    {
        if (step.InvokedContractKind == WorkflowInvocationContractKind.ExactPin)
        {
            if (contract.Hash != step.InvokedContractHash)
                throw new WorkflowEngineException(WorkflowErrorKind.ArtifactMismatch,
                    $"Workflow {definition.Name} v{definition.Version} contract hash differs: resolved '{contract.Hash}', invoked '{step.InvokedContractHash}'");
            return;
        }
        foreach (var mapping in step.InputMappings.Where(mapping => mapping.Direction == PropertyMappingDirection.Input))
        {
            if (WorkflowContract.FindProperty(definition.WorkflowDataType!, mapping.TargetProperty) == null)
                throw InvalidInput(mapping.TargetProperty, "is not a property of its data");
            if (!contract.Inputs.Any(member => member.Name == mapping.TargetProperty))
                throw InvalidInput(mapping.TargetProperty, "is not an input in its workflow contract");
        }
        foreach (var mapping in step.OutputMappings.Where(mapping => mapping.Direction == PropertyMappingDirection.Output))
            if (!contract.Outputs.Any(member => member.Name == mapping.TargetProperty))
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                    $"Child workflow output '{definition.WorkflowDataType!.Name}.{mapping.TargetProperty}' is not an output in its workflow contract");
        foreach (var member in contract.Inputs.Where(member => member.Required && !member.HasDefault))
            if (!step.InputMappings.Any(mapping => mapping.Direction == PropertyMappingDirection.Input && mapping.TargetProperty == member.Name))
                throw InvalidInput(member.Name, "is a required input in its workflow contract and is not mapped");

        WorkflowEngineException InvalidInput(string member, string reason) => new(WorkflowErrorKind.InvalidDefinition,
            $"Child workflow input '{definition.WorkflowDataType!.Name}.{member}' {reason}");
    }

    private static object? Convert(object? value, PropertyInfo target, Type dataType)
    {
        if (value == null || target.PropertyType.IsInstanceOfType(value))
            return value;
        var underlying = Nullable.GetUnderlyingType(target.PropertyType) ?? target.PropertyType;
        try
        {
            if (!typeof(IConvertible).IsAssignableFrom(underlying))
                return JsonSerializer.Deserialize(JsonSerializer.Serialize(value), target.PropertyType);
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
