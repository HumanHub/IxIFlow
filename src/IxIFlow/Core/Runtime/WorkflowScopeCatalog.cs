using JsonSerializer = IxIFlow.Core.Runtime.WorkflowJson;
using System.Reflection;
using IxIFlow.Builders;

namespace IxIFlow.Core.Runtime;

internal sealed class WorkflowScopeCatalog
{
    private readonly Dictionary<string, IReadOnlyList<WorkflowStep>> _scopes = new();
    private readonly Dictionary<string, WorkflowStep> _steps = new();
    private readonly Dictionary<(WorkflowStep Step, string Kind, string? Key), string> _scopeIds = new();
    private readonly Dictionary<(WorkflowStep Saga, WorkflowStep Source), string> _compensationScopeIds = new();
    private readonly Dictionary<WorkflowStep, string> _ids = new();
    private readonly Dictionary<WorkflowStep, string> _authorIds = new();
    public string AuthorId(WorkflowStep step) => _authorIds[step];
    private readonly Dictionary<ItemScope, string> _handles = new(ReferenceEqualityComparer.Instance);

    public WorkflowScopeCatalog(WorkflowDefinition definition)
    {
        InlineConstruction.Cataloged(definition);
        RootDataType = definition.WorkflowDataType;
        AddScope("root", definition.Steps);
        StructureHash = ComputeStructureHash(definition);
        StepTable = new(this);
        GraphTable = new(this);
        SwitchTable = new(this);
    }

    internal IEnumerable<KeyValuePair<string, WorkflowStep>> AllSteps => _steps;

    public Type? RootDataType { get; }
    public string StructureHash { get; }
    public StepTable StepTable { get; private set; }

    internal void BindBundleSources(IReadOnlyList<WorkflowInlineWorkflow> inlineWorkflows) =>
        StepTable = StepTable.InBundle(inlineWorkflows);

    public GraphTable GraphTable { get; }
    public SwitchTable SwitchTable { get; }

    public IEnumerable<string> FaultPropertyKeys => _steps.Values
        .Where(step => step.StepType == WorkflowStepType.CatchBlock && step.FaultType != null)
        .SelectMany(step => FaultProjection.For(step.ExceptionType!, step.FaultType!).SourceProperties)
        .Select(FaultProjection.PropertyKey).Distinct(StringComparer.Ordinal);

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
    public string NodeScope(WorkflowStep graph, string key) => ScopeId(graph, "node", key);

