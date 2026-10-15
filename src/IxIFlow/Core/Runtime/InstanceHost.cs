using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using IxIFlow.Builders.Interfaces;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// Owns workflow instances: it validates requests, holds the instance gate and execution lease,
/// commits checkpoints, and coordinates child workflows. The <see cref="WorkflowExecutor"/> runs
/// the instance between commits.
/// </summary>
internal sealed class InstanceHost(
    IServiceProvider services,
    IActivityExecutor activityExecutor,
    IWorkflowInvoker workflowInvoker,
    IWorkflowStateRepository repository,
    IWorkflowVersionRegistry registry,
    InProcessInstanceGate gate,
    ExecutionLeaseSettings? leaseSettings = null,
    TimeProvider? timeProvider = null,
    ExecutionLimits? limits = null,
    RunControl? control = null,
    CancellationWatch? watch = null)
{
    private readonly IWorkflowStateRepository _store = GuardedStateRepository.Wrap(repository);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly WorkflowInvocationTransitionExecutor _invocations = new(workflowInvoker);
    private readonly bool _childStartIsCheckpointed = workflowInvoker is ICheckpointedWorkflowInvoker;
    private readonly ExecutionLeaseSettings _leaseSettings = leaseSettings ?? ExecutionLeaseSettings.Default;
    private readonly ExecutionLimits _limits = limits ?? ExecutionLimits.Default;
    private readonly RunControl _control = control ?? new RunControl();
    private readonly CancellationWatch _watch = watch ?? new CancellationWatch(repository, timeProvider);
    private readonly WorkflowExecutor _executor = new(services, activityExecutor,
        timeProvider ?? TimeProvider.System);
    private readonly ContinuationTransitions _transitions = new(services, timeProvider ?? TimeProvider.System);

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public static bool HasStructuredCheckpoint(WorkflowInstance? instance) =>
        instance != null && ExecutionCheckpoint.IsStructured(instance.ExecutionStateJson);

    public async Task<WorkflowExecutionResult> CancelAsync(string instanceId,
        CancellationReason reason, CancellationToken cancellationToken) =>
        await RequestCancellationAsync(instanceId, reason, cancellationToken)
        ?? await RecoverAsync(instanceId, cancellationToken);

    /// <summary>
    /// Saves a durable cancellation request. Returns the result when nothing has to run here, or
    /// null when the instance is idle and must be recovered to unwind.
    /// </summary>
    public async Task<WorkflowExecutionResult?> RequestCancellationAsync(string instanceId,
        CancellationReason reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(reason);
        cancellationToken.ThrowIfCancellationRequested();
        if (!await _store.RequestCancellationAsync(instanceId, reason))
        {
            var existing = await _store.GetWorkflowInstanceAsync(instanceId);
            if (existing == null)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
                    $"Workflow instance '{instanceId}' does not exist");
            if (existing.Status is WorkflowStatus.Running or WorkflowStatus.Suspended or
                WorkflowStatus.NeedsResolution)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                    "The workflow instance changed while cancellation was requested; retry");
            return ExistingResult(existing);
        }

        var instance = await _store.GetWorkflowInstanceAsync(instanceId);
        if (instance?.Status == WorkflowStatus.Running)
            return new WorkflowExecutionResult
            {
                InstanceId = instanceId,
                Status = WorkflowExecutionStatus.Running,
                ErrorMessage = "Cancellation was requested; the executing host will stop the workflow"
            };
        if (instance?.Status is WorkflowStatus.Completed or WorkflowStatus.Failed or
            WorkflowStatus.Cancelled or WorkflowStatus.Terminated or WorkflowStatus.TimedOut)
            return ExistingResult(instance);
        return null;
    }

    /// <summary>The saved state of an instance as a result, or null when it does not exist.</summary>
    public async Task<WorkflowExecutionResult?> ReadAsync(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        var instance = await _store.GetWorkflowInstanceAsync(instanceId);
        return instance == null ? null : ExistingResult(instance);
    }

    public async Task<WorkflowExecutionResult> ResolveAsync(string instanceId,
        string invocationId, ActivityResolution resolution, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);
        ArgumentNullException.ThrowIfNull(resolution);
        using var lease = await gate.EnterAsync(instanceId, cancellationToken);
        var instance = await _store.GetWorkflowInstanceAsync(instanceId);
        if (instance?.Status != WorkflowStatus.NeedsResolution || !HasStructuredCheckpoint(instance))
            return Rejected(instanceId, "The workflow instance has no activity awaiting resolution");

        var definition = await registry.GetWorkflowDefinitionAsync(instance.WorkflowName, instance.WorkflowVersion)
            ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                "The registered workflow definition is unavailable");
        var scopes = new WorkflowScopeCatalog(definition);
        var checkpoint = ExecutionCheckpoint.Read(instance.ExecutionStateJson);
        if (checkpoint.DefinitionFingerprint != scopes.Fingerprint)
            return Rejected(instanceId, "The registered workflow definition differs from the saved version");
        if (checkpoint.OperatorStop is { } stop)
            return Rejected(instanceId, stop.Describe());
        var continuation = checkpoint.Continuations.SingleOrDefault(item =>
            item.PendingActivity?.Id == invocationId && item.Status == ContinuationStatus.WaitingResolution)
            ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
                $"Activity invocation '{invocationId}' is not awaiting resolution");
        var pending = continuation.PendingActivity!;
        var step = scopes.Step(pending.StepId);
        if (step.StepType is not (WorkflowStepType.Activity or WorkflowStepType.WorkflowInvocation))
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
                "This step cannot be resolved by an operator");
        var child = pending.ChildWorkflowId == null
            ? null : await _store.GetWorkflowInstanceAsync(pending.ChildWorkflowId);
        if (ChildIsActive(child))
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                $"Resolve child workflow '{pending.ChildWorkflowId}' before recovering its parent invocation");
        var childDataType = step.StepType == WorkflowStepType.WorkflowInvocation
            ? await ResolveChildDataTypeAsync(step)
            : null;
        var decision = PrepareResolution(scopes, step, resolution, childDataType);
        var dataType = WorkflowTypeIdentity.Resolve(instance.WorkflowDataType)
            ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                $"Workflow data type '{instance.WorkflowDataType}' is unavailable");
        var workflowData = JsonSerializer.Deserialize(instance.WorkflowDataJson, dataType)
            ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "Saved workflow data cannot be read");

        await using var owner = await WorkflowExecutionLease.TryAcquireAsync(
            _store, instance, _leaseSettings, _watch, _time);
        if (owner == null)
            return WorkflowExecutor.Busy(instanceId);
        var current = await _store.GetWorkflowInstanceAsync(instanceId);
        if (current?.Revision != instance.Revision || current.Status != instance.Status)
            return current?.Status == WorkflowStatus.Running
                ? WorkflowExecutor.Busy(instanceId)
                : Rejected(instanceId, "The activity changed before resolution could be saved");

        pending.Resolution = decision;
        pending.ResolutionReason = null;
        continuation.Status = ContinuationStatus.Active;
        instance.Status = WorkflowStatus.Running;
        var cancellationReason = await _store.GetCancellationRequestAsync(instanceId);
        if (cancellationReason != null && !checkpoint.CancellationRequested &&
            checkpoint.Continuations[0].Status != ContinuationStatus.Completed)
        {
            instance.CancellationReason = cancellationReason;
            checkpoint.CancellationRequested = true;
            ContinuationTransitions.CancelTree(checkpoint, checkpoint.Continuations[0]);
        }
        await PersistAsync(instance, checkpoint, workflowData, scopes, owner);
        return await RunAndSaveAsync(definition, instance, checkpoint, workflowData,
            true, owner, cancellationToken);
    }

    public async Task<IReadOnlyList<PendingActivityInfo>> GetPendingActivitiesAsync(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        var instance = await _store.GetWorkflowInstanceAsync(instanceId);
        if (instance?.Status != WorkflowStatus.NeedsResolution || !HasStructuredCheckpoint(instance))
            return [];
        var definition = await registry.GetWorkflowDefinitionAsync(instance.WorkflowName, instance.WorkflowVersion)
            ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                "The registered workflow definition is unavailable");
        var scopes = new WorkflowScopeCatalog(definition);
        var checkpoint = ExecutionCheckpoint.Read(instance.ExecutionStateJson);
        if (checkpoint.DefinitionFingerprint != scopes.Fingerprint)
            throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                "The registered workflow definition differs from the saved version");
        var pendingActivities = new List<PendingActivityInfo>();
        foreach (var continuation in checkpoint.Continuations.Where(item =>
                     item.Status == ContinuationStatus.WaitingResolution &&
                     item.PendingActivity != null))
        {
            var pending = continuation.PendingActivity!;
            var step = scopes.Step(pending.StepId);
            var child = pending.ChildWorkflowId == null
                ? null : await _store.GetWorkflowInstanceAsync(pending.ChildWorkflowId);
            pendingActivities.Add(new PendingActivityInfo(pending.Id, pending.StepId,
                step.ActivityType?.Name ?? step.Name, pending.ResolutionReason,
                pending.StartedAtUtc,
                step.OutputMappings.Where(mapping => mapping.Direction == PropertyMappingDirection.Output)
                    .Select(mapping => mapping.TargetProperty).Distinct().ToArray(),
                step.StepType == WorkflowStepType.Activity ||
                step.StepType == WorkflowStepType.WorkflowInvocation && !ChildIsActive(child)));
        }
        return pendingActivities;
    }

    private static bool ChildIsActive(WorkflowInstance? child) =>
        child != null && child.Status is not (WorkflowStatus.Completed or
            WorkflowStatus.Failed or WorkflowStatus.Cancelled or
            WorkflowStatus.TimedOut or WorkflowStatus.Terminated);

    private async Task<Type> ResolveChildDataTypeAsync(WorkflowStep step)
    {
        if (step.WorkflowType != null)
        {
            var contract = step.WorkflowType.GetInterfaces().FirstOrDefault(type =>
                type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IWorkflow<>));
            return contract?.GetGenericArguments()[0]
                ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                    "The child workflow data type is unavailable");
        }
        var definition = await registry.GetWorkflowDefinitionAsync(step.WorkflowName!, step.WorkflowVersion!.Value);
        return definition?.WorkflowDataType
            ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                "The registered child workflow definition is unavailable");
    }

    private static ActivityResolutionState PrepareResolution(WorkflowScopeCatalog scopes,
        WorkflowStep step, ActivityResolution resolution, Type? childDataType)
    {
        var decision = new ActivityResolutionState
        {
            Kind = resolution.Kind,
            Note = resolution.Note,
            DecidedBy = resolution.DecidedBy
        };
        if (resolution.Kind == ActivityResolutionKind.Failed)
        {
            var error = resolution.Error ?? new InvalidOperationException(resolution.Note);
            decision.Failure = SerializedException.From(error,
                scopes.FaultProperties(SerializedException.CatchType(error)));
            return decision;
        }

        if (step.StepType == WorkflowStepType.WorkflowInvocation)
        {
            var result = resolution.InvocationResult
                ?? throw new ArgumentException("Observed child completion requires child workflow data", nameof(resolution));
            if (!childDataType!.IsInstanceOfType(result))
                throw new ArgumentException("Observed child workflow data has the wrong type", nameof(resolution));
            foreach (var mapped in step.OutputMappings.Where(mapping =>
                         mapping.Direction == PropertyMappingDirection.Output))
            {
                if (childDataType.GetProperty(mapped.TargetProperty) == null)
                    throw new ArgumentException(
                        $"Child workflow data has no output '{mapped.TargetProperty}'", nameof(resolution));
            }
            decision.InvocationResult = SerializedValue.From(result);
            return decision;
        }

        if (resolution.InvocationResult != null)
            throw new ArgumentException("Activity completion requires activity output properties", nameof(resolution));

        foreach (var mapped in step.OutputMappings.Where(mapping =>
                     mapping.Direction == PropertyMappingDirection.Output))
        {
            if (!resolution.OutputProperties.ContainsKey(mapped.TargetProperty))
                throw new ArgumentException(
                    $"Observed completion must provide output '{mapped.TargetProperty}'", nameof(resolution));
        }
        foreach (var (name, value) in resolution.OutputProperties)
        {
            var property = step.ActivityType!.GetProperty(name)
                ?? throw new ArgumentException($"Activity has no output property '{name}'", nameof(resolution));
            if (property.GetSetMethod(nonPublic: true) == null || property.GetIndexParameters().Length != 0)
                throw new ArgumentException($"Activity output '{name}' is not writable", nameof(resolution));
            if ((value == null && property.PropertyType.IsValueType &&
                 Nullable.GetUnderlyingType(property.PropertyType) == null) ||
                (value != null && !property.PropertyType.IsInstanceOfType(value)))
                throw new ArgumentException($"Activity output '{name}' has the wrong type", nameof(resolution));
            decision.OutputProperties.Add(name, SerializedValue.From(value));
        }
        return decision;
    }

    public async Task<WorkflowExecutionResult> StartAsync<TData>(
        WorkflowDefinition definition,
        TData workflowData,
        WorkflowOptions options,
        CancellationToken cancellationToken)
        where TData : class
    {
        if (options.EnableDebugging)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
                "Workflow debugging is not implemented");
        if (options.WorkflowRetryPolicy != null)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
                "Workflow-wide retry is not supported; configure retry on a saga or activity step");
        if (options.InstanceId != null && string.IsNullOrWhiteSpace(options.InstanceId))
            throw new ArgumentException("An explicit workflow instance ID cannot be empty", nameof(options));
        if (options.ExecutionTimeout is { } executionTimeout && executionTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options),
                "Execution timeout must be positive");
        if (options.InstanceId != null && !options.PersistState)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
                "An explicit workflow instance ID requires state persistence");
        WorkflowDefinitionIdentity.AssignImplicitName(definition);
        var instance = new WorkflowInstance
        {
            InstanceId = options.InstanceId ?? Guid.NewGuid().ToString("N"),
            WorkflowName = definition.Name,
            WorkflowVersion = definition.Version,
            WorkflowDataType = workflowData.GetType().AssemblyQualifiedName!,
            WorkflowDataJson = JsonSerializer.Serialize(workflowData),
            Status = WorkflowStatus.Running,
            CorrelationId = options.CorrelationId ?? Guid.NewGuid().ToString("N"),
            ParentInstanceId = options.ParentInstanceId,
            CreatedAt = Now,
            StartedAt = Now,
            TotalSteps = definition.Steps.Count,
            TracingEnabled = options.EnableTracing,
            Properties = new Dictionary<string, object>(options.Properties)
        };
        var scopes = new WorkflowScopeCatalog(definition);
        if (!options.PersistState && scopes.RequiresPersistence)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
                "A workflow with waits or child workflows requires state persistence");
        var checkpoint = new ExecutionCheckpoint
        {
            DefinitionFingerprint = scopes.Fingerprint,
            ExecutionDeadlineUtc = options.ExecutionTimeout is { } timeout
                ? instance.StartedAt!.Value.Add(timeout) : null,
            Continuations =
            [
                new ContinuationState
                {
                    Stack = [new ScopePosition { ScopeId = "root" }]
                }
            ]
        };

        try
        {
            using var lease = await gate.EnterAsync(instance.InstanceId, cancellationToken);
            // A repeated start of an existing instance is answered without claiming it, so retries
            // never count as crashes of an instance another host lost (B-08).
            if (options.InstanceId != null &&
                await _store.GetWorkflowInstanceAsync(instance.InstanceId) is { } started)
                return ExistingStart(started, definition, scopes);
            await using var owner = options.PersistState
                ? await WorkflowExecutionLease.TryAcquireAsync(_store, instance, _leaseSettings, _watch, _time)
                    ?? (options.InstanceId == null
                        ? throw new WorkflowEngineException(WorkflowErrorKind.Infrastructure,
                            "The new workflow instance could not acquire execution ownership")
                        : null)
                : null;
            if (options.PersistState && owner == null)
                return WorkflowExecutor.Busy(instance.InstanceId);
            // Another start may have saved the instance since the first read.
            if (options.InstanceId != null &&
                await _store.GetWorkflowInstanceAsync(instance.InstanceId) is { } existing)
                return ExistingStart(existing, definition, scopes);
            var registered = await registry.GetWorkflowDefinitionAsync(definition.Name, definition.Version);
            if (registered?.Metadata.TryGetValue("IsActive", out var isActive) == true &&
                isActive is false)
                throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                    $"Workflow {definition.Name} v{definition.Version} is deactivated");
            instance.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
            instance.ExecutionSnapshot = ExecutionSnapshotFactory.Create(instance, checkpoint, scopes, Now);
            if (options.PersistState)
            {
                await registry.RegisterWorkflowAsync(definition);
                await PersistAsync(instance, checkpoint, workflowData, scopes, owner);
                // The start is durable from here; a caller waiting only for acceptance continues.
                _control.Accept(instance.InstanceId);
            }
            return await RunAndSaveAsync(definition, instance, checkpoint, workflowData,
                options.PersistState, owner, cancellationToken);
        }
        catch (ExecutionOwnershipLostException)
        {
            return WorkflowExecutor.Busy(instance.InstanceId);
        }
        catch (WorkflowEngineException error) when (WorkflowEngineException.IsEngineFailure(error))
        {
            error.At(instance.InstanceId);
            throw;
        }
    }

    public async Task<WorkflowExecutionResult> ResumeAsync<TEvent>(
        string instanceId,
        string? key,
        TEvent @event,
        CancellationToken cancellationToken,
        string? deliveryId = null)
        where TEvent : class
    {
        using var lease = await gate.EnterAsync(instanceId, cancellationToken);
        var deliveryHash = deliveryId == null ? null : ComputeDeliveryHash(key, @event);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var instance = await _store.GetWorkflowInstanceAsync(instanceId);
            if (deliveryId != null && HasStructuredCheckpoint(instance) &&
                ExecutionCheckpoint.Read(instance!.ExecutionStateJson).AcceptedDeliveries
                    .TryGetValue(deliveryId, out var acceptedHash))
            {
                if (acceptedHash != deliveryHash)
                    throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
                        $"Delivery ID '{deliveryId}' was already used with a different event");
                var accepted = ExistingResult(instance);
                accepted.EventAccepted = true;
                return accepted;
            }
            if (instance?.Status == WorkflowStatus.Running && HasStructuredCheckpoint(instance))
                return new WorkflowExecutionResult
                {
                    InstanceId = instanceId,
                    Status = WorkflowExecutionStatus.Running,
                    ErrorMessage = "The workflow instance is executing; retry the event after it becomes idle"
                };
            if (instance?.Status is not (WorkflowStatus.Suspended or WorkflowStatus.NeedsResolution) ||
                !HasStructuredCheckpoint(instance))
                return Rejected(instanceId, "The workflow instance is not waiting");

            var checkpoint = ExecutionCheckpoint.Read(instance.ExecutionStateJson);
            if (checkpoint.OperatorStop is { } stop)
                return Rejected(instanceId, stop.Describe());
            var definition = await registry.GetWorkflowDefinitionAsync(instance.WorkflowName, instance.WorkflowVersion);
            if (definition == null)
                return Rejected(instanceId, "The registered workflow definition is unavailable");

            var dataType = WorkflowTypeIdentity.Resolve(instance.WorkflowDataType)
                ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                    $"Workflow data type '{instance.WorkflowDataType}' is unavailable");
            var workflowData = JsonSerializer.Deserialize(instance.WorkflowDataJson, dataType)
                ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                    "Saved workflow data cannot be read");
            var scopes = new WorkflowScopeCatalog(definition);
            if (checkpoint.DefinitionFingerprint != scopes.Fingerprint)
                return Rejected(instanceId, "The registered workflow definition differs from the saved version");
            await using var owner = await WorkflowExecutionLease.TryAcquireAsync(
                _store, instance, _leaseSettings, _watch, _time);
            if (owner == null)
                return WorkflowExecutor.Busy(instanceId);
            var current = await _store.GetWorkflowInstanceAsync(instanceId);
            if (current?.Revision != instance.Revision || current.Status != instance.Status)
            {
                if (current?.Status is WorkflowStatus.Suspended or WorkflowStatus.NeedsResolution)
                    continue;
                return current?.Status == WorkflowStatus.Running
                    ? WorkflowExecutor.Busy(instanceId)
                    : Rejected(instanceId, "The wait changed before this event could be accepted");
            }
            if (await _executor.ReconcileChildWaitsAsync(checkpoint, new HostedRun(this, instance, owner, true)))
            {
                instance.Status = WorkflowStatus.Running;
                await PersistAsync(instance, checkpoint, workflowData, scopes, owner);
                var refreshed = await RunAndSaveAsync(definition, instance, checkpoint, workflowData,
                    true, owner, cancellationToken);
                if (refreshed.Status == WorkflowExecutionStatus.Suspended)
                    continue;
                return refreshed;
            }
            if (_transitions.HasDueDeadline(checkpoint))
            {
                instance.Status = WorkflowStatus.Running;
                await PersistAsync(instance, checkpoint, workflowData, scopes, owner);
                var afterDeadline = await RunAndSaveAsync(definition, instance, checkpoint, workflowData,
                    true, owner, cancellationToken);
                if (afterDeadline.Status == WorkflowExecutionStatus.Suspended)
                    continue;
                return afterDeadline;
            }
            var candidates = checkpoint.Waits.Where(wait =>
                (key == null || wait.Key == key) &&
                WorkflowTypeIdentity.Resolve(wait.EventType)?.IsAssignableFrom(@event.GetType()) == true).ToList();
            if (candidates.Count == 0)
                return Rejected(instanceId, "No active wait matches this event and key");
            var matching = StructuredWaitMatcher.FindMatches(checkpoint, scopes, workflowData,
                @event, services, key);
            if (matching.Count == 0)
                return new WorkflowExecutionResult
                {
                    InstanceId = instanceId,
                    Status = WorkflowExecutionStatus.Suspended,
                    WorkflowData = workflowData,
                    ErrorMessage = "The event did not satisfy any active wait"
                };
            if (matching.Count > 1)
                return Rejected(instanceId, "The event satisfies more than one wait; provide a unique key");

            var cancellationReason = await _store.GetCancellationRequestAsync(instanceId);
            if (cancellationReason != null && !checkpoint.CancellationRequested)
            {
                instance.CancellationReason = cancellationReason;
                checkpoint.CancellationRequested = true;
                ContinuationTransitions.CancelTree(checkpoint, checkpoint.Continuations[0]);
                instance.Status = WorkflowStatus.Running;
                await PersistAsync(instance, checkpoint, workflowData, scopes, owner);
                return await RunAndSaveAsync(definition, instance, checkpoint, workflowData,
                    true, owner, cancellationToken);
            }

            var wait = matching[0];
            var continuation = checkpoint.Continuations.Single(item => item.Id == wait.ContinuationId);

            var acceptedWait = new AcceptedWaitState
            {
                StepId = wait.StepId,
                Event = SerializedValue.From(@event)!,
                Key = wait.ChildWorkflowId == null ? null : wait.Key,
                DeliveryId = wait.ChildWorkflowId == null ? null : deliveryId ?? Guid.NewGuid().ToString("N"),
                DeliveryHash = wait.ChildWorkflowId == null ? null : deliveryHash
            };
            continuation.AcceptedWait = acceptedWait;
            if (wait.ChildWorkflowId != null)
                checkpoint.Waits.RemoveAll(item => item.ChildWorkflowId == wait.ChildWorkflowId &&
                    item.ContinuationId == continuation.Id);
            else
                checkpoint.Waits.Remove(wait);
            if (deliveryId != null && wait.ChildWorkflowId == null)
                checkpoint.AcceptedDeliveries.Add(deliveryId, deliveryHash!);
            continuation.Status = ContinuationStatus.Active;

            instance.Status = WorkflowStatus.Running;
            await PersistAsync(instance, checkpoint, workflowData, scopes, owner);

            var resumed = await RunAndSaveAsync(definition, instance, checkpoint, workflowData,
                true, owner, cancellationToken);
            resumed.EventAccepted = wait.ChildWorkflowId == null || acceptedWait.EventAccepted == true;
            return resumed;
        }
        return WorkflowExecutor.Busy(instanceId);
    }

    private static string ComputeDeliveryHash<TEvent>(string? key, TEvent @event)
        where TEvent : class
    {
        var type = @event.GetType();
        var payload = $"{WorkflowTypeIdentity.StableName(type)}\n{key}\n" +
            JsonSerializer.Serialize(@event, type);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    public Task<WorkflowExecutionResult> RecoverAsync(string instanceId, CancellationToken cancellationToken) =>
        ContinueAsync(instanceId, release: false, cancellationToken);

    /// <summary>
    /// Releases an instance stopped for an operator and runs it. Automatic recovery, including a
    /// parent checking its child, never releases a stop.
    /// </summary>
    public Task<WorkflowExecutionResult> ReleaseAsync(string instanceId, CancellationToken cancellationToken) =>
        ContinueAsync(instanceId, release: true, cancellationToken);

    private async Task<WorkflowExecutionResult> ContinueAsync(string instanceId, bool release,
        CancellationToken cancellationToken)
    {
        using var lease = await gate.EnterAsync(instanceId, cancellationToken);
        var instance = await _store.GetWorkflowInstanceAsync(instanceId);
        var cancellationReason = await _store.GetCancellationRequestAsync(instanceId);
        var canRecover = instance?.Status is WorkflowStatus.Running or WorkflowStatus.NeedsResolution ||
            instance?.Status == WorkflowStatus.Suspended &&
            (cancellationReason != null || HasDueDeadline(instance) || HasChildWait(instance));
        if (!canRecover || !HasStructuredCheckpoint(instance))
            return Rejected(instanceId, release
                ? "The workflow instance is not stopped for an operator"
                : "The workflow instance has no interrupted execution to recover");
        var checkpoint = ExecutionCheckpoint.Read(instance.ExecutionStateJson);
        if (release && checkpoint.OperatorStop == null)
            return Rejected(instanceId, "The workflow instance is not stopped for an operator");
        // A request already applied is being unwound; only a new one changes what the run does.
        var newCancellation = cancellationReason != null && !checkpoint.CancellationRequested &&
            checkpoint.Continuations[0].Status != ContinuationStatus.Completed;
        // Only a release or a new cancellation moves a stopped instance, so recovery, including a
        // parent checking its child, answers it without claiming it. A cancellation that is already
        // unwinding and stopped again needs a release.
        if (checkpoint.OperatorStop is { } held && !release && !newCancellation)
            return Stopped(instance, held);

        var definition = await registry.GetWorkflowDefinitionAsync(instance.WorkflowName, instance.WorkflowVersion);
        if (definition == null)
            return Rejected(instanceId, "The registered workflow definition is unavailable");
        var scopes = new WorkflowScopeCatalog(definition);
        if (checkpoint.DefinitionFingerprint != scopes.Fingerprint)
            return Rejected(instanceId, "The registered workflow definition differs from the saved version");
        // Ownership comes before the workflow data is read, so an instance whose data or code kills
        // its host is still counted by the next claim, and quarantined (B-08).
        await using var owner = await WorkflowExecutionLease.TryAcquireAsync(
            _store, instance, _leaseSettings, _watch, _time);
        if (owner == null)
            return WorkflowExecutor.Busy(instanceId);
        var current = await _store.GetWorkflowInstanceAsync(instanceId);
        if (current?.Revision != instance.Revision || current.Status != instance.Status)
            return current?.Status == WorkflowStatus.Running
                ? WorkflowExecutor.Busy(instanceId)
                : Rejected(instanceId, "The workflow instance changed before recovery could start");
        // Only a claim of a running instance can see crashes: every commit that leaves Running
        // records progress and clears the count, and other entry points refuse running instances.
        if (owner.CrashCount >= _limits.MaxCrashes)
            return await QuarantineAsync(instance, checkpoint, scopes, owner);
        var stopCleared = checkpoint.OperatorStop != null;
        if (stopCleared)
        {
            checkpoint.OperatorStop = null;
            instance.LastError = null;
            instance.LastErrorStackTrace = null;
        }
        foreach (var continuation in checkpoint.Continuations.Where(item =>
                     item.PendingActivity is { ChildWorkflowId: null } &&
                     item.Status is (ContinuationStatus.WaitingResolution or
                         ContinuationStatus.Cancelling)))
            continuation.Status = ContinuationStatus.Active;
        instance.Status = WorkflowStatus.Running;
        if (newCancellation)
        {
            instance.CancellationReason = cancellationReason;
            checkpoint.CancellationRequested = true;
            checkpoint.UnhandledError = null;
            checkpoint.RuntimeUnhandledError = null;
            ContinuationTransitions.CancelTree(checkpoint, checkpoint.Continuations[0]);
        }
        if (stopCleared)
        {
            // Leaving a stop is saved before any workflow code runs, so a crash after it is counted
            // and a cancellation is not left waiting. It saves the stopped checkpoint without its
            // marker, so no limit applies; a run that still exceeds a limit stops again at its next commit.
            await ExecutionCheckpointWriter.CommitAsync(new HostedRun(this, instance, owner, true), instance,
                checkpoint, null, scopes, Now, preserveSavedWorkflowData: true, enforceLimit: false);
        }
        var dataType = WorkflowTypeIdentity.Resolve(instance.WorkflowDataType)
            ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                $"Workflow data type '{instance.WorkflowDataType}' is unavailable");
        var workflowData = JsonSerializer.Deserialize(instance.WorkflowDataJson, dataType)
            ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "Saved workflow data cannot be read");
        return await RunAndSaveAsync(definition, instance, checkpoint,
            workflowData, true, owner, cancellationToken);
    }

    private bool HasDueDeadline(WorkflowInstance instance) =>
        HasStructuredCheckpoint(instance) &&
        _transitions.HasDueDeadline(ExecutionCheckpoint.Read(instance.ExecutionStateJson));

    private bool HasChildWait(WorkflowInstance instance) =>
        HasStructuredCheckpoint(instance) &&
        ExecutionCheckpoint.Read(instance.ExecutionStateJson).Waits.Any(wait =>
            wait.ChildWorkflowId != null);

    private static WorkflowExecutionResult ExistingStart(WorkflowInstance existing, WorkflowDefinition definition,
        WorkflowScopeCatalog scopes)
    {
        var checkpoint = ExecutionCheckpoint.Read(existing.ExecutionStateJson);
        if (existing.WorkflowName != definition.Name || existing.WorkflowVersion != definition.Version ||
            checkpoint.DefinitionFingerprint != scopes.Fingerprint)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
                $"Workflow instance '{existing.InstanceId}' belongs to a different definition");
        // A stopped instance is answered without reading its data, which may be what stopped it.
        return checkpoint.OperatorStop is { } stop ? Stopped(existing, stop) : ExistingResult(existing);
    }

    private static WorkflowExecutionResult Rejected(string instanceId, string reason) => new()
    {
        InstanceId = instanceId,
        Status = WorkflowExecutionStatus.Faulted,
        ErrorMessage = reason
    };

    private static WorkflowExecutionResult ExistingResult(WorkflowInstance instance) => new()
    {
        InstanceId = instance.InstanceId,
        Status = instance.Status switch
        {
            WorkflowStatus.Completed => WorkflowExecutionStatus.Success,
            WorkflowStatus.Suspended => WorkflowExecutionStatus.Suspended,
            WorkflowStatus.NeedsResolution => WorkflowExecutionStatus.NeedsResolution,
            WorkflowStatus.Failed => WorkflowExecutionStatus.Faulted,
            WorkflowStatus.Cancelled => WorkflowExecutionStatus.Cancelled,
            WorkflowStatus.TimedOut => WorkflowExecutionStatus.TimedOut,
            WorkflowStatus.Terminated => WorkflowExecutionStatus.Failed,
            _ => WorkflowExecutionStatus.Running
        },
        WorkflowData = WorkflowTypeIdentity.Resolve(instance.WorkflowDataType) is { } dataType
            ? JsonSerializer.Deserialize(instance.WorkflowDataJson, dataType)
            : null,
        TraceEntries = instance.ExecutionHistory.ToList(),
        ErrorMessage = instance.LastError,
        ErrorStackTrace = instance.LastErrorStackTrace
    };

    // A finished child makes its parent due now instead of waiting for the parent's next check.
    // If this fails, the parent still finds the result when it next checks its children.
    private async Task MakeParentRunnableAsync(WorkflowInstance instance)
    {
        if (instance.ParentInstanceId == null || instance.Status is not (WorkflowStatus.Completed or
                WorkflowStatus.Failed or WorkflowStatus.Cancelled or WorkflowStatus.TimedOut or
                WorkflowStatus.Terminated))
            return;
        try
        {
            await _store.MakeRunnableAsync(instance.ParentInstanceId);
        }
        catch (Exception)
        {
            // Not a commit failure: the child's result is already saved.
        }
    }

    private Task<WorkflowExecutionResult> RunAndSaveAsync(WorkflowDefinition definition, WorkflowInstance instance,
        ExecutionCheckpoint checkpoint, object workflowData, bool persistState, WorkflowExecutionLease? owner,
        CancellationToken cancellationToken) =>
        _executor.RunAsync(definition, instance, checkpoint, workflowData,
            new HostedRun(this, instance, owner, persistState), cancellationToken);

    // A request whose checkpoint would exceed a limit is refused; the instance keeps its saved state.
    private async Task PersistAsync(WorkflowInstance instance, ExecutionCheckpoint checkpoint,
        object workflowData, WorkflowScopeCatalog scopes, WorkflowExecutionLease? owner)
    {
        try
        {
            await ExecutionCheckpointWriter.CommitAsync(new HostedRun(this, instance, owner, true), instance,
                checkpoint, workflowData, scopes, Now);
        }
        catch (CheckpointLimitExceededException error)
        {
            throw new WorkflowEngineException(WorkflowErrorKind.ResourceExhausted, error.Message, error);
        }
    }

    private async Task<CheckpointReply> CommitAsync(WorkflowInstance instance, CheckpointCommit commit,
        long epoch)
    {
        if (epoch == 0)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                $"A run of workflow instance '{commit.InstanceId}' without an execution lease cannot commit");
        if (commit.InstanceId != instance.InstanceId)
            throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                $"A commit for workflow instance '{commit.InstanceId}' reached the run of '{instance.InstanceId}'");
        if (commit.Epoch != epoch)
            return new CheckpointReply(CheckpointReplyKind.Conflict, Error: new WorkflowEngineException(
                WorkflowErrorKind.InvalidState,
                $"A commit from execution epoch {commit.Epoch} cannot change workflow instance " +
                $"'{commit.InstanceId}' owned at epoch {epoch}"));
        WorkflowCommitResult result;
        try
        {
            result = await _store.CommitWorkflowInstanceAsync(commit);
        }
        catch (WorkflowEngineException error) when (error.Kind == WorkflowErrorKind.Infrastructure)
        {
            return new CheckpointReply(CheckpointReplyKind.Unknown, Error: error);
        }

        switch (result.Status)
        {
            case WorkflowCommitStatus.Applied or WorkflowCommitStatus.AlreadyApplied:
                await MakeParentRunnableAsync(instance);
                return new CheckpointReply(CheckpointReplyKind.Acknowledged, result.Revision,
                    PendingCancellation: result.PendingCancellation);
            case WorkflowCommitStatus.Conflict:
                return new CheckpointReply(CheckpointReplyKind.Conflict, result.Revision,
                    new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                        $"Workflow instance '{commit.InstanceId}' changed at revision {result.Revision}"));
            case WorkflowCommitStatus.StaleEpoch:
                return new CheckpointReply(CheckpointReplyKind.Conflict, result.Revision,
                    new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                        $"Workflow instance '{commit.InstanceId}' has a newer owner than execution epoch {commit.Epoch}"));
            default:
                return new CheckpointReply(CheckpointReplyKind.Retry, Error: new WorkflowEngineException(
                    WorkflowErrorKind.Infrastructure,
                    $"Saving workflow instance '{commit.InstanceId}' was not applied; the store asked for a retry"));
        }
    }

    // A stopped instance as a result: it waits for an operator to release or cancel it.
    private static WorkflowExecutionResult Stopped(WorkflowInstance instance, OperatorStopState stop) => new()
    {
        InstanceId = instance.InstanceId,
        Status = WorkflowExecutionStatus.NeedsResolution,
        TraceEntries = instance.ExecutionHistory.ToList(),
        ErrorMessage = stop.Describe()
    };

    // A claim that finds the instance lost by MaxCrashes owners in a row stops it instead of running
    // it (B-08). Nothing of the workflow runs and its data is not read; the last checkpoint stays.
    // The stop clears the live count, so a released instance gets MaxCrashes new attempts.
    private async Task<WorkflowExecutionResult> QuarantineAsync(WorkflowInstance instance,
        ExecutionCheckpoint checkpoint, WorkflowScopeCatalog scopes, WorkflowExecutionLease owner)
    {
        var stop = new OperatorStopState
        {
            Kind = OperatorStopKind.Quarantine,
            Reason = $"{owner.CrashCount} owners in a row lost it while it was running",
            StoppedAtUtc = Now,
            CrashCount = owner.CrashCount
        };
        checkpoint.OperatorStop = stop;
        instance.Status = WorkflowStatus.NeedsResolution;
        instance.LastError = stop.Reason;
        instance.LastErrorStackTrace = null;
        instance.SuspensionInfo = null;
        // Like the resource stop, this saves the last checkpoint plus a marker, so no limit applies.
        await ExecutionCheckpointWriter.CommitAsync(new HostedRun(this, instance, owner, true), instance,
            checkpoint, null, scopes, Now, preserveSavedWorkflowData: true, enforceLimit: false);
        return Stopped(instance, stop);
    }

    // Children run inline in the parent's slot and scope (D-17 makes them independent later).
    private DirectInstanceClient Children =>
        (DirectInstanceClient?)services.GetService(typeof(DirectInstanceClient))
        ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
            "The workflow engine is not registered");

    /// <summary>The host side of one run: the instance's lease, its saved state and its children.</summary>
    private sealed class HostedRun(InstanceHost host, WorkflowInstance instance, WorkflowExecutionLease? owner,
        bool persistState) : IExecutionHost
    {
        public bool PersistsState => persistState;
        public CancellationToken Drain => host._control.Drain;
        public long Epoch => owner?.Epoch ?? 0;
        public long MaxCheckpointBytes => host._limits.MaxCheckpointBytes;
        public bool ChildStartIsCheckpointed => host._childStartIsCheckpointed;
        public Task? OwnershipLoss => owner?.Loss;
        public Task<CancellationReason>? CancellationSignal => owner?.Cancellation;

        public CancellationReason? ObservedCancellation =>
            owner?.Cancellation.IsCompletedSuccessfully == true ? owner.Cancellation.Result : null;

        public void ThrowIfOwnershipLost() => owner?.ThrowIfLost();

        public Task<CheckpointReply> CommitAsync(CheckpointCommit commit) =>
            host.CommitAsync(instance, commit, Epoch);

        public void ObserveCancellation(CancellationReason reason) => owner?.ObserveCancellation(reason);

        public Task<WorkflowInstance?> ReadChildAsync(string childInstanceId) =>
            host._store.GetWorkflowInstanceAsync(childInstanceId);

        public Task<WorkflowInvocationTransitionResult> InvokeChildAsync(WorkflowStep step,
            WorkflowDefinition definition, WorkflowInstance instance, object workflowData, object? previous,
            string invocationId, CancellationToken cancellationToken) =>
            host._invocations.ExecuteAsync(step, definition, instance, workflowData, previous, invocationId,
                cancellationToken);

        public Task<WorkflowExecutionResult> RecoverChildAsync(string childInstanceId,
            CancellationToken cancellationToken = default) =>
            host.Children.RecoverAsync(childInstanceId, cancellationToken);

        public Task<WorkflowExecutionResult> CancelChildAsync(string childInstanceId, CancellationReason reason,
            CancellationToken cancellationToken) =>
            host.Children.CancelAsync(childInstanceId, reason, cancellationToken);

        public async Task<WorkflowExecutionResult> ResumeChildAsync(string childInstanceId, string? key,
            object @event, string deliveryId, CancellationToken cancellationToken)
        {
            var method = typeof(DirectInstanceClient).GetMethods().Single(candidate =>
                candidate.Name == nameof(DirectInstanceClient.ResumeAsync) && candidate.GetParameters().Length == 5);
            var task = (Task<WorkflowExecutionResult>)method.MakeGenericMethod(@event.GetType())
                .Invoke(host.Children, [childInstanceId, key, @event, deliveryId, cancellationToken])!;
            return await task;
        }
    }
}
