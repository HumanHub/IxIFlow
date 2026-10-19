using System.Reflection;
using IxIFlow.Builders;

namespace IxIFlow.Core.Runtime;

internal sealed class WorkflowScopeCatalog
{
    private readonly Dictionary<string, IReadOnlyList<WorkflowStep>> _scopes = new();
    private readonly Dictionary<string, WorkflowStep> _steps = new();
    private readonly Dictionary<(WorkflowStep Step, string Kind, int Index), string> _scopeIds = new();
    private readonly Dictionary<(WorkflowStep Saga, WorkflowStep Source), string> _compensationScopeIds = new();
    private readonly Dictionary<WorkflowStep, string> _ids = new();
    private readonly Dictionary<ItemScope, string> _handles = new(ReferenceEqualityComparer.Instance);

    public WorkflowScopeCatalog(WorkflowDefinition definition)
    {
        AddScope("root", definition.Steps);
        Fingerprint = ComputeFingerprint(definition);
    }

    internal IEnumerable<KeyValuePair<string, WorkflowStep>> AllSteps => _steps;

    public string Fingerprint { get; }

    public IReadOnlyCollection<PropertyInfo> FaultProperties(Type exceptionType) => _steps.Values
        .Where(step => step.StepType == WorkflowStepType.CatchBlock &&
            step.ExceptionType?.IsAssignableFrom(exceptionType) == true && step.FaultType != null)
        .SelectMany(step => FaultProjection.For(step.ExceptionType!, step.FaultType!).SourceProperties)
        .DistinctBy(FaultProjection.PropertyKey)
        .ToArray();

