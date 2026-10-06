using System.Reflection;
using IxIFlow.Builders;

namespace IxIFlow.Core.Runtime;

internal sealed class WorkflowScopeCatalog
{
    private readonly Dictionary<string, IReadOnlyList<WorkflowStep>> _scopes = new();
    private readonly Dictionary<string, WorkflowStep> _steps = new();
    private readonly Dictionary<WorkflowStep, string> _ids = new();

    public WorkflowScopeCatalog(WorkflowDefinition definition)
    {
        AddScope("root", definition.Steps);
        ValidateSupportedSteps();
        Fingerprint = ComputeFingerprint();
    }

    public string Fingerprint { get; }
    public bool ContainsWait => _steps.Values.Any(step =>
        step.StepType is WorkflowStepType.SuspendResume or WorkflowStepType.Delay);

    public IReadOnlyCollection<PropertyInfo> FaultProperties(Type exceptionType) => _steps.Values
        .Where(step => step.StepType == WorkflowStepType.CatchBlock &&
            step.ExceptionType?.IsAssignableFrom(exceptionType) == true && step.FaultType != null)
        .SelectMany(step => FaultProjection.For(step.ExceptionType!, step.FaultType!).SourceProperties)
        .DistinctBy(FaultProjection.PropertyKey)
        .ToArray();

    public IReadOnlyList<WorkflowStep> Steps(string scopeId) => _scopes.TryGetValue(scopeId, out var steps)
        ? steps
        : throw new InvalidOperationException($"Scope '{scopeId}' was not found in the registered workflow definition");

    public WorkflowStep Step(string stepId) => _steps.TryGetValue(stepId, out var step)
        ? step
        : throw new InvalidOperationException($"Step '{stepId}' was not found in the registered workflow definition");

    public string Id(WorkflowStep step) => _ids[step];
    public string SequenceScope(WorkflowStep step) => $"{Id(step)}/sequence";
    public string ThenScope(WorkflowStep step) => $"{Id(step)}/then";
    public string ElseScope(WorkflowStep step) => $"{Id(step)}/else";
    public string LoopScope(WorkflowStep step) => $"{Id(step)}/loop";
    public string SagaScope(WorkflowStep step) => SequenceScope(step);
    public string CompensationScope(WorkflowStep saga, WorkflowStep source) =>
        $"{Id(saga)}/compensation/{Id(source)}";
    public string FinallyScope(WorkflowStep step) => $"{Id(step)}/finally";
    public string TimeoutScope(WorkflowStep step) => $"{Id(step)}/timeout";
    public string CatchBodyScope(WorkflowStep catchBlock) => SequenceScope(catchBlock);
    public string BranchScope(WorkflowStep step, int index) => $"{Id(step)}/branch/{index}";
    public string OutcomeScope(WorkflowStep step, int index) => $"{Id(step)}/outcome/{index}";

