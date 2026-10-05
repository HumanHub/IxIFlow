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
    public string BranchScope(WorkflowStep step, int index) => $"{Id(step)}/branch/{index}";

    public static bool RequiresStructuredExecution(IReadOnlyList<WorkflowStep> steps)
    {
        foreach (var step in steps)
        {
            if (step.StepType == WorkflowStepType.SuspendResume && step.StepMetadata.ContainsKey("WaitKey"))
                return true;
            if (step.StepType == WorkflowStepType.Parallel && step.ParallelJoinMode != ParallelJoinMode.WaitAll)
                return true;
            if (RequiresStructuredExecution(step.SequenceSteps) || RequiresStructuredExecution(step.ThenSteps) ||
                RequiresStructuredExecution(step.ElseSteps) || RequiresStructuredExecution(step.LoopBodySteps) ||
                RequiresStructuredExecution(step.FinallySteps) || RequiresStructuredExecution(step.CatchBlocks) ||
                step.ParallelBranches.Any(RequiresStructuredExecution))
                return true;
        }
        return false;
    }

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
            AddScope($"{stepId}/finally", step.FinallySteps);
            AddScope($"{stepId}/catch", step.CatchBlocks);
            for (var branchIndex = 0; branchIndex < step.ParallelBranches.Count; branchIndex++)
                AddScope(BranchScope(step, branchIndex), step.ParallelBranches[branchIndex]);
        }
    }

    private string ComputeFingerprint()
    {
        var signature = string.Join("\n", _steps.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair =>
            {
                var step = pair.Value;
                var waitKey = step.StepMetadata.TryGetValue("WaitKey", out var key) ? key?.ToString() : "";
                return string.Join('|', pair.Key, step.StepType, step.ActivityType?.AssemblyQualifiedName,
                    step.ResumeEventType?.AssemblyQualifiedName, waitKey, step.ParallelJoinMode,
                    step.LoopType);
            }));
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(signature));
        return Convert.ToHexString(bytes);
    }

    private void ValidateSupportedSteps()
    {
        foreach (var (id, step) in _steps)
        {
            var supported = step.StepType switch
            {
                WorkflowStepType.Activity or WorkflowStepType.Sequence or
                    WorkflowStepType.Conditional or WorkflowStepType.Parallel or WorkflowStepType.Loop => true,
                WorkflowStepType.SuspendResume => step.StepMetadata.ContainsKey("WaitKey"),
                _ => false
            };
            if (!supported)
                throw new NotSupportedException(
                    $"Structured execution does not support {step.StepType} at '{id}'");
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