    public IReadOnlyList<WorkflowStep> Steps(string scopeId) => _scopes.TryGetValue(scopeId, out var steps)
        ? steps
        : throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
            $"Scope '{scopeId}' was not found in the registered workflow definition");

    public WorkflowStep Step(string stepId) => _steps.TryGetValue(stepId, out var step)
        ? step
        : throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
            $"Step '{stepId}' was not found in the registered workflow definition");

    public string Id(WorkflowStep step) => _ids[step];
    public string SequenceScope(WorkflowStep step) => ScopeId(step, "sequence");
    public string ThenScope(WorkflowStep step) => ScopeId(step, "then");
    public string ElseScope(WorkflowStep step) => ScopeId(step, "else");
    public string LoopScope(WorkflowStep step) => ScopeId(step, "loop");
    public string ForEachScope(WorkflowStep step) => ScopeId(step, "foreach");
    public string SagaScope(WorkflowStep step) => SequenceScope(step);
    public string CompensationScope(WorkflowStep saga, WorkflowStep source)
    {
        if (!_compensationScopeIds.TryGetValue((saga, source), out var id))
            _compensationScopeIds.Add((saga, source), id = $"{Id(saga)}/compensation/{Id(source)}");
        return id;
    }
    public string FinallyScope(WorkflowStep step) => ScopeId(step, "finally");
    public string TimeoutScope(WorkflowStep step) => ScopeId(step, "timeout");
    public string CatchBodyScope(WorkflowStep catchBlock) => SequenceScope(catchBlock);
    public string BranchScope(WorkflowStep step, int index) => ScopeId(step, "branch", index);
    public string OutcomeScope(WorkflowStep step, int index) => ScopeId(step, "outcome", index);

    private string ScopeId(WorkflowStep step, string kind, int index = -1)
    {
        var key = (step, kind, index);
        if (!_scopeIds.TryGetValue(key, out var id))
            _scopeIds.Add(key, id = index < 0 ? $"{Id(step)}/{kind}" : $"{Id(step)}/{kind}/{index}");
        return id;
    }

    private void AddScope(string id, IReadOnlyList<WorkflowStep> steps)
    {
        _scopes.Add(id, steps);
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            var stepId = $"{id}/{index}";
            _steps.Add(stepId, step);
            if (!_ids.TryAdd(step, stepId))
                throw WorkflowBuildError.Invalid(step, $"Step '{step.Id}' belongs to two scopes");
            if (step.ForEachItem != null && !_handles.TryAdd(step.ForEachItem, stepId))
                throw WorkflowBuildError.Invalid(step,
                    $"{step.ForEachItem} belongs to two ForEach steps, '{_handles[step.ForEachItem]}' and '{stepId}'");
            AddScope(SequenceScope(step), step.SequenceSteps);
            AddScope(ThenScope(step), step.ThenSteps);
            AddScope(ElseScope(step), step.ElseSteps);
            AddScope(LoopScope(step), step.LoopBodySteps);
            AddScope(ForEachScope(step), step.ForEachBodySteps);
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
            var steps = activities?.Select((activity, index) => new WorkflowStep
            {
                Runtime = activity.Runtime,
                SourceLocation = source.SourceLocation,
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

    private string ComputeFingerprint(WorkflowDefinition definition)
    {
        // Combiners declared on the data type decide how joins merge, so they are part of it (spec E-05).
        var combiners = ParallelRules.CombinerSignature(definition.WorkflowDataType);
        var signature = (combiners.Length == 0 ? "" : "combiners:" + combiners + "\n") +
            string.Join("\n", _steps.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair =>
            {
                var step = pair.Value;
                // DefaultPayload is discovery metadata, not execution behavior (D-14).
                var waitKey = step.StepMetadata.TryGetValue("WaitKey", out var key) ? key?.ToString() : "";
                return WorkflowBuildError.Check(step, () => string.Join('|', pair.Key, step.StepType,
                    step.Name, WorkflowTypeIdentity.StableName(step.WorkflowDataType),
                    WorkflowTypeIdentity.StableName(step.PreviousStepDataType),
                    WorkflowTypeIdentity.StableName(step.ActivityType),
                    WorkflowTypeIdentity.StableName(step.WorkflowType),
                    step.WorkflowName, step.WorkflowVersion, step.CancelWithParent,
                    WorkflowTypeIdentity.StableName(step.ResumeEventType), waitKey,
                    step.WaitTimeout?.Ticks, step.DelayDuration?.Ticks, step.ParallelJoinMode,
                    step.LoopType, WorkflowTypeIdentity.StableName(step.ExceptionType),
                    WorkflowTypeIdentity.StableName(step.FaultType),
                    Shape(step.ConditionExpression),
                    step.ConditionExpression == null ? Code(step.ConditionBehavior ?? step.CompiledCondition) : "",
                    step.OutcomeExpression == null ? Code(step.OutcomeSelector) : Shape(step.OutcomeExpression),
                    step.ParallelCompletionExpression == null ? Code(step.ParallelCompletionBehavior ?? step.ParallelCompletionCondition) : Shape(step.ParallelCompletionExpression),
                    string.Join(';', step.OutcomeBranches.Select(OutcomeBranchSignature)),
                    ErrorPolicySignature(step),
                    string.Join(';', step.InputMappings.Select(MappingSignature)),
                    string.Join(';', step.OutputMappings.Select(MappingSignature)),
                    step.RequestName, WorkflowTypeIdentity.StableName(step.ReplyType), step.ReplyToRequest,
                    step.RequestSubjectExpression == null ? Code(step.RequestSubjectBehavior ?? step.RequestSubject) : Shape(step.RequestSubjectExpression),
                    string.Join(';', step.ParallelCombiners.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => pair.Key + "=" + WorkflowTypeIdentity.StableName(pair.Value))),
                    // A ForEach is sequential until a parallel mode exists; its handle is known by this step.
                    step.ForEachItem == null
                        ? ""
                        : (step.ForEachParallel
                              ? $"parallel:{step.ForEachMaxConcurrency}:{step.ParallelJoinMode}"
                              : "sequential") + ":" + WorkflowTypeIdentity.StableName(step.ForEachItem.ItemType),
                    step.ForEachCompletionExpression == null ? Code(step.ForEachCompletionBehavior ?? step.ForEachCompletionCondition) : Shape(step.ForEachCompletionExpression),
                    step.ForEachItemsExpression == null ? Code(step.ForEachItemsBehavior ?? step.ForEachItems) : Shape(step.ForEachItemsExpression),
                    WorkflowTypeIdentity.StableName(step.ResultType), step.ResultExpression == null ? Code(step.ResultBehavior ?? step.ResultSelector) : Shape(step.ResultExpression),
                    Shape(step.ForEachIntoExpression)));
            }));
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(signature));
        return Convert.ToHexString(bytes);
    }

    private string Code(Delegate? behavior) => WorkflowCodeSignature.Of(behavior, _handles);

    private string Shape(System.Linq.Expressions.LambdaExpression? expression) =>
        expression == null ? "" : WorkflowExpressionSignature.Of(expression, _handles);

    private string MappingSignature(PropertyMapping mapping) => string.Join(':',
        mapping.Direction, mapping.TargetProperty,
        WorkflowTypeIdentity.StableName(mapping.SourceType),
        WorkflowTypeIdentity.StableName(mapping.TargetType),
        mapping.CompensationSource,
        Shape(mapping.Expression),
        mapping.Expression == null ? Code(mapping.SourceFunction) : "",
        mapping.Expression == null ? Code(mapping.TargetAssignmentFunction) : "");

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
                    policy.IgnoreCompensationErrors,
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
}