    public string EdgeScope(WorkflowStep graph, string key, int index) =>
        ScopeId(graph, "node", key + "/to/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public string StateScope(WorkflowStep graph, string key) => ScopeId(graph, "state", key);
    public string StateExitScope(WorkflowStep graph, string key) => ScopeId(graph, "state", key + "/exit");
    public string ScopeBodyScope(WorkflowStep step) => ScopeId(step, "scope");
    public string InlineScope(WorkflowStep step) => ScopeId(step, "inline");
    public string CancellationScope(WorkflowStep step) => ScopeId(step, "cancelled");
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

    public string BranchScope(WorkflowStep step, int index) =>
        ScopeId(step, "branch", index.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public string OutcomeScope(WorkflowStep step, int index) => step.OutcomeBranches[index].IsDefault
        ? ScopeId(step, "default")
        : ScopeId(step, "case", index.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private string ScopeId(WorkflowStep step, string kind, string? childKey = null)
    {
        var key = (step, kind, childKey);

        if (!_scopeIds.TryGetValue(key, out var id))
            _scopeIds.Add(key, id = childKey == null ? $"{Id(step)}/{kind}" : $"{Id(step)}/{kind}/{childKey}");

        return id;
    }

    private readonly HashSet<WorkflowStep> _tryCatchOwners = new(ReferenceEqualityComparer.Instance);

    private void AddScope(string id, IReadOnlyList<WorkflowStep> steps, string? authorPrefix = null, string? faultOwner = null,
        string? rethrowOwner = null)
    {
        _scopes.Add(id, steps);

        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            var stepId = $"{id}/{index}";

            if (step.StepType == WorkflowStepType.Rethrow)
                step.RethrowOwnerId = rethrowOwner;

            if (step.LexicalFaultType != null)
                step.LexicalFaultOwnerId = faultOwner;

            _steps.Add(stepId, step);
            _authorIds.TryAdd(step, authorPrefix + step.Id);
            InlineConstruction.Cataloged(step);

            if (!_ids.TryAdd(step, stepId))
                throw WorkflowBuildError.Invalid(step, $"Step '{step.Id}' belongs to two scopes");

            if (step.ForEachItem != null && !_handles.TryAdd(step.ForEachItem, stepId))
                throw WorkflowBuildError.Invalid(step,
                    $"{step.ForEachItem} belongs to two ForEach steps, '{_handles[step.ForEachItem]}' and '{stepId}'");

            if (step.StepType == WorkflowStepType.TryCatch)
                foreach (var handler in step.CatchBlocks)
                    _tryCatchOwners.Add(handler);

            foreach (var scope in step.ChildScopes())
            {
                AddScope(ScopeId(step, scope.Kind, scope.Key), scope.Steps,
                    scope.Kind == "inline" ? AuthorId(step) + "/" : authorPrefix,
                    scope.Kind == "inline" ? null : step.StepType == WorkflowStepType.CatchBlock ? stepId : faultOwner,
                    scope.Kind == "inline" ? null :
                    step.StepType == WorkflowStepType.CatchBlock &&
                    _tryCatchOwners.Contains(step) ? stepId : rethrowOwner);

                if (scope.Kind == "catch" && step.StepType == WorkflowStepType.Saga)
                    AddSagaCompensations(step, authorPrefix);
            }
        }
    }

    private void AddSagaCompensations(WorkflowStep saga, string? authorPrefix)
    {
        foreach (var source in SagaActivities(saga.SequenceSteps))
        {
            var activities = source.StepMetadata.TryGetValue("CompensationActivities", out var value)
                ? value as List<CompensationActivityInfo>
                : null;

            var steps = activities?.Select((activity, index) => new WorkflowStep
            {
                Runtime = activity.Runtime,
                SourceLocation = activity.SourceLocation ?? source.SourceLocation,
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

            AddScope(CompensationScope(saga, source), steps, authorPrefix);
        }
    }

    private static IEnumerable<WorkflowStep> SagaActivities(IEnumerable<WorkflowStep> steps)
    {
        foreach (var step in steps)
        {
            if (step.StepType == WorkflowStepType.Activity &&
                step.StepMetadata.ContainsKey("IsSagaStep"))
                yield return step;

            foreach (var scope in step.ChildScopes().Where(scope =>
                         scope.Kind is "case" or "default" ||
                         step.StepType == WorkflowStepType.CancellationScope && scope.Kind == "sequence"))
            foreach (var nested in SagaActivities(scope.Steps))
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

            // The release namespace prevents reuse of a pre-release format-1 hash.
            Runtime = ExecutionCheckpoint.RuntimeMarker,
            RequiredScopes = definition.RequiredScopes.Select(WorkflowTypeIdentity.StableName).Order(StringComparer.Ordinal),
            Data = WorkflowTypeIdentity.StableName(definition.WorkflowDataType),
            DataTypes = _steps.Values.Select(step => step.WorkflowDataType).Prepend(definition.WorkflowDataType)
                .OfType<Type>().Distinct().OrderBy(WorkflowTypeIdentity.StableName, StringComparer.Ordinal)
                .Select(type => new { Type = WorkflowTypeIdentity.StableName(type), Combiners = ParallelRules.CombinerSignature(type) }),
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
                    ScopeValue = step.ScopeValueType == null ? null : WorkflowTypeIdentity.StableName(step.ScopeValueType),
                    ScopeRetry = RetrySignature(step.ScopeRetryPolicy), step.ScopeThreading, step.ScopeConcurrency,
                    step.WorkflowName, step.WorkflowVersion, step.CancelWithParent, step.InlineStructureHash,
                    Event = WorkflowTypeIdentity.StableName(step.ResumeEventType),
                    WaitKey = step.StepMetadata.GetValueOrDefault("WaitKey") as string,
                    Timeout = step.WaitTimeout?.Ticks, Delay = step.DelayDuration?.Ticks,
                    step.ParallelJoinMode, step.LoopType,
                    step.Target, step.IsDefault, step.Explicit, step.TriggerKind,
                    After = Shape(step.AfterExpression),
                    EdgeLabel = SwitchLabelSignature.Of(step.EdgeLabel),
                    Graph = step.StepType is WorkflowStepType.Flowchart or WorkflowStepType.StateMachine
                        ? new
                        {
                            Kind = step.StepType, Start = step.GraphStart,
                            Nodes = step.GraphNodes.Select(node => new { node.Key, Final = false })
                                .Concat(step.GraphStates.Select(state => new { state.Key, state.Final }))
                                .OrderBy(node => node.Key, StringComparer.Ordinal)
                        }
                        : null,
                    Exception = WorkflowTypeIdentity.StableName(step.ExceptionType),
                    Fault = WorkflowTypeIdentity.StableName(step.FaultType),
                    Factory = Shape(step.ExceptionExpression),
                    FactoryType = WorkflowTypeIdentity.StableName(step.ExceptionFactoryReturnType),
                    step.RethrowOwnerId,
                    Condition = Shape(step.ConditionExpression),
                    Outcome = Shape(step.OutcomeExpression),
                    SwitchValueType = WorkflowTypeIdentity.StableName(step.SwitchValueType),
                    Assignment = step.Assignment == null
                        ? null
                        : new
                        {
                            step.Assignment.TargetPath,
                            Type = WorkflowTypeIdentity.StableName(step.Assignment.ValueType),
                            Target = Shape(step.Assignment.Target), Value = Shape(step.Assignment.Value)
                        },
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

        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    public string Mismatch(WorkflowDefinition definition, string savedHash) =>
        $"Workflow '{definition.Name}' v{definition.Version} differs from the saved version: " +
        $"saved structure hash '{savedHash}', loaded structure hash '{StructureHash}'";

    private string Shape(System.Linq.Expressions.LambdaExpression? expression) =>
        expression == null ? "" : WorkflowExpressionSignature.Of(expression, _handles);

    private string MappingSignature(PropertyMapping mapping) => JsonSerializer.Serialize(new
    {
        mapping.Direction, mapping.TargetProperty, mapping.TargetPath,
        Source = WorkflowTypeIdentity.StableName(mapping.SourceType),
        Target = WorkflowTypeIdentity.StableName(mapping.TargetType),
        mapping.CompensationSource,
        Expression = Shape(mapping.Expression)
    });

    private static string OutcomeBranchSignature(WorkflowOutcomeBranch branch) =>
        branch.IsDefault ? "default" : SwitchLabelSignature.Of(branch.Value);

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