using System.Reflection;
using System.Runtime.CompilerServices;
using IxIFlow.Builders;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

/// <summary>Checks the build rules of a definition.</summary>
public static class WorkflowDefinitionValidator
{
    /// <summary>Validates executable shape and returns the compiler-independent structure hash.</summary>
    public static string Inspect(WorkflowDefinition definition)
    {
        Validate(definition);
        return ForExecution(definition).StructureHash;
    }

    public static string GetCombinerSignature(WorkflowDefinition definition)
    {
        Validate(definition);
        return BranchData.CombinerSignature(definition.WorkflowDataType);
    }

    public const int CheckpointContractVersion = 4;

    public static IReadOnlyList<WorkflowVersionReference> GetDependencies(WorkflowDefinition definition)
    {
        Validate(definition);
        return ForExecution(definition).AllSteps.Select(pair => pair.Value)
            .Where(step => step.WorkflowName != null && step.WorkflowVersion != null)
            .Select(step => new WorkflowVersionReference(step.WorkflowName!, step.WorkflowVersion!.Value))
            .Distinct().OrderBy(item => item.Name, StringComparer.Ordinal).ThenBy(item => item.Version).ToArray();
    }

    private static readonly ConditionalWeakTable<WorkflowDefinition, Lazy<Validation>> Validated = new();
    private static readonly ConditionalWeakTable<Type, string[]> UnrestorableProperties = new();

    public static void Validate(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Apply(definition);
        _ = ForExecution(definition);
    }

    internal static void Apply(WorkflowDefinition definition) =>
        _ = Validated.GetValue(definition, static value => new Lazy<Validation>(() => Checked(value))).Value;

    // Execution may consume a successful Apply, but must never run the build rules itself.
    internal static WorkflowScopeCatalog ForExecution(WorkflowDefinition definition)
    {
        if (!Validated.TryGetValue(definition, out var validation) || !validation.IsValueCreated)
            Check(() => throw WorkflowBuildError.Invalid(definition.FirstStep ?? new WorkflowStep(),
                "The workflow definition has not been validated; call WorkflowDefinitionValidator.Validate before execution"));
        return validation!.Value.Scopes.Value;
    }

