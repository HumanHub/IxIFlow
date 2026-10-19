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
        StructureHash = ComputeStructureHash(definition);
    }

    internal IEnumerable<KeyValuePair<string, WorkflowStep>> AllSteps => _steps;

    public string StructureHash { get; }

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

    private string ComputeStructureHash(WorkflowDefinition definition)
    {
        // Both authoring routes lower to this executable model. Include empty scopes as well:
        // adding an empty branch changes the topology even when it adds no steps.
        var manifest = new
        {
            Format = 1,
            Data = WorkflowTypeIdentity.StableName(definition.WorkflowDataType),
            Combiners = ParallelRules.CombinerSignature(definition.WorkflowDataType),
            Scopes = _scopes.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new { Id = pair.Key, Count = pair.Value.Count }),
            Steps = _steps.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
            {
                var step = pair.Value;
                return WorkflowBuildError.Check(step, () => new
                {
                    Id = pair.Key,
                    step.StepType,
                    Data = WorkflowTypeIdentity.StableName(step.WorkflowDataType),
                    Previous = WorkflowTypeIdentity.StableName(step.PreviousStepDataType),
                    Activity = WorkflowTypeIdentity.StableName(step.ActivityType),
                    Workflow = WorkflowTypeIdentity.StableName(step.WorkflowType),
                    InvokedData = WorkflowTypeIdentity.StableName(step.InvokedDataType),
                    step.WorkflowName, step.WorkflowVersion, step.CancelWithParent,
                    Event = WorkflowTypeIdentity.StableName(step.ResumeEventType),
                    WaitKey = step.StepMetadata.GetValueOrDefault("WaitKey") as string,
                    Timeout = step.WaitTimeout?.Ticks, Delay = step.DelayDuration?.Ticks,
                    step.ParallelJoinMode, step.LoopType,
                    Exception = WorkflowTypeIdentity.StableName(step.ExceptionType),
                    Fault = WorkflowTypeIdentity.StableName(step.FaultType),
                    Condition = Shape(step.ConditionExpression),
                    Outcome = Shape(step.OutcomeExpression),
                    Completion = Shape(step.ParallelCompletionExpression),
                    Outcomes = step.OutcomeBranches.Select(OutcomeBranchSignature),
                    Policy = ErrorPolicySignature(step),
                    Compensations = (step.StepMetadata.GetValueOrDefault("CompensationActivities") as List<CompensationActivityInfo> ?? [])
                        .Select(activity => new
                        {
                            Activity = WorkflowTypeIdentity.StableName(activity.ActivityType),
                            Previous = WorkflowTypeIdentity.StableName(activity.PreviousChainActivityType),
                            Reply = WorkflowTypeIdentity.StableName(activity.ReplyType)
                        }),
                    Inputs = step.InputMappings.Select(MappingSignature),
                    Outputs = step.OutputMappings.Select(MappingSignature),
                    step.RequestName,
                    Reply = WorkflowTypeIdentity.StableName(step.ReplyType), step.ReplyToRequest,
                    Subject = Shape(step.RequestSubjectExpression),
                    Combiners = step.ParallelCombiners.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => new { Path = pair.Key, Type = WorkflowTypeIdentity.StableName(pair.Value) }),
                    step.ForEachParallel, step.ForEachMaxConcurrency,
                    Item = WorkflowTypeIdentity.StableName(step.ForEachItem?.ItemType),
                    ItemCompletion = Shape(step.ForEachCompletionExpression),
                    Items = Shape(step.ForEachItemsExpression),
                    ResultType = WorkflowTypeIdentity.StableName(step.ResultType),
                    StoredResult = WorkflowTypeIdentity.StableName(step.ForEachStoredResultType),
                    ConditionResult = WorkflowTypeIdentity.StableName(step.ForEachConditionResultType),
                    Result = Shape(step.ResultExpression), Into = Shape(step.ForEachIntoExpression)
                });
            })
        };
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(manifest);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    public string Mismatch(WorkflowDefinition definition, string savedHash) =>
        $"Workflow '{definition.Name}' v{definition.Version} differs from the saved version: " +
        $"saved structure hash '{savedHash}', loaded structure hash '{StructureHash}'";

    private string Shape(System.Linq.Expressions.LambdaExpression? expression) =>
        expression == null ? "" : WorkflowExpressionSignature.Of(expression, _handles);

    private string MappingSignature(PropertyMapping mapping) => System.Text.Json.JsonSerializer.Serialize(new
    {
        mapping.Direction, mapping.TargetProperty, mapping.TargetPath,
        Source = WorkflowTypeIdentity.StableName(mapping.SourceType),
        Target = WorkflowTypeIdentity.StableName(mapping.TargetType),
        mapping.CompensationSource,
        Expression = Shape(mapping.Expression)
    });

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