    private void AddScope(string id, IReadOnlyList<WorkflowStep> steps)
    {
        _scopes.Add(id, steps);
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            var stepId = $"{id}/{index}";
            _steps.Add(stepId, step);
            _ids.Add(step, stepId);
            AddScope(SequenceScope(step), step.SequenceSteps);
            AddScope(ThenScope(step), step.ThenSteps);
            AddScope(ElseScope(step), step.ElseSteps);
            AddScope(LoopScope(step), step.LoopBodySteps);
            AddScope(FinallyScope(step), step.FinallySteps);
            AddScope(TimeoutScope(step), step.TimeoutSteps);
            AddScope($"{stepId}/catch", step.CatchBlocks);
            if (step.StepType == WorkflowStepType.Saga)
                AddSagaCompensations(step);
            for (var branchIndex = 0; branchIndex < step.ParallelBranches.Count; branchIndex++)
                AddScope(BranchScope(step, branchIndex), step.ParallelBranches[branchIndex]);
            for (var outcomeIndex = 0; outcomeIndex < step.OutcomeBranches.Count; outcomeIndex++)
                AddScope(OutcomeScope(step, outcomeIndex), step.OutcomeBranches[outcomeIndex].Steps);
        }
    }

    private void AddSagaCompensations(WorkflowStep saga)
    {
        foreach (var source in SagaActivities(saga.SequenceSteps))
        {
            var activities = source.StepMetadata.TryGetValue("CompensationActivities", out var value)
                ? value as List<CompensationActivityInfo>
                : null;
            if (activities != null)
            {
                for (var index = 0; index < activities.Count; index++)
                {
                    var expected = activities[index].PreviousChainActivityType;
                    if (expected != null && (index == 0 ||
                            !expected.IsAssignableFrom(activities[index - 1].ActivityType)))
                        throw new InvalidOperationException(
                            $"Compensation '{activities[index].ActivityType.Name}' requires a preceding " +
                            $"'{expected.Name}' compensation at '{Id(source)}'");
                }
            }
            var steps = activities?.Select((activity, index) => new WorkflowStep
            {
                Id = $"{source.Id}/compensation/{index}",
                Name = activity.ActivityType.Name,
                StepType = WorkflowStepType.Activity,
                ActivityType = activity.ActivityType,
                WorkflowDataType = saga.WorkflowDataType,
                PreviousStepDataType = source.ActivityType,
                InputMappings = activity.InputMappings,
                OutputMappings = activity.OutputMappings,
                StepMetadata = new Dictionary<string, object>
                {
                    ["IsCompensationActivity"] = true
                }
            }).ToList() ?? [];
            AddScope(CompensationScope(saga, source), steps);
        }
    }

    private static IEnumerable<WorkflowStep> SagaActivities(IEnumerable<WorkflowStep> steps)
    {
        foreach (var step in steps)
        {
            if (step.StepType == WorkflowStepType.Activity &&
                step.StepMetadata.ContainsKey("IsSagaStep"))
                yield return step;
            foreach (var branch in step.OutcomeBranches)
                foreach (var nested in SagaActivities(branch.Steps))
                    yield return nested;
        }
    }

    private string ComputeFingerprint()
    {
        var signature = string.Join("\n", _steps.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair =>
            {
                var step = pair.Value;
                var waitKey = step.StepMetadata.TryGetValue("WaitKey", out var key) ? key?.ToString() : "";
                return string.Join('|', pair.Key, step.StepType,
                    step.Name, WorkflowTypeIdentity.StableName(step.WorkflowDataType),
                    WorkflowTypeIdentity.StableName(step.PreviousStepDataType),
                    WorkflowTypeIdentity.StableName(step.ActivityType),
                    WorkflowTypeIdentity.StableName(step.WorkflowType),
                    step.WorkflowName, step.WorkflowVersion,
                    WorkflowTypeIdentity.StableName(step.ResumeEventType), waitKey,
                    step.WaitTimeout?.Ticks, step.DelayDuration?.Ticks, step.ParallelJoinMode,
                    step.LoopType, WorkflowTypeIdentity.StableName(step.ExceptionType),
                    WorkflowTypeIdentity.StableName(step.FaultType),
                    step.ConditionExpressionSignature,
                    WorkflowCodeSignature.Of(step.CompiledCondition),
                    WorkflowCodeSignature.Of(step.OutcomeSelector),
                    WorkflowCodeSignature.Of(step.ParallelCompletionCondition),
                    string.Join(';', step.OutcomeBranches.Select(OutcomeBranchSignature)),
                    ErrorPolicySignature(step),
                    string.Join(';', step.InputMappings.Select(MappingSignature)),
                    string.Join(';', step.OutputMappings.Select(MappingSignature)));
            }));
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(signature));
        return Convert.ToHexString(bytes);
    }

    private static string MappingSignature(PropertyMapping mapping) => string.Join(':',
        mapping.Direction, mapping.TargetProperty,
        WorkflowTypeIdentity.StableName(mapping.SourceType),
        WorkflowTypeIdentity.StableName(mapping.TargetType),
        mapping.CompensationSource,
        mapping.ExpressionSignature,
        WorkflowCodeSignature.Of(mapping.SourceFunction),
        WorkflowCodeSignature.Of(mapping.TargetAssignmentFunction));

    private static string OutcomeBranchSignature(WorkflowOutcomeBranch branch) => string.Join(':',
        branch.IsDefault,
        WorkflowTypeIdentity.StableName(branch.Value?.GetType()),
        branch.Value == null ? "null" :
            System.Text.Json.JsonSerializer.Serialize(branch.Value, branch.Value.GetType()));

    private static string ErrorPolicySignature(WorkflowStep step)
    {
        var handlers = step.StepMetadata.TryGetValue("StepErrorHandlers", out var value) &&
            value is List<StepErrorHandlerInfo> configured
                ? string.Join(';', configured.Select(handler => string.Join(':',
                    WorkflowTypeIdentity.StableName(handler.ExceptionType), handler.HandlerAction,
                    RetrySignature(handler.RetryPolicy))))
                : "";
        var saga = step.StepMetadata.TryGetValue("SagaErrorConfig", out value) &&
            value is SagaErrorConfiguration policy
                ? string.Join(':', policy.CompensationStrategy, policy.ContinuationAction,
                    WorkflowTypeIdentity.StableName(policy.CompensationTargetType),
                    RetrySignature(policy.RetryPolicy))
                : "";
        return handlers + "|" + saga;
    }

    private static string RetrySignature(RetryPolicy? policy) => policy == null
        ? ""
        : string.Join(':', policy.MaximumAttempts, policy.InitialInterval.Ticks,
            policy.MaximumInterval.Ticks,
            policy.BackoffCoefficient.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private void ValidateSupportedSteps()
    {
        foreach (var (id, step) in _steps)
        {
            var supported = step.StepType switch
            {
                WorkflowStepType.Activity or WorkflowStepType.WorkflowInvocation or WorkflowStepType.Sequence or
                    WorkflowStepType.Conditional or WorkflowStepType.Parallel or WorkflowStepType.Loop or
                    WorkflowStepType.TryCatch or WorkflowStepType.CatchBlock or WorkflowStepType.Saga => true,
                WorkflowStepType.SuspendResume => step.StepMetadata.ContainsKey("WaitKey"),
                WorkflowStepType.Delay => step.DelayDuration > TimeSpan.Zero,
                _ => false
            };
            if (!supported)
                throw new NotSupportedException(
                    $"Structured execution does not support {step.StepType} at '{id}'");
            if (step.StepType == WorkflowStepType.SuspendResume &&
                ((step.WaitTimeout == null) != (step.TimeoutSteps.Count == 0) ||
                 step.WaitTimeout is { } timeout && timeout <= TimeSpan.Zero))
                throw new InvalidOperationException(
                    $"Wait at '{id}' requires a positive timeout and a timeout branch");
            if (step.StepType == WorkflowStepType.WorkflowInvocation &&
                step.WorkflowType == null &&
                (string.IsNullOrWhiteSpace(step.WorkflowName) || step.WorkflowVersion == null))
                throw new InvalidOperationException($"Workflow invocation at '{id}' has no child workflow reference");
            if (step.StepMetadata.ContainsKey("OutcomeType") &&
                (step.OutcomeSelector == null || step.OutcomeBranches.Count == 0))
                throw new InvalidOperationException($"Outcome step at '{id}' has no branches or selector");
            if (step.StepType == WorkflowStepType.Parallel &&
                step.ParallelJoinMode == ParallelJoinMode.WaitConditionally &&
                step.ParallelCompletionCondition == null)
                throw new InvalidOperationException($"Conditional parallel join at '{id}' has no completion condition");
            if (step.StepType == WorkflowStepType.Activity && step.ActivityType != null)
            {
                foreach (var property in step.ActivityType.GetProperties(
                             System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
                {
                    if (property.GetMethod == null || property.GetIndexParameters().Length != 0 ||
                        property.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), true)
                            .Cast<System.Text.Json.Serialization.JsonIgnoreAttribute>()
                            .Any(attribute => attribute.Condition ==
                                System.Text.Json.Serialization.JsonIgnoreCondition.Always))
                        continue;
                    if (property.GetSetMethod(nonPublic: true) == null)
                        throw new NotSupportedException(
                            $"Activity '{step.ActivityType.Name}' has a getter-only property '{property.Name}' " +
                            "that cannot be restored from a checkpoint");
                }
            }
        }
    }
}