    private static Validation Checked(WorkflowDefinition definition)
    {
        Check(() =>
        {
            if (string.IsNullOrWhiteSpace(definition.Name) || definition.Name.Length != definition.Name.Trim().Length)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                    "A workflow name is required and cannot have leading or trailing whitespace");
            if (definition.Version < 1)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                    $"Workflow '{definition.Name}' requires a positive version");
            foreach (var demand in definition.Demands)
            {
                WorkflowDemands.Validate(demand);
                ExpressionRules.CheckDemand(demand);
            }
            var steps = definition.Steps;
            foreach (var type in ParallelRules.Flatten(steps).SelectMany(step => new[]
                     { step.WorkflowDataType, step.PreviousStepDataType,
                         step.ResumeEventType, step.ReplyType, step.InvokedDataType, step.ResultType })
                         .Prepend(definition.WorkflowDataType).OfType<Type>().Distinct())
                _ = RedactingJson.ClassifiedPaths(type);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var step in ParallelRules.Flatten(steps))
                if (!ids.Add(step.Id))
                    throw WorkflowBuildError.Invalid(step, $"Duplicate step ID '{step.Id}'");
            ExpressionRules.Checked(MappingRules.Checked(ParallelRules.Checked(
                ForEachRules.Paired(SagaRules.Checked(RequestPairing.Paired(steps))))));
            foreach (var step in ParallelRules.Flatten(steps))
            {
                if (step.Runtime == null)
                    throw WorkflowBuildError.Invalid(step,
                        $"Step '{step.Id}' has no runtime delegates; construct steps through the workflow builders");
                if (step.StepMetadata.GetValueOrDefault("CompensationActivities") is List<CompensationActivityInfo> compensations &&
                    compensations.Any(compensation => compensation.Runtime == null))
                    throw WorkflowBuildError.Invalid(step,
                        $"Step '{step.Id}' has a compensation with no runtime delegates; construct steps through the workflow builders");
            }
        });
        return new Validation(definition);
    }

    // Preserve the execution compatibility checks at validation/registration/start, after Build's rules.
    // Lazy caches both the catalog and any compatibility failure, including for direct executor hosts.
    private sealed class Validation(WorkflowDefinition definition)
    {
        public Lazy<WorkflowScopeCatalog> Scopes { get; } = new(() =>
        {
            WorkflowScopeCatalog? catalog = null;
            Check(() =>
            {
                catalog = new WorkflowScopeCatalog(definition);
                ValidateSupportedSteps(catalog);
            });
            return catalog!;
        });
    }

    private static void ValidateSupportedSteps(WorkflowScopeCatalog catalog)
    {
        ValidateCompensations(catalog);
        foreach (var (id, step) in catalog.AllSteps)
        {
            var supported = step.StepType switch
            {
                WorkflowStepType.Activity or WorkflowStepType.WorkflowInvocation or WorkflowStepType.Sequence or
                    WorkflowStepType.Conditional or WorkflowStepType.Parallel or WorkflowStepType.Loop or
                    WorkflowStepType.TryCatch or WorkflowStepType.CatchBlock or WorkflowStepType.Saga or
                    WorkflowStepType.ForEach or WorkflowStepType.ForEachResult => true,
                WorkflowStepType.SuspendResume => step.StepMetadata.ContainsKey("WaitKey"),
                WorkflowStepType.Delay => step.DelayDuration > TimeSpan.Zero,
                _ => false
            };
            if (!supported)
                throw WorkflowBuildError.Invalid(step,
                    $"Structured execution does not support {step.StepType} at '{id}'");
            if (step.StepType == WorkflowStepType.SuspendResume &&
                ((step.WaitTimeout == null) != (step.TimeoutSteps.Count == 0) ||
                 step.WaitTimeout is { } timeout && timeout <= TimeSpan.Zero))
                throw WorkflowBuildError.Invalid(step,
                    $"Wait at '{id}' requires a positive timeout and a timeout branch");
            if (step.StepType == WorkflowStepType.WorkflowInvocation &&
                (string.IsNullOrWhiteSpace(step.WorkflowName) || step.WorkflowName.Length != step.WorkflowName.Trim().Length || step.WorkflowVersion is not > 0))
                throw WorkflowBuildError.Invalid(step,
                    $"Workflow invocation at '{id}' has no child workflow reference");
            if (step.StepMetadata.ContainsKey("OutcomeType") &&
                (step.OutcomeSelector == null || step.OutcomeBranches.Count == 0))
                throw WorkflowBuildError.Invalid(step,
                    $"Outcome step at '{id}' has no branches or selector");
            if (step.StepType == WorkflowStepType.Parallel &&
                step.ParallelJoinMode == ParallelJoinMode.WaitConditionally &&
                step.ParallelCompletionCondition == null)
                throw WorkflowBuildError.Invalid(step,
                    $"Conditional parallel join at '{id}' has no completion condition");
            if (step.StepType == WorkflowStepType.Activity && step.ActivityType != null)
            {
                var properties = UnrestorableProperties.GetValue(step.ActivityType, static type => type.GetProperties(
                        BindingFlags.Instance | BindingFlags.Public)
                    .Where(property => property.GetMethod != null && property.GetIndexParameters().Length == 0 &&
                        property.GetSetMethod(nonPublic: true) == null &&
                        !property.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), true)
                            .Cast<System.Text.Json.Serialization.JsonIgnoreAttribute>()
                            .Any(attribute => attribute.Condition == System.Text.Json.Serialization.JsonIgnoreCondition.Always))
                    .Select(property => property.Name).ToArray());
                if (properties.Length != 0)
                    throw WorkflowBuildError.Invalid(step,
                        $"Activity '{step.ActivityType.Name}' has a getter-only property '{properties[0]}' " +
                        "that cannot be restored from a checkpoint");
            }
        }
    }

    private static void ValidateCompensations(WorkflowScopeCatalog catalog)
    {
        foreach (var source in catalog.AllSteps.Select(pair => pair.Value).Where(step =>
                     step.StepType == WorkflowStepType.Activity && step.StepMetadata.ContainsKey("IsSagaStep")))
        {
            var activities = source.StepMetadata.GetValueOrDefault("CompensationActivities") as List<CompensationActivityInfo>;
            if (activities != null)
            {
                for (var index = 0; index < activities.Count; index++)
                {
                    var expected = activities[index].PreviousChainActivityType;
                    if (expected != null && (index == 0 ||
                            !expected.IsAssignableFrom(activities[index - 1].ActivityType)))
                        throw WorkflowBuildError.Invalid(source,
                            $"Compensation '{activities[index].ActivityType.Name}' requires a preceding " +
                            $"'{expected.Name}' compensation at '{catalog.Id(source)}'");
                }
            }
        }
    }

    private static void Check(Action check)
    {
        try
        {
            check();
        }
        catch (WorkflowEngineException error) when (WorkflowBuildError.Step(error)?.SourceLocation is { })
        {
            var step = WorkflowBuildError.Step(error)!;
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidDefinition,
                step.SourceLocation + ": " + error.Message, error);
        }
    }
}
