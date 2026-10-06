using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.ExceptionServices;
using System.Reflection;
using IxIFlow.Builders.Interfaces;

namespace IxIFlow.Core.Runtime;

/// <summary>
/// Drives serializable continuations over a structured workflow definition.
/// A wait parks one continuation; the instance becomes idle only when no work can run.
/// </summary>
internal sealed class StructuredWorkflowRunner(
    IServiceProvider services,
    IActivityExecutor activityExecutor,
    IWorkflowInvoker workflowInvoker,
    IWorkflowStateRepository repository,
    IWorkflowVersionRegistry registry,
    InProcessInstanceGate gate,
    ExecutionLeaseSettings? leaseSettings = null)
{
    private readonly ActivityTransitionExecutor _activities = new(activityExecutor);
    private readonly WorkflowInvocationTransitionExecutor _invocations = new(workflowInvoker);
    private readonly bool _childStartIsCheckpointed = workflowInvoker is ICheckpointedWorkflowInvoker;
    private readonly ExecutionLeaseSettings _leaseSettings = leaseSettings ?? ExecutionLeaseSettings.Default;

    public static bool HasStructuredCheckpoint(WorkflowInstance? instance) =>
        instance != null && ExecutionCheckpoint.IsStructured(instance.ExecutionStateJson);

    public async Task<WorkflowExecutionResult> CancelAsync(string instanceId,
        CancellationReason reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(reason);
        cancellationToken.ThrowIfCancellationRequested();
        if (!await repository.RequestCancellationAsync(instanceId, reason))
        {
            var existing = await repository.GetWorkflowInstanceAsync(instanceId);
            if (existing == null)
                throw new KeyNotFoundException($"Workflow instance '{instanceId}' does not exist");
            if (existing.Status is WorkflowStatus.Running or WorkflowStatus.Suspended or
                WorkflowStatus.NeedsResolution)
                throw new InvalidOperationException(
                    "The workflow instance changed while cancellation was requested; retry");
            return ExistingResult(existing);
        }

        var instance = await repository.GetWorkflowInstanceAsync(instanceId);
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
        return await RecoverAsync(instanceId, cancellationToken);
    }

    public async Task<WorkflowExecutionResult> ResolveAsync(string instanceId,
        string invocationId, ActivityResolution resolution, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);
        ArgumentNullException.ThrowIfNull(resolution);
        using var lease = await gate.EnterAsync(instanceId, cancellationToken);
        var instance = await repository.GetWorkflowInstanceAsync(instanceId);
        if (instance?.Status != WorkflowStatus.NeedsResolution || !HasStructuredCheckpoint(instance))
            return Rejected(instanceId, "The workflow instance has no activity awaiting resolution");

        var definition = await registry.GetWorkflowDefinitionAsync(instance.WorkflowName, instance.WorkflowVersion)
            ?? throw new InvalidOperationException("The registered workflow definition is unavailable");
        var scopes = new WorkflowScopeCatalog(definition);
        var checkpoint = ExecutionCheckpoint.Read(instance.ExecutionStateJson);
        if (checkpoint.DefinitionFingerprint != scopes.Fingerprint)
            return Rejected(instanceId, "The registered workflow definition differs from the saved version");
        var continuation = checkpoint.Continuations.SingleOrDefault(item =>
            item.PendingActivity?.Id == invocationId && item.Status == ContinuationStatus.WaitingResolution)
            ?? throw new KeyNotFoundException($"Activity invocation '{invocationId}' is not awaiting resolution");
        var pending = continuation.PendingActivity!;
        var step = scopes.Step(pending.StepId);
        if (step.StepType is not (WorkflowStepType.Activity or WorkflowStepType.WorkflowInvocation))
            throw new NotSupportedException("This step cannot be resolved by an operator");
        var childDataType = step.StepType == WorkflowStepType.WorkflowInvocation
            ? await ResolveChildDataTypeAsync(step)
            : null;
        var decision = PrepareResolution(scopes, step, resolution, childDataType);
        var dataType = WorkflowTypeIdentity.Resolve(instance.WorkflowDataType)
            ?? throw new InvalidOperationException($"Workflow data type '{instance.WorkflowDataType}' is unavailable");
        var workflowData = JsonSerializer.Deserialize(instance.WorkflowDataJson, dataType)
            ?? throw new InvalidOperationException("Saved workflow data cannot be read");

        await using var owner = await WorkflowExecutionLease.TryAcquireAsync(
            repository, instance, _leaseSettings);
        if (owner == null)
            return Busy(instanceId);
        var current = await repository.GetWorkflowInstanceAsync(instanceId);
        if (current?.Revision != instance.Revision || current.Status != instance.Status)
            return current?.Status == WorkflowStatus.Running
                ? Busy(instanceId)
                : Rejected(instanceId, "The activity changed before resolution could be saved");

        pending.Resolution = decision;
        pending.ResolutionReason = null;
        continuation.Status = ContinuationStatus.Active;
        instance.Status = WorkflowStatus.Running;
        var cancellationReason = await repository.GetCancellationRequestAsync(instanceId);
        if (cancellationReason != null && !checkpoint.CancellationRequested &&
            checkpoint.Continuations[0].Status != ContinuationStatus.Completed)
        {
            instance.CancellationReason = cancellationReason;
            checkpoint.CancellationRequested = true;
            CancelTree(checkpoint, checkpoint.Continuations[0]);
        }
        await PersistUnwrappedAsync(instance, checkpoint, workflowData, scopes);
        return await RunAndSaveAsync(definition, instance, checkpoint, workflowData,
            true, owner, cancellationToken);
    }

    public async Task<IReadOnlyList<PendingActivityInfo>> GetPendingActivitiesAsync(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        var instance = await repository.GetWorkflowInstanceAsync(instanceId);
        if (instance?.Status != WorkflowStatus.NeedsResolution || !HasStructuredCheckpoint(instance))
            return [];
        var definition = await registry.GetWorkflowDefinitionAsync(instance.WorkflowName, instance.WorkflowVersion)
            ?? throw new InvalidOperationException("The registered workflow definition is unavailable");
        var scopes = new WorkflowScopeCatalog(definition);
        var checkpoint = ExecutionCheckpoint.Read(instance.ExecutionStateJson);
        if (checkpoint.DefinitionFingerprint != scopes.Fingerprint)
            throw new InvalidOperationException("The registered workflow definition differs from the saved version");
        return checkpoint.Continuations
            .Where(item => item.Status == ContinuationStatus.WaitingResolution && item.PendingActivity != null)
            .Select(item =>
            {
                var pending = item.PendingActivity!;
                var step = scopes.Step(pending.StepId);
                return new PendingActivityInfo(pending.Id, pending.StepId,
                    step.ActivityType?.Name ?? step.Name, pending.ResolutionReason,
                    pending.StartedAtUtc,
                    step.OutputMappings.Where(mapping => mapping.Direction == PropertyMappingDirection.Output)
                        .Select(mapping => mapping.TargetProperty).Distinct().ToArray(),
                    step.StepType is WorkflowStepType.Activity or WorkflowStepType.WorkflowInvocation);
            })
            .ToArray();
    }

    private async Task<Type> ResolveChildDataTypeAsync(WorkflowStep step)
    {
        if (step.WorkflowType != null)
        {
            var contract = step.WorkflowType.GetInterfaces().FirstOrDefault(type =>
                type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IWorkflow<>));
            return contract?.GetGenericArguments()[0]
                ?? throw new InvalidOperationException("The child workflow data type is unavailable");
        }
        var definition = await registry.GetWorkflowDefinitionAsync(step.WorkflowName!, step.WorkflowVersion!.Value);
        return definition?.WorkflowDataType
            ?? throw new InvalidOperationException("The registered child workflow definition is unavailable");
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
            throw new NotSupportedException("Workflow debugging is not implemented");
        if (options.WorkflowRetryPolicy != null)
            throw new NotSupportedException(
                "Workflow-wide retry is not supported; configure retry on a saga or activity step");
        if (options.InstanceId != null && string.IsNullOrWhiteSpace(options.InstanceId))
            throw new ArgumentException("An explicit workflow instance ID cannot be empty", nameof(options));
        if (options.ExecutionTimeout is { } executionTimeout && executionTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options),
                "Execution timeout must be positive");
        if (options.InstanceId != null && !options.PersistState)
            throw new InvalidOperationException("An explicit workflow instance ID requires state persistence");
        var instance = new WorkflowInstance
        {
            InstanceId = options.InstanceId ?? Guid.NewGuid().ToString("N"),
            WorkflowName = definition.Name,
            WorkflowVersion = definition.Version,
            WorkflowDataType = workflowData.GetType().AssemblyQualifiedName!,
            WorkflowDataJson = JsonSerializer.Serialize(workflowData),
            Status = WorkflowStatus.Running,
            CorrelationId = options.CorrelationId ?? Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow,
            StartedAt = DateTime.UtcNow,
            TotalSteps = definition.Steps.Count,
            TracingEnabled = options.EnableTracing,
            Properties = new Dictionary<string, object>(options.Properties)
        };
        var scopes = new WorkflowScopeCatalog(definition);
        if (!options.PersistState && scopes.ContainsWait)
            throw new InvalidOperationException("A workflow with waits requires state persistence");
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

        using var lease = await gate.EnterAsync(instance.InstanceId, cancellationToken);
        await using var owner = options.PersistState
            ? await WorkflowExecutionLease.TryAcquireAsync(repository, instance, _leaseSettings)
                ?? (options.InstanceId == null
                    ? throw new InvalidOperationException("The new workflow instance could not acquire execution ownership")
                    : null)
            : null;
        if (options.PersistState && owner == null)
            return Busy(instance.InstanceId);
        if (options.InstanceId != null)
        {
            var existing = await repository.GetWorkflowInstanceAsync(instance.InstanceId);
            if (existing != null)
            {
                if (existing.WorkflowName != definition.Name || existing.WorkflowVersion != definition.Version ||
                    ExecutionCheckpoint.Read(existing.ExecutionStateJson).DefinitionFingerprint != scopes.Fingerprint)
                    throw new InvalidOperationException(
                        $"Workflow instance '{instance.InstanceId}' belongs to a different definition");
                return ExistingResult(existing);
            }
        }
        instance.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
        instance.ExecutionSnapshot = ExecutionSnapshotFactory.Create(instance, checkpoint, scopes);
        if (options.PersistState)
        {
            await registry.RegisterWorkflowAsync(definition);
            await PersistUnwrappedAsync(instance, checkpoint, workflowData, scopes);
        }
        return await RunAndSaveAsync(definition, instance, checkpoint, workflowData,
            options.PersistState, owner, cancellationToken);
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
            var instance = await repository.GetWorkflowInstanceAsync(instanceId);
            if (deliveryId != null && HasStructuredCheckpoint(instance) &&
                ExecutionCheckpoint.Read(instance!.ExecutionStateJson).AcceptedDeliveries
                    .TryGetValue(deliveryId, out var acceptedHash))
            {
                if (acceptedHash != deliveryHash)
                    throw new InvalidOperationException(
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
            var candidates = checkpoint.Waits.Where(wait =>
                (key == null || wait.Key == key) &&
                WorkflowTypeIdentity.Resolve(wait.EventType)?.IsAssignableFrom(@event.GetType()) == true).ToList();
            if (candidates.Count == 0)
                return Rejected(instanceId, "No active wait matches this event and key");

            var definition = await registry.GetWorkflowDefinitionAsync(instance.WorkflowName, instance.WorkflowVersion);
            if (definition == null)
                return Rejected(instanceId, "The registered workflow definition is unavailable");

            var dataType = WorkflowTypeIdentity.Resolve(instance.WorkflowDataType)
                ?? throw new InvalidOperationException($"Workflow data type '{instance.WorkflowDataType}' is unavailable");
            var workflowData = JsonSerializer.Deserialize(instance.WorkflowDataJson, dataType)
                ?? throw new InvalidOperationException("Saved workflow data cannot be read");
            var scopes = new WorkflowScopeCatalog(definition);
            if (checkpoint.DefinitionFingerprint != scopes.Fingerprint)
                return Rejected(instanceId, "The registered workflow definition differs from the saved version");
            if (HasDueDeadline(checkpoint))
            {
                await using var deadlineOwner = await WorkflowExecutionLease.TryAcquireAsync(
                    repository, instance, _leaseSettings);
                if (deadlineOwner == null)
                    return Busy(instanceId);
                var deadlineCurrent = await repository.GetWorkflowInstanceAsync(instanceId);
                if (deadlineCurrent?.Revision != instance.Revision ||
                    deadlineCurrent.Status != instance.Status)
                    continue;
                instance.Status = WorkflowStatus.Running;
                await PersistUnwrappedAsync(instance, checkpoint, workflowData, scopes);
                var afterDeadline = await RunAndSaveAsync(definition, instance, checkpoint, workflowData,
                    true, deadlineOwner, cancellationToken);
                if (afterDeadline.Status == WorkflowExecutionStatus.Suspended)
                    continue;
                return afterDeadline;
            }
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

            await using var owner = await WorkflowExecutionLease.TryAcquireAsync(
                repository, instance, _leaseSettings);
            if (owner == null)
                return Busy(instanceId);
            var current = await repository.GetWorkflowInstanceAsync(instanceId);
            if (current?.Revision != instance.Revision || current.Status != instance.Status)
            {
                if (current?.Status is WorkflowStatus.Suspended or WorkflowStatus.NeedsResolution)
                    continue;
                return current?.Status == WorkflowStatus.Running
                    ? Busy(instanceId)
                    : Rejected(instanceId, "The wait changed before this event could be accepted");
            }

            var cancellationReason = await repository.GetCancellationRequestAsync(instanceId);
            if (cancellationReason != null && !checkpoint.CancellationRequested)
            {
                instance.CancellationReason = cancellationReason;
                checkpoint.CancellationRequested = true;
                CancelTree(checkpoint, checkpoint.Continuations[0]);
                instance.Status = WorkflowStatus.Running;
                await PersistUnwrappedAsync(instance, checkpoint, workflowData, scopes);
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
            await PersistUnwrappedAsync(instance, checkpoint, workflowData, scopes);

            var resumed = await RunAndSaveAsync(definition, instance, checkpoint, workflowData,
                true, owner, cancellationToken);
            resumed.EventAccepted = wait.ChildWorkflowId == null || acceptedWait.EventAccepted == true;
            return resumed;
        }
        return Busy(instanceId);
    }

    private static string ComputeDeliveryHash<TEvent>(string? key, TEvent @event)
        where TEvent : class
    {
        var type = @event.GetType();
        var payload = $"{WorkflowTypeIdentity.StableName(type)}\n{key}\n" +
            JsonSerializer.Serialize(@event, type);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    public async Task<WorkflowExecutionResult> RecoverAsync(string instanceId, CancellationToken cancellationToken)
    {
        using var lease = await gate.EnterAsync(instanceId, cancellationToken);
        var instance = await repository.GetWorkflowInstanceAsync(instanceId);
        var cancellationReason = await repository.GetCancellationRequestAsync(instanceId);
        var canRecover = instance?.Status is WorkflowStatus.Running or WorkflowStatus.NeedsResolution ||
            instance?.Status == WorkflowStatus.Suspended &&
            (cancellationReason != null || HasDueDeadline(instance) || HasChildWait(instance));
        if (!canRecover || !HasStructuredCheckpoint(instance))
            return Rejected(instanceId, "The workflow instance has no interrupted execution to recover");

        var definition = await registry.GetWorkflowDefinitionAsync(instance.WorkflowName, instance.WorkflowVersion);
        if (definition == null)
            return Rejected(instanceId, "The registered workflow definition is unavailable");
        var dataType = WorkflowTypeIdentity.Resolve(instance.WorkflowDataType)
            ?? throw new InvalidOperationException($"Workflow data type '{instance.WorkflowDataType}' is unavailable");
        var workflowData = JsonSerializer.Deserialize(instance.WorkflowDataJson, dataType)
            ?? throw new InvalidOperationException("Saved workflow data cannot be read");
        var checkpoint = ExecutionCheckpoint.Read(instance.ExecutionStateJson);
        var scopes = new WorkflowScopeCatalog(definition);
        if (checkpoint.DefinitionFingerprint != scopes.Fingerprint)
            return Rejected(instanceId, "The registered workflow definition differs from the saved version");
        await using var owner = await WorkflowExecutionLease.TryAcquireAsync(
            repository, instance, _leaseSettings);
        if (owner == null)
            return Busy(instanceId);
        var current = await repository.GetWorkflowInstanceAsync(instanceId);
        if (current?.Revision != instance.Revision || current.Status != instance.Status)
            return current?.Status == WorkflowStatus.Running
                ? Busy(instanceId)
                : Rejected(instanceId, "The workflow instance changed before recovery could start");
        foreach (var continuation in checkpoint.Continuations.Where(item =>
                     item.PendingActivity != null && item.Status is
                         ContinuationStatus.WaitingResolution or ContinuationStatus.Cancelling))
            continuation.Status = ContinuationStatus.Active;
        instance.Status = WorkflowStatus.Running;
        if (cancellationReason != null && !checkpoint.CancellationRequested &&
            checkpoint.Continuations[0].Status != ContinuationStatus.Completed)
        {
            instance.CancellationReason = cancellationReason;
            checkpoint.CancellationRequested = true;
            checkpoint.UnhandledError = null;
            checkpoint.RuntimeUnhandledError = null;
            CancelTree(checkpoint, checkpoint.Continuations[0]);
        }
        return await RunAndSaveAsync(definition, instance, checkpoint,
            workflowData, true, owner, cancellationToken);
    }

    private async Task<WorkflowExecutionResult> RunAndSaveAsync(
        WorkflowDefinition definition,
        WorkflowInstance instance,
        ExecutionCheckpoint checkpoint,
        object workflowData,
        bool persistState,
        WorkflowExecutionLease? owner,
        CancellationToken cancellationToken)
    {
        var inFlight = new Dictionary<string, RunningActivity>();
        var cancellationWake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationRegistration = cancellationToken.Register(
            () => cancellationWake.TrySetResult());
        var callerCancellation = cancellationToken.CanBeCanceled
            ? cancellationWake.Task
            : null;
        try
        {
            var scopes = new WorkflowScopeCatalog(definition);
            if (checkpoint.DefinitionFingerprint != scopes.Fingerprint)
                throw new InvalidOperationException("The registered workflow definition differs from the saved version");
            if (await ReconcileChildWaitsAsync(checkpoint))
                await PersistAsync(instance, checkpoint, workflowData, scopes, persistState);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owner?.ThrowIfLost();
                var deadlineAdvanced = ProcessExecutionDeadline(instance, checkpoint);
                deadlineAdvanced |= await ProcessDueChildWaitsAsync(checkpoint);
                deadlineAdvanced |= ProcessDueWaits(checkpoint, scopes);
                if (deadlineAdvanced)
                    await PersistAsync(instance, checkpoint, workflowData, scopes, persistState);
                await ApplyCancellationRequestAsync(instance, checkpoint, workflowData,
                    scopes, persistState);
                foreach (var activity in inFlight.Values.Where(item =>
                             item.Continuation.Status == ContinuationStatus.Cancelling))
                    activity.Cancellation.Cancel();

                var unwinding = checkpoint.Continuations.FirstOrDefault(item =>
                    item.Status == ContinuationStatus.Cancelling && !inFlight.ContainsKey(item.Id));
                if (unwinding != null)
                {
                    if (unwinding.PendingActivity != null)
                    {
                        // A committed Start must be resolved before cancellation can discard
                        // the branch or run its compensation and Finally steps.
                        unwinding.Status = ContinuationStatus.Active;
                        await PersistAsync(instance, checkpoint, workflowData, scopes, persistState);
                        continue;
                    }
                    TryScopeTransitions.ContinueCancellation(scopes, unwinding);
                    await PersistAsync(instance, checkpoint, workflowData, scopes, persistState);
                    continue;
                }

                FinishCompletingJoins(checkpoint);

                // Advance every runnable branch to its next asynchronous boundary.
                // This lets a sibling start even when another activity is awaiting it.
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    owner?.ThrowIfLost();
                    if (HasDueDeadline(checkpoint))
                        break;
                    await ApplyCancellationRequestAsync(instance, checkpoint, workflowData,
                        scopes, persistState);
                    if (!checkpoint.CancellationRequested &&
                        owner?.Cancellation.IsCompleted == true &&
                        checkpoint.Continuations[0].Status != ContinuationStatus.Completed)
                        break;
                    var runnable = checkpoint.Continuations.FirstOrDefault(item =>
                        item.Status == ContinuationStatus.Active &&
                        !inFlight.ContainsKey(item.Id) &&
                        (item.RetryAfterUtc == null || item.RetryAfterUtc <= DateTime.UtcNow));
                    if (runnable == null)
                        break;
                    runnable.RetryAfterUtc = null;

                    var position = runnable.Stack[^1];
                    var next = scopes.Steps(position.ScopeId).ElementAtOrDefault(position.NextStepIndex);
                    if (next?.StepType is WorkflowStepType.Activity or WorkflowStepType.WorkflowInvocation)
                    {
                        var sourceData = workflowData;
                        var activityCancellation = new CancellationTokenSource();
                        RunningActivity? running;
                        try
                        {
                            if (next.StepType == WorkflowStepType.Activity)
                            {
                                running = await StartActivityAsync(scopes, checkpoint, runnable, next,
                                    definition, instance, sourceData, workflowData, persistState,
                                    activityCancellation);
                            }
                            else
                            {
                                running = await StartInvocationAsync(scopes, checkpoint, runnable, next,
                                    definition, instance, sourceData, workflowData, persistState,
                                    activityCancellation);
                            }
                        }
                        catch
                        {
                            activityCancellation.Dispose();
                            throw;
                        }
                        if (running != null)
                            inFlight.Add(runnable.Id, running);
                        else
                            activityCancellation.Dispose();
                    }
                    else
                    {
                        try
                        {
                            Advance(scopes, checkpoint, runnable, workflowData);
                        }
                        catch (Exception error)
                        {
                            if (!CaptureFailure(scopes, checkpoint, runnable, error))
                                throw;
                        }
                        await PersistAsync(instance, checkpoint, workflowData, scopes, persistState);
                        foreach (var activity in inFlight.Values.Where(item =>
                                     item.Continuation.Status == ContinuationStatus.Cancelling))
                            activity.Cancellation.Cancel();
                    }
                }

                if (!checkpoint.CancellationRequested && owner?.Cancellation.IsCompleted == true &&
                    checkpoint.Continuations[0].Status != ContinuationStatus.Completed)
                    continue;
                if (checkpoint.Continuations.Any(item =>
                        item.Status == ContinuationStatus.Cancelling && !inFlight.ContainsKey(item.Id)) ||
                    checkpoint.Joins.Any(item => item.IsCompleting &&
                        checkpoint.Continuations.Where(child => item.ChildContinuationIds.Contains(child.Id))
                            .All(child => child.Status is ContinuationStatus.Completed or ContinuationStatus.Cancelled)))
                    continue;
                if (HasDueDeadline(checkpoint))
                    continue;
                var nextRetry = checkpoint.Continuations
                    .Where(item => item.Status == ContinuationStatus.Active &&
                                   item.RetryAfterUtc > DateTime.UtcNow)
                    .Min(item => item.RetryAfterUtc);
                var nextWaitDeadline = checkpoint.Waits
                    .Where(wait => wait.DeadlineUtc > DateTime.UtcNow)
                    .Min(wait => wait.DeadlineUtc);
                if (inFlight.Count == 0 && nextRetry == null)
                {
                    if (await ApplyCancellationRequestAsync(instance, checkpoint, workflowData,
                            scopes, persistState))
                        continue;
                    break;
                }

                foreach (var activity in inFlight.Values.Where(item =>
                             item.Continuation.Status == ContinuationStatus.Cancelling))
                    activity.Cancellation.Cancel();

                var runningTasks = inFlight.Values.Select(item => (Task)item.Task).ToList();
                using var retryTimerCancellation = new CancellationTokenSource();
                Task? retryTimer = null;
                var nextWake = new[] { nextRetry,
                        inFlight.Count > 0 ? nextWaitDeadline : null,
                        checkpoint.TimedOut ? null : checkpoint.ExecutionDeadlineUtc }
                    .Where(due => due != null).Min();
                if (nextWake is { } retryAt)
                {
                    var delay = retryAt - DateTime.UtcNow;
                    if (delay < TimeSpan.Zero)
                        delay = TimeSpan.Zero;
                    retryTimer = Task.Delay(delay > TimeSpan.FromDays(1)
                        ? TimeSpan.FromDays(1) : delay, retryTimerCancellation.Token);
                    runningTasks.Add(retryTimer);
                }
                if (!checkpoint.CancellationRequested && callerCancellation != null)
                    runningTasks.Add(callerCancellation);
                if (owner != null)
                {
                    runningTasks.Add(owner.Loss);
                    if (!checkpoint.CancellationRequested &&
                        checkpoint.Continuations[0].Status != ContinuationStatus.Completed)
                        runningTasks.Add(owner.Cancellation);
                }
                var finished = await Task.WhenAny(runningTasks);
                if (finished != retryTimer)
                    retryTimerCancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
                if (finished == retryTimer || finished == callerCancellation ||
                    finished == owner?.Cancellation)
                    continue;
                owner?.ThrowIfLost();
                var completedActivity = inFlight.Values.First(item => item.Task == finished);
                try
                {
                    await SettleAsync(scopes, checkpoint, completedActivity, inFlight, workflowData, instance);
                }
                catch (CheckpointPersistenceException)
                {
                    throw;
                }
                catch (Exception error)
                {
                    if (!CaptureFailure(scopes, checkpoint, completedActivity.Continuation,
                            error, !completedActivity.ActivityReturned))
                        throw;
                }
                await PersistAsync(instance, checkpoint, workflowData, scopes, persistState);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (checkpoint.UnhandledError != null && checkpoint.Waits.Count == 0 && checkpoint.Joins.Count == 0)
                throw checkpoint.RuntimeUnhandledError
                      ?? (checkpoint.UnhandledError.IsRestorable
                          ? checkpoint.UnhandledError.Restore()
                          : new InvalidOperationException(checkpoint.UnhandledError.Message));

            if (checkpoint.CancellationRequested &&
                checkpoint.Continuations[0].Status == ContinuationStatus.Cancelled)
            {
                instance.Status = checkpoint.TimedOut
                    ? WorkflowStatus.TimedOut : WorkflowStatus.Cancelled;
                instance.CompletedAt = DateTime.UtcNow;
            }
            else if (checkpoint.Continuations[0].Status == ContinuationStatus.Completed)
            {
                instance.Status = WorkflowStatus.Completed;
                instance.CompletedAt = DateTime.UtcNow;
            }
            else if (checkpoint.Continuations.Any(item =>
                         item.Status == ContinuationStatus.WaitingResolution))
            {
                instance.Status = WorkflowStatus.NeedsResolution;
            }
            else if (checkpoint.Waits.Count > 0)
            {
                instance.Status = WorkflowStatus.Suspended;
            }
            else
            {
                throw new InvalidOperationException("Execution stopped without a runnable continuation or wait");
            }

            instance.SuspensionInfo = checkpoint.Waits.Count == 1
                ? new SuspensionInfo
                {
                    SuspendReason = checkpoint.Waits[0].Key,
                    ResumeEventType = checkpoint.Waits[0].EventType,
                    SuspendedAt = checkpoint.Waits[0].RegisteredAtUtc
                }
                : null;
            await PersistAsync(instance, checkpoint, workflowData, scopes, persistState);

            return new WorkflowExecutionResult
            {
                InstanceId = instance.InstanceId,
                Status = instance.Status switch
                {
                    WorkflowStatus.Completed => WorkflowExecutionStatus.Success,
                    WorkflowStatus.Cancelled => WorkflowExecutionStatus.Cancelled,
                    WorkflowStatus.TimedOut => WorkflowExecutionStatus.TimedOut,
                    WorkflowStatus.NeedsResolution => WorkflowExecutionStatus.NeedsResolution,
                    _ => WorkflowExecutionStatus.Suspended
                },
                WorkflowData = workflowData,
                TraceEntries = instance.ExecutionHistory.ToList(),
                ErrorMessage = instance.Status switch
                {
                    WorkflowStatus.NeedsResolution => checkpoint.Continuations
                        .First(item => item.Status == ContinuationStatus.WaitingResolution)
                        .PendingActivity?.ResolutionReason,
                    WorkflowStatus.Suspended when checkpoint.Waits.Count == 1 =>
                        $"Workflow suspended: {checkpoint.Waits[0].Key}",
                    WorkflowStatus.Suspended =>
                        $"Workflow suspended with {checkpoint.Waits.Count} active waits",
                    _ => null
                },
                ExecutionTime = DateTime.UtcNow - instance.StartedAt!.Value
            };
        }
        catch (ExecutionLeaseLostException)
        {
            foreach (var activity in inFlight.Values)
                activity.Cancellation.Cancel();
            try
            {
                await Task.WhenAll(inFlight.Values.Select(item => item.Task));
            }
            catch
            {
                // The former owner cannot commit these activity outcomes.
            }
            finally
            {
                foreach (var activity in inFlight.Values)
                    activity.Cancellation.Dispose();
            }
            return Busy(instance.InstanceId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            foreach (var activity in inFlight.Values)
                activity.Cancellation.Cancel();
            try
            {
                await Task.WhenAll(inFlight.Values.Select(item => item.Task));
                var scopes = new WorkflowScopeCatalog(definition);
                foreach (var activity in inFlight.Values.ToArray())
                {
                    if (activity.Task.Result is { Kind: ActivityRunKind.Threw,
                            Error: OperationCanceledException })
                        continue;
                    try
                    {
                        await SettleAsync(scopes, checkpoint, activity, inFlight, workflowData, instance);
                    }
                    catch (Exception error) when (error is not CheckpointPersistenceException)
                    {
                        if (!CaptureFailure(scopes, checkpoint, activity.Continuation,
                                error, !activity.ActivityReturned))
                        {
                            checkpoint.UnhandledError = SerializedException.From(error);
                            checkpoint.RuntimeUnhandledError = error;
                            activity.Continuation.Status = ContinuationStatus.Completed;
                        }
                    }
                    await PersistAsync(instance, checkpoint, workflowData, scopes, persistState);
                }
            }
            finally
            {
                foreach (var activity in inFlight.Values)
                    activity.Cancellation.Dispose();
            }
            throw;
        }
        catch (CheckpointPersistenceException ex)
        {
            foreach (var activity in inFlight.Values)
                activity.Cancellation.Cancel();
            try
            {
                await Task.WhenAll(inFlight.Values.Select(item => item.Task));
            }
            catch
            {
                // Activity outcomes are ignored after the checkpoint write failed.
            }
            finally
            {
                foreach (var activity in inFlight.Values)
                    activity.Cancellation.Dispose();
            }

            ExceptionDispatchInfo.Capture(ex.InnerException!).Throw();
            throw;
        }
        catch (Exception ex)
        {
            foreach (var activity in inFlight.Values)
                activity.Cancellation.Cancel();
            try
            {
                await Task.WhenAll(inFlight.Values.Select(item => item.Task));
            }
            catch
            {
                // The original execution error is reported below.
            }
            finally
            {
                foreach (var activity in inFlight.Values)
                    activity.Cancellation.Dispose();
            }

            var cancelled = ex is OperationCanceledException && cancellationToken.IsCancellationRequested;
            if (!cancelled)
                checkpoint.UnhandledError ??= SerializedException.From(ex);
            instance.Status = cancelled ? WorkflowStatus.Cancelled : WorkflowStatus.Failed;
            instance.SuspensionInfo = null;
            checkpoint.Waits.Clear();
            checkpoint.Joins.Clear();
            foreach (var continuation in checkpoint.Continuations.Where(item =>
                         item.Status != ContinuationStatus.Completed))
                continuation.Status = ContinuationStatus.Cancelled;
            instance.LastError = ex.Message;
            instance.LastErrorStackTrace = ex.StackTrace;
            instance.CompletedAt = DateTime.UtcNow;
            if (instance.ExecutionSnapshot != null)
            {
                instance.ExecutionSnapshot.Status = instance.Status;
                instance.ExecutionSnapshot.Pointers.Clear();
                instance.ExecutionSnapshot.Frames.Clear();
            }
            if (persistState)
            {
                var scopes = new WorkflowScopeCatalog(definition);
                try
                {
                    await PersistUnwrappedAsync(instance, checkpoint, workflowData, scopes);
                }
                catch (CheckpointDataException)
                {
                    // A terminal failure cannot resume. Retain the last committed data
                    // and the execution history even if live state cannot be serialized.
                    checkpoint.Continuations.Clear();
                    checkpoint.UnhandledError = null;
                    await PersistUnwrappedAsync(instance, checkpoint, workflowData, scopes,
                        preserveSavedWorkflowData: true);
                }
            }
            return new WorkflowExecutionResult
            {
                InstanceId = instance.InstanceId,
                Status = cancelled ? WorkflowExecutionStatus.Cancelled : WorkflowExecutionStatus.Faulted,
                WorkflowData = workflowData,
                TraceEntries = instance.ExecutionHistory.ToList(),
                ErrorMessage = ex.Message,
                ErrorType = SerializedException.CatchType(ex).FullName,
                ErrorStackTrace = ex.StackTrace
            };
        }
    }

    private async Task<bool> ApplyCancellationRequestAsync(WorkflowInstance instance,
        ExecutionCheckpoint checkpoint, object workflowData, WorkflowScopeCatalog scopes,
        bool persistState)
    {
        if (!persistState || checkpoint.CancellationRequested)
            return false;
        CancellationReason? requested;
        try
        {
            requested = await repository.GetCancellationRequestAsync(instance.InstanceId);
        }
        catch (Exception)
        {
            // The lease watcher tolerates a short store outage and detects a
            // persistent one. Keep committed activity attempts in this owner.
            return false;
        }
        if (requested == null)
            return false;
        var root = checkpoint.Continuations[0];
        if (root.Status == ContinuationStatus.Completed)
            return false;
        instance.CancellationReason = requested;
        checkpoint.CancellationRequested = true;
        checkpoint.UnhandledError = null;
        checkpoint.RuntimeUnhandledError = null;
        CancelTree(checkpoint, root);
        await PersistAsync(instance, checkpoint, workflowData, scopes, persistState);
        return true;
    }

    private static bool HasDueDeadline(WorkflowInstance instance) =>
        HasStructuredCheckpoint(instance) &&
        HasDueDeadline(ExecutionCheckpoint.Read(instance.ExecutionStateJson));

    private static bool HasChildWait(WorkflowInstance instance) =>
        HasStructuredCheckpoint(instance) &&
        ExecutionCheckpoint.Read(instance.ExecutionStateJson).Waits.Any(wait =>
            wait.ChildWorkflowId != null);

    private static bool HasDueDeadline(ExecutionCheckpoint checkpoint) =>
        (!checkpoint.CancellationRequested &&
         checkpoint.ExecutionDeadlineUtc <= DateTime.UtcNow) ||
        checkpoint.Waits.Any(wait => wait.DeadlineUtc <= DateTime.UtcNow);

    private static bool ProcessExecutionDeadline(WorkflowInstance instance,
        ExecutionCheckpoint checkpoint)
    {
        if (checkpoint.CancellationRequested || checkpoint.TimedOut ||
            checkpoint.ExecutionDeadlineUtc > DateTime.UtcNow ||
            checkpoint.ExecutionDeadlineUtc == null ||
            checkpoint.Continuations[0].Status is ContinuationStatus.Completed or ContinuationStatus.Cancelled)
            return false;
        checkpoint.TimedOut = true;
        checkpoint.CancellationRequested = true;
        instance.CancellationReason = new CancellationReason
        {
            ReasonCode = "execution_timeout",
            Description = "The workflow execution deadline expired",
            RequestedAt = DateTime.UtcNow
        };
        CancelTree(checkpoint, checkpoint.Continuations[0]);
        return true;
    }

    private static bool ProcessDueWaits(ExecutionCheckpoint checkpoint,
        WorkflowScopeCatalog scopes)
    {
        var due = checkpoint.Waits.Where(wait => wait.ChildWorkflowId == null &&
            wait.DeadlineUtc <= DateTime.UtcNow).ToArray();
        foreach (var wait in due)
        {
            var continuation = checkpoint.Continuations.Single(item => item.Id == wait.ContinuationId);
            var step = scopes.Step(wait.StepId);
            if (continuation.Status != ContinuationStatus.Waiting ||
                step.StepType != WorkflowStepType.Delay && step.TimeoutSteps.Count == 0)
                throw new InvalidOperationException("A due wait has no active timeout branch");
            checkpoint.Waits.Remove(wait);
            continuation.Status = ContinuationStatus.Active;
            if (step.StepType == WorkflowStepType.Delay)
            {
                continuation.Stack[^1].NextStepIndex++;
                continue;
            }
            continuation.Stack.Add(new ScopePosition
            {
                ScopeId = scopes.TimeoutScope(step),
                EntryPrevious = continuation.Previous,
                RuntimeEntryPrevious = continuation.RuntimePrevious,
                RestorePreviousOnExit = true,
                ExitParentScopeOnExit = true
            });
        }
        return due.Length != 0;
    }

    private async Task<bool> ProcessDueChildWaitsAsync(ExecutionCheckpoint checkpoint)
    {
        var due = checkpoint.Waits.Where(wait => wait.ChildWorkflowId != null &&
            wait.DeadlineUtc <= DateTime.UtcNow)
            .GroupBy(wait => (wait.ContinuationId, wait.ChildWorkflowId))
            .ToArray();
        if (due.Length == 0)
            return false;
        var engine = (IWorkflowEngine?)services.GetService(typeof(IWorkflowEngine))
            ?? throw new InvalidOperationException("The workflow engine is not registered");
        foreach (var childWaits in due)
        {
            var childId = childWaits.Key.ChildWorkflowId!;
            var result = await engine.RecoverWorkflowAsync(childId);
            if (result.Status == WorkflowExecutionStatus.Running)
            {
                foreach (var wait in childWaits)
                    wait.DeadlineUtc = DateTime.UtcNow.AddSeconds(5);
                continue;
            }
            if (result.Status == WorkflowExecutionStatus.NeedsResolution)
            {
                foreach (var wait in childWaits)
                    wait.DeadlineUtc = null;
                var unresolved = checkpoint.Continuations.Single(item =>
                    item.Id == childWaits.Key.ContinuationId);
                unresolved.Status = ContinuationStatus.WaitingResolution;
                unresolved.PendingActivity!.ResolutionReason =
                    $"Child workflow '{childId}' needs activity resolution";
                continue;
            }
            checkpoint.Waits.RemoveAll(wait => wait.ChildWorkflowId == childId &&
                wait.ContinuationId == childWaits.Key.ContinuationId);
            var continuation = checkpoint.Continuations.Single(item =>
                item.Id == childWaits.Key.ContinuationId);
            continuation.Status = ContinuationStatus.Active;
        }
        return true;
    }

    private async Task<bool> ReconcileChildWaitsAsync(ExecutionCheckpoint checkpoint)
    {
        var changed = false;
        var childWaits = checkpoint.Waits.Where(wait => wait.ChildWorkflowId != null)
            .GroupBy(wait => (wait.ContinuationId, wait.ChildWorkflowId)).ToArray();
        foreach (var group in childWaits)
        {
            var child = await repository.GetWorkflowInstanceAsync(group.Key.ChildWorkflowId!);
            if (child == null)
                throw new InvalidOperationException(
                    $"Saved child workflow '{group.Key.ChildWorkflowId}' is missing");
            var continuation = checkpoint.Continuations.Single(item =>
                item.Id == group.Key.ContinuationId);
            if (child.Status is WorkflowStatus.Completed or WorkflowStatus.Failed or
                WorkflowStatus.Cancelled or WorkflowStatus.TimedOut or WorkflowStatus.Terminated)
            {
                checkpoint.Waits.RemoveAll(wait => wait.ChildWorkflowId == child.InstanceId &&
                    wait.ContinuationId == continuation.Id);
                continuation.Status = ContinuationStatus.Active;
                changed = true;
            }
            else if (child.Status == WorkflowStatus.NeedsResolution &&
                     continuation.Status != ContinuationStatus.WaitingResolution)
            {
                continuation.Status = ContinuationStatus.WaitingResolution;
                continuation.PendingActivity!.ResolutionReason =
                    $"Child workflow '{child.InstanceId}' needs activity resolution";
                changed = true;
            }
            else if (child.Status == WorkflowStatus.Suspended)
            {
                var childCheckpoint = ExecutionCheckpoint.Read(child.ExecutionStateJson);
                var savedWaitIds = group.Select(wait => wait.ChildWaitId)
                    .OrderBy(id => id, StringComparer.Ordinal);
                var currentWaitIds = childCheckpoint.Waits.Select(wait => wait.Id)
                    .OrderBy(id => id, StringComparer.Ordinal);
                if (continuation.Status != ContinuationStatus.Waiting ||
                    !savedWaitIds.SequenceEqual(currentWaitIds))
                {
                    ProjectChildWaits(checkpoint, continuation, child.InstanceId, childCheckpoint);
                    continuation.PendingActivity!.ResolutionReason = null;
                    changed = true;
                }
            }
        }
        return changed;
    }

    private void Advance(
        WorkflowScopeCatalog scopes,
        ExecutionCheckpoint checkpoint,
        ContinuationState continuation,
        object workflowData)
    {
        if (continuation.AcceptedWait is { } accepted)
        {
            var acceptedPosition = continuation.Stack[^1];
            var acceptedStep = scopes.Steps(acceptedPosition.ScopeId)[acceptedPosition.NextStepIndex];
            if (scopes.Id(acceptedStep) != accepted.StepId)
                throw new InvalidOperationException("The accepted event does not match its saved wait");
            continuation.AcceptedWait = null;
            var @event = accepted.Event.Read(services)
                ?? throw new InvalidOperationException("The accepted event could not be restored");
            SagaScopeTransitions.RecordAcceptedWait(continuation, accepted.StepId, @event);
            WorkflowValueBinding.ApplyEventOutputs(acceptedStep, @event, workflowData);
            continuation.SetPrevious(@event);
            acceptedPosition.NextStepIndex++;
            return;
        }

        var position = continuation.Stack[^1];
        var steps = scopes.Steps(position.ScopeId);
        if (position.NextStepIndex == steps.Count)
        {
            continuation.Stack.RemoveAt(continuation.Stack.Count - 1);
            if (position.RestorePreviousOnExit)
            {
                continuation.Previous = position.EntryPrevious;
                continuation.RuntimePrevious = position.RuntimeEntryPrevious;
            }
            if (continuation.Stack.Count == 0)
            {
                continuation.Status = ContinuationStatus.Completed;
                CompleteJoin(scopes, checkpoint, continuation, workflowData);
            }
            else
            {
                var parent = continuation.Stack[^1];
                if (position.ExitParentScopeOnExit)
                {
                    parent.NextStepIndex = scopes.Steps(parent.ScopeId).Count;
                    return;
                }
                var currentStep = scopes.Steps(parent.ScopeId)[parent.NextStepIndex];
                if (currentStep.StepType == WorkflowStepType.Loop &&
                    position.ScopeId == scopes.LoopScope(currentStep))
                {
                    // Revisit the loop guard with the completed iteration recorded on its parent.
                }
                else if (!TryScopeTransitions.Exit(scopes, parent, position, currentStep, continuation) &&
                         !SagaScopeTransitions.Exit(scopes, parent, position, currentStep, continuation))
                    parent.NextStepIndex++;
            }
            return;
        }

        var step = steps[position.NextStepIndex];
        switch (step.StepType)
        {
            case WorkflowStepType.Sequence:
                continuation.Stack.Add(new ScopePosition
                {
                    ScopeId = scopes.SequenceScope(step),
                    EntryPrevious = continuation.Previous,
                    RuntimeEntryPrevious = continuation.RuntimePrevious,
                    RestorePreviousOnExit = true
                });
                break;

            case WorkflowStepType.Conditional:
                if (step.OutcomeSelector != null)
                {
                    var outcomeContext = WorkflowValueBinding.EvaluationContext(workflowData,
                        step.PreviousStepDataType, continuation.GetPrevious(services));
                    var outcome = step.OutcomeSelector(outcomeContext);
                    var selectedOutcome = step.OutcomeBranches.FindIndex(branch =>
                        !branch.IsDefault && Equals(branch.Value, outcome));
                    if (selectedOutcome < 0)
                        selectedOutcome = step.OutcomeBranches.FindIndex(branch => branch.IsDefault);
                    if (selectedOutcome < 0)
                        throw new InvalidOperationException(
                            $"Outcome step '{step.Id}' has no branch for '{outcome}'");
                    continuation.Stack.Add(new ScopePosition
                    {
                        ScopeId = scopes.OutcomeScope(step, selectedOutcome),
                        EntryPrevious = continuation.Previous,
                        RuntimeEntryPrevious = continuation.RuntimePrevious,
                        RestorePreviousOnExit = true
                    });
                    break;
                }
                var conditionContext = WorkflowValueBinding.EvaluationContext(workflowData, step.PreviousStepDataType,
                    continuation.GetPrevious(services));
                var selected = step.CompiledCondition?.Invoke(conditionContext)
                    ?? throw new InvalidOperationException($"If step '{step.Id}' has no condition");
                continuation.Stack.Add(new ScopePosition
                {
                    ScopeId = selected
                        ? scopes.ThenScope(step)
                        : scopes.ElseScope(step),
                    EntryPrevious = continuation.Previous,
                    RuntimeEntryPrevious = continuation.RuntimePrevious,
                    RestorePreviousOnExit = true
                });
                break;

            case WorkflowStepType.Parallel:
                Fork(scopes, step, checkpoint, continuation);
                break;

            case WorkflowStepType.TryCatch:
                TryScopeTransitions.Enter(scopes, step, position, continuation);
                break;

            case WorkflowStepType.Saga:
                SagaScopeTransitions.Enter(scopes, step, position, continuation, services);
                break;

            case WorkflowStepType.Loop:
                var shouldRun = position.LoopIterationCount == 0 && step.LoopType == LoopType.DoWhile;
                if (!shouldRun)
                {
                    var loopContext = WorkflowValueBinding.EvaluationContext(workflowData,
                        step.PreviousStepDataType, continuation.GetPrevious(services));
                    shouldRun = step.CompiledCondition?.Invoke(loopContext)
                        ?? throw new InvalidOperationException($"Loop step '{step.Id}' has no condition");
                }
                if (!shouldRun)
                {
                    position.LoopIterationCount = 0;
                    position.NextStepIndex++;
                    break;
                }
                position.LoopIterationCount++;
                continuation.Stack.Add(new ScopePosition
                {
                    ScopeId = scopes.LoopScope(step),
                    EntryPrevious = continuation.Previous,
                    RuntimeEntryPrevious = continuation.RuntimePrevious,
                    RestorePreviousOnExit = true
                });
                break;

            case WorkflowStepType.SuspendResume when step.StepMetadata.TryGetValue("WaitKey", out var keyValue):
                TryScopeTransitions.EnsureCanWait(checkpoint, continuation);
                if (SagaScopeTransitions.TryReuseAcceptedWait(continuation, scopes.Id(step),
                        services, out var acceptedEvent))
                {
                    WorkflowValueBinding.ApplyEventOutputs(step, acceptedEvent!, workflowData);
                    continuation.SetPrevious(acceptedEvent);
                    position.NextStepIndex++;
                    break;
                }
                var key = keyValue as string
                    ?? throw new InvalidOperationException($"Wait step '{step.Id}' has an invalid key");
                checkpoint.Waits.Add(new WaitState
                {
                    ContinuationId = continuation.Id,
                    StepId = scopes.Id(step),
                    Key = key,
                    DeadlineUtc = step.WaitTimeout is { } timeout
                        ? DateTime.UtcNow.Add(timeout) : null,
                    EventType = step.ResumeEventType?.AssemblyQualifiedName
                        ?? throw new InvalidOperationException($"Wait step '{step.Id}' has no event type")
                });
                continuation.Status = ContinuationStatus.Waiting;
                break;

            case WorkflowStepType.Delay:
                TryScopeTransitions.EnsureCanWait(checkpoint, continuation);
                checkpoint.Waits.Add(new WaitState
                {
                    ContinuationId = continuation.Id,
                    StepId = scopes.Id(step),
                    Key = "delay",
                    DeadlineUtc = DateTime.UtcNow.Add(step.DelayDuration!.Value)
                });
                continuation.Status = ContinuationStatus.Waiting;
                break;

            default:
                throw new NotSupportedException($"Structured execution does not yet support {step.StepType}");
        }

    }

    private async Task<RunningActivity?> StartActivityAsync(
        WorkflowScopeCatalog scopes,
        ExecutionCheckpoint checkpoint,
        ContinuationState continuation,
        WorkflowStep step,
        WorkflowDefinition definition,
        WorkflowInstance instance,
        object sourceData,
        object workflowData,
        bool persistState,
        CancellationTokenSource activityCancellation)
    {
        var pending = continuation.PendingActivity;
        var isRecovery = pending != null;
        if (pending != null && pending.StepId != scopes.Id(step))
            throw new InvalidOperationException("The saved activity does not match its continuation");

        var invocationId = pending?.Id ?? NewInvocationId(instance, continuation, scopes.Id(step));
        var attempt = new ActivityAttemptState
        {
            Kind = pending?.Resolution is { } resolution
                ? $"Operator{resolution.Kind}"
                : isRecovery ? "Recover" : "Execute"
        };
        PreparedActivity prepared;
        object? recoveryState = null;
        try
        {
            var compensationInputs = SagaScopeTransitions.PrepareCompensationActivity(
                scopes, continuation, services);
            prepared = await _activities.PrepareAsync(step, definition, instance, sourceData,
                continuation.GetPrevious(services),
                TryScopeTransitions.CatchFault(checkpoint, continuation, services),
                activityCancellation.Token, invocationId, attempt.Id,
                continuation.Stack[^1].NextStepIndex, !isRecovery,
                pending?.Inputs.CodeInputs,
                compensationInputs, SagaScopeTransitions.ActivityMetadata(scopes, continuation, step));
            if (pending == null)
            {
                recoveryState = (prepared.Activity as IRecoverableActivity)?
                    .CaptureRecoveryStateObject(prepared.Context);
                if (prepared.Activity is IRecoverableActivity && recoveryState == null)
                    throw new InvalidOperationException(
                        $"Activity '{prepared.Activity.GetType().Name}' returned a null recovery state");
                prepared.SetRecoveryState(recoveryState);
                pending = new ActivityInvocationState
                {
                    Id = invocationId,
                    StepId = scopes.Id(step),
                    Inputs = ActivityInputSnapshot.Capture(prepared.Activity, step),
                    RecoveryState = SerializedValue.From(recoveryState)
                };
                continuation.PendingActivity = pending;
            }
            else
            {
                pending.Inputs.Restore(prepared.Activity, services);
                recoveryState = pending.RecoveryState?.Read(services);
                prepared.SetRecoveryState(recoveryState);
                if (pending.Resolution?.Kind == ActivityResolutionKind.Completed)
                {
                    foreach (var (name, value) in pending.Resolution.OutputProperties)
                        prepared.Activity.GetType().GetProperty(name)!
                            .SetValue(prepared.Activity, value?.Read(services));
                }
            }
        }
        catch (Exception error) when (!isRecovery)
        {
            return new RunningActivity(continuation, step,
                Task.FromResult(ActivityRunResult.Threw(error)), activityCancellation,
                null, continuation.Stack[^1].NextStepIndex);
        }
        catch (Exception error)
        {
            pending!.ResolutionReason = $"Activity recovery could not be prepared: {error.Message}";
            continuation.Status = ContinuationStatus.WaitingResolution;
            await PersistAsync(instance, checkpoint, workflowData, scopes, persistState);
            return null;
        }

        if (isRecovery && pending!.Resolution == null && prepared.Activity is not IRecoverableActivity)
        {
            pending!.ResolutionReason = "The activity has no recovery contract";
            continuation.Status = ContinuationStatus.WaitingResolution;
            await PersistAsync(instance, checkpoint, workflowData, scopes, persistState);
            return null;
        }

        pending!.ResolutionReason = null;
        pending.Attempts.Add(attempt);
        var startTrace = new ExecutionTraceEntry
        {
            EntryType = TraceEntryType.ActivityStarted,
            ActivityName = step.ActivityType?.Name ?? "Activity",
            StepNumber = continuation.Stack[^1].NextStepIndex,
            Metadata = new Dictionary<string, object>
            {
                ["InvocationId"] = invocationId,
                ["AttemptId"] = attempt.Id,
                ["Kind"] = attempt.Kind
            }
        };
        if (pending.Resolution != null)
        {
            startTrace.Metadata["DecisionNote"] = pending.Resolution.Note;
            if (pending.Resolution.DecidedBy != null)
                startTrace.Metadata["DecidedBy"] = pending.Resolution.DecidedBy;
        }
        if (instance.TracingEnabled)
            instance.ExecutionHistory.Add(startTrace);
        await PersistAsync(instance, checkpoint, workflowData, scopes, persistState);

        var skipCancelledRecovery = isRecovery && continuation.CancellationUnwind &&
            !TryScopeTransitions.IsInsideFinally(continuation) &&
            !SagaScopeTransitions.IsInsideCompensation(continuation);
        var task = ObserveActivityAsync(prepared, isRecovery, recoveryState, pending.Resolution,
            skipCancelledRecovery, activityCancellation.Token);
        return new RunningActivity(continuation, step, task, activityCancellation,
            prepared, continuation.Stack[^1].NextStepIndex);
    }

    private static async Task<ActivityRunResult> ObserveActivityAsync(
        PreparedActivity prepared,
        bool isRecovery,
        object? recoveryState,
        ActivityResolutionState? operatorResolution,
        bool skipCancelledRecovery,
        CancellationToken cancellationToken)
    {
        if (operatorResolution?.Kind == ActivityResolutionKind.Completed)
            return ActivityRunResult.Returned(prepared.Activity);
        if (operatorResolution?.Kind == ActivityResolutionKind.Failed)
            return ActivityRunResult.Threw(operatorResolution.Failure?.ForPropagation()
                ?? new InvalidOperationException(operatorResolution.Note));
        if (isRecovery)
        {
            ActivityRecoveryResolution resolution;
            try
            {
                resolution = await ((IRecoverableActivity)prepared.Activity)
                    .RecoverObjectAsync(recoveryState, prepared.Context, cancellationToken);
            }
            catch (Exception error)
            {
                return ActivityRunResult.Unresolved(
                    $"Recovery check failed: {error.Message}");
            }

            switch (resolution.Disposition)
            {
                case ActivityRecoveryDisposition.Completed:
                    return ActivityRunResult.Returned(prepared.Activity);
                case ActivityRecoveryDisposition.Unresolved:
                    return ActivityRunResult.Unresolved(resolution.Reason ??
                        "The activity outcome is not known");
                case ActivityRecoveryDisposition.Faulted:
                    return ActivityRunResult.Threw(resolution.Error ??
                        new InvalidOperationException("Recovery did not provide an exception"));
                case ActivityRecoveryDisposition.Execute:
                    if (skipCancelledRecovery)
                        return ActivityRunResult.Unresolved(
                            "Cancellation interrupted the activity before its outcome was confirmed");
                    break;
                default:
                    return ActivityRunResult.Unresolved("Unknown recovery decision");
            }
        }

        try
        {
            await prepared.Activity.ExecuteAsync(prepared.Context, cancellationToken);
            return ActivityRunResult.Returned(prepared.Activity);
        }
        catch (Exception error)
        {
            return ActivityRunResult.Threw(error);
        }
    }

    private static string NewInvocationId(WorkflowInstance instance,
        ContinuationState continuation, string stepId)
    {
        var position = continuation.Stack[^1];
        var identity = $"{instance.InstanceId}|{position.ActivationId}|{stepId}";
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }

    private static async Task<ActivityRunResult> ObserveInvocationAsync(
        Task<WorkflowInvocationTransitionResult> invocation)
    {
        try
        {
            var result = await invocation;
            return result.Suspended
                ? ActivityRunResult.Suspended(result.ChildInstanceId!)
                : ActivityRunResult.Returned(result.Output);
        }
        catch (Exception error)
        {
            return ActivityRunResult.Threw(error);
        }
    }

    private async Task<RunningActivity?> StartInvocationAsync(
        WorkflowScopeCatalog scopes,
        ExecutionCheckpoint checkpoint,
        ContinuationState continuation,
        WorkflowStep step,
        WorkflowDefinition definition,
        WorkflowInstance instance,
        object sourceData,
        object workflowData,
        bool persistState,
        CancellationTokenSource cancellation)
    {
        var pending = continuation.PendingActivity;
        if (pending != null && pending.Resolution == null)
        {
            var savedChild = await repository.GetWorkflowInstanceAsync(pending.Id);
            // The child commits its initial checkpoint before running any activity.
            // No child record means the invocation can be started with the same ID.
            if (savedChild == null && !_childStartIsCheckpointed)
            {
                pending.ResolutionReason =
                    "The child workflow outcome is unknown; resolve it before continuing";
                continuation.Status = ContinuationStatus.WaitingResolution;
                await PersistAsync(instance, checkpoint, workflowData, scopes, persistState);
                return null;
            }
            if (savedChild != null)
                pending.ChildWorkflowId = savedChild.InstanceId;
        }

        pending ??= new ActivityInvocationState { StepId = scopes.Id(step) };
        var attempt = new ActivityAttemptState
        {
            Kind = pending.Resolution == null ? "InvokeWorkflow" :
                $"Operator{pending.Resolution.Kind}"
        };
        pending.Attempts.Add(attempt);
        continuation.PendingActivity = pending;
        var startTrace = new ExecutionTraceEntry
        {
            EntryType = TraceEntryType.ActivityStarted,
            ActivityName = step.Name,
            StepNumber = continuation.Stack[^1].NextStepIndex,
            Metadata = new Dictionary<string, object>
            {
                ["InvocationId"] = pending.Id,
                ["AttemptId"] = attempt.Id,
                ["Kind"] = attempt.Kind
            }
        };
        if (pending.Resolution != null)
        {
            startTrace.Metadata["DecisionNote"] = pending.Resolution.Note;
            if (pending.Resolution.DecidedBy != null)
                startTrace.Metadata["DecidedBy"] = pending.Resolution.DecidedBy;
        }
        if (instance.TracingEnabled)
            instance.ExecutionHistory.Add(startTrace);
        await PersistAsync(instance, checkpoint, workflowData, scopes, persistState);

        if (continuation.CancellationUnwind && pending.Resolution == null &&
            pending.ChildWorkflowId != null)
        {
            var child = await repository.GetWorkflowInstanceAsync(pending.ChildWorkflowId);
            if (child?.Status is WorkflowStatus.Suspended or WorkflowStatus.Running or
                WorkflowStatus.NeedsResolution)
            {
                var engine = (IWorkflowEngine?)services.GetService(typeof(IWorkflowEngine))
                    ?? throw new InvalidOperationException("The workflow engine is not registered");
                var childResult = await engine.CancelWorkflowAsync(pending.ChildWorkflowId,
                    instance.CancellationReason ?? new CancellationReason
                    {
                        ReasonCode = "parent_branch_cancelled",
                        Description = "The parent workflow branch was cancelled"
                    }, cancellation.Token);
                var cancellationOutcome = childResult.Status == WorkflowExecutionStatus.Cancelled
                    ? ActivityRunResult.Skipped()
                    : ActivityRunResult.Unresolved(
                        $"Child workflow '{pending.ChildWorkflowId}' has not completed cancellation");
                return new RunningActivity(continuation, step,
                    Task.FromResult(cancellationOutcome), cancellation, null,
                    continuation.Stack[^1].NextStepIndex);
            }
        }

        Task<ActivityRunResult> task;
        if (pending.Resolution is { } decision)
            task = Task.FromResult(decision.Kind == ActivityResolutionKind.Completed
                ? ActivityRunResult.Returned(decision.InvocationResult?.Read(services))
                : ActivityRunResult.Threw(decision.Failure?.ForPropagation()
                    ?? new InvalidOperationException(decision.Note)));
        else
        {
            var accepted = continuation.AcceptedWait;
            if (accepted?.StepId == scopes.Id(step) && pending.ChildWorkflowId != null)
            {
                var childResult = await ResumeChildWaitAsync(pending.ChildWorkflowId,
                    accepted, cancellation.Token);
                accepted.EventAccepted = childResult.EventAccepted;
                if (childResult.EventAccepted && accepted.DeliveryHash != null)
                    checkpoint.AcceptedDeliveries.TryAdd(accepted.DeliveryId!,
                        accepted.DeliveryHash);
            }
            task = ObserveInvocationAsync(_invocations.ExecuteAsync(step, definition, instance,
                sourceData, continuation.GetPrevious(services), pending.Id, cancellation.Token));
        }
        return new RunningActivity(continuation, step, task,
            cancellation, null, continuation.Stack[^1].NextStepIndex);
    }

    private async Task<WorkflowExecutionResult> ResumeChildWaitAsync(string childInstanceId,
        AcceptedWaitState accepted, CancellationToken cancellationToken)
    {
        var @event = accepted.Event.Read(services)
            ?? throw new InvalidOperationException("A child resume event cannot be null");
        var engine = (IWorkflowEngine?)services.GetService(typeof(IWorkflowEngine))
            ?? throw new InvalidOperationException("The workflow engine is not registered");
        var method = typeof(IWorkflowEngine).GetMethods().Single(candidate =>
            candidate.Name == nameof(IWorkflowEngine.ResumeWorkflowDeliveryAsync));
        var task = (Task<WorkflowExecutionResult>)method.MakeGenericMethod(@event.GetType())
            .Invoke(engine, [childInstanceId, accepted.Key, @event,
                accepted.DeliveryId!, cancellationToken])!;
        return await task;
    }

    private async Task ProjectChildWaitsAsync(ExecutionCheckpoint checkpoint,
        ContinuationState continuation, string childInstanceId)
    {
        var child = await repository.GetWorkflowInstanceAsync(childInstanceId)
            ?? throw new InvalidOperationException($"Child workflow '{childInstanceId}' was not saved");
        if (child.Status != WorkflowStatus.Suspended || !HasStructuredCheckpoint(child))
            throw new InvalidOperationException(
                $"Child workflow '{childInstanceId}' has no saved waits to project");
        var childCheckpoint = ExecutionCheckpoint.Read(child.ExecutionStateJson);
        if (childCheckpoint.Waits.Count == 0)
            throw new InvalidOperationException(
                $"Suspended child workflow '{childInstanceId}' has no active waits");

        ProjectChildWaits(checkpoint, continuation, childInstanceId, childCheckpoint);
    }

    private static void ProjectChildWaits(ExecutionCheckpoint checkpoint,
        ContinuationState continuation, string childInstanceId,
        ExecutionCheckpoint childCheckpoint)
    {

        var pending = continuation.PendingActivity
            ?? throw new InvalidOperationException("A child wait has no parent invocation");
        pending.ChildWorkflowId = childInstanceId;
        checkpoint.Waits.RemoveAll(wait => wait.ChildWorkflowId == childInstanceId &&
            wait.ContinuationId == continuation.Id);
        foreach (var childWait in childCheckpoint.Waits)
            checkpoint.Waits.Add(new WaitState
            {
                ContinuationId = continuation.Id,
                StepId = pending.StepId,
                Key = childWait.Key,
                EventType = childWait.EventType,
                DeadlineUtc = childWait.DeadlineUtc,
                ChildWorkflowId = childInstanceId,
                ChildWaitId = childWait.Id
            });
        continuation.AcceptedWait = null;
        continuation.Status = ContinuationStatus.Waiting;
    }

    private async Task SettleAsync(
        WorkflowScopeCatalog scopes,
        ExecutionCheckpoint checkpoint,
        RunningActivity activity,
        Dictionary<string, RunningActivity> inFlight,
        object workflowData,
        WorkflowInstance instance)
    {
        try
        {
            var outcome = await activity.Task;
            if (outcome.Kind == ActivityRunKind.Unresolved)
            {
                MarkActivityUnresolved(activity, outcome.Reason);
                return;
            }

            if (outcome.Kind == ActivityRunKind.Suspended)
            {
                await ProjectChildWaitsAsync(checkpoint, activity.Continuation,
                    outcome.ChildWorkflowId!);
                return;
            }

            if (outcome.Kind == ActivityRunKind.Skipped)
            {
                MarkActivityEnd(activity, instance, "NotRun");
                activity.Continuation.Status = ContinuationStatus.Cancelling;
                return;
            }

            if (outcome.Kind == ActivityRunKind.Threw)
            {
                MarkActivityEnd(activity, instance, "Threw", outcome.Error);
                throw outcome.Error ?? new InvalidOperationException("Activity failed without an exception");
            }

            activity.ActivityReturned = true;
            var output = outcome.Output;
            try
            {
                // The activity has returned successfully. Record it for saga compensation
                // before applying mappings, which can fail after an external effect.
                try
                {
                    SagaScopeTransitions.CompleteForwardActivity(scopes, activity.Continuation,
                        activity.Step, output, services);
                }
                catch (Exception error)
                {
                    MarkActivityUnresolved(activity,
                        $"Activity returned, but its saga output could not be saved: {error.Message}");
                    return;
                }
                if (activity.Prepared != null)
                    await activity.Prepared.ApplyOutputsAsync();
                else if (activity.Step.StepType == WorkflowStepType.WorkflowInvocation &&
                         activity.Continuation.PendingActivity?.Resolution?.Kind == ActivityResolutionKind.Completed)
                    WorkflowValueBinding.ApplyActivityOutputs(activity.Step, output, workflowData);
                if (activity.Continuation.Status is ContinuationStatus.Cancelling or ContinuationStatus.Cancelled ||
                    activity.Continuation.CancellationUnwind &&
                    !TryScopeTransitions.IsInsideFinally(activity.Continuation) &&
                    !SagaScopeTransitions.IsInsideCompensation(activity.Continuation))
                {
                    MarkActivityEnd(activity, instance, "Returned");
                    activity.Continuation.Status = ContinuationStatus.Cancelling;
                    return;
                }

                SagaScopeTransitions.CompleteCompensationActivity(scopes, activity.Continuation, output);
                if (activity.Step.StepType == WorkflowStepType.WorkflowInvocation)
                    activity.Continuation.AcceptedWait = null;
                activity.Continuation.SetPrevious(output);
                activity.Continuation.Stack[^1].StepRetryCounts.Remove(
                    activity.Continuation.Stack[^1].NextStepIndex);
                activity.Continuation.Stack[^1].NextStepIndex++;
                MarkActivityEnd(activity, instance, "Returned");
            }
            catch (Exception error)
            {
                MarkActivityEnd(activity, instance, "OutputFailed", error);
                throw;
            }
        }
        catch (Exception error) when (
            error is not CheckpointPersistenceException &&
            (activity.Continuation.Status is (ContinuationStatus.Cancelling or ContinuationStatus.Cancelled) ||
             activity.Continuation.CancellationUnwind &&
             !TryScopeTransitions.IsInsideFinally(activity.Continuation) &&
             !SagaScopeTransitions.IsInsideCompensation(activity.Continuation)))
        {
            // The parent join has cancelled this branch.
            activity.Continuation.Status = ContinuationStatus.Cancelling;
        }
        finally
        {
            inFlight.Remove(activity.Continuation.Id);
            activity.Cancellation.Dispose();
        }
    }

    private static void MarkActivityUnresolved(RunningActivity activity, string? reason)
    {
        var pending = activity.Continuation.PendingActivity
            ?? throw new InvalidOperationException("Unresolved activity has no saved Start");
        pending.ResolutionReason = reason;
        pending.Attempts[^1].EndedAtUtc = DateTime.UtcNow;
        pending.Attempts[^1].Observation = "Unresolved";
        activity.Continuation.Status = ContinuationStatus.WaitingResolution;
    }

    private static void MarkActivityEnd(RunningActivity running, WorkflowInstance instance,
        string observation,
        Exception? error = null)
    {
        var pending = running.Continuation.PendingActivity;
        if (pending == null)
            return;
        var attempt = pending.Attempts[^1];
        attempt.EndedAtUtc = DateTime.UtcNow;
        attempt.Observation = observation;
        running.Continuation.PendingActivity = null;
        if (instance.TracingEnabled)
            instance.ExecutionHistory.Add(new ExecutionTraceEntry
        {
            EntryType = error == null ? TraceEntryType.ActivityCompleted : TraceEntryType.ActivityFailed,
            ActivityName = running.Step.ActivityType?.Name ?? running.Step.Name,
            StepNumber = running.StepNumber,
            Duration = attempt.EndedAtUtc - attempt.StartedAtUtc,
            ErrorMessage = error?.Message,
            StackTrace = error?.StackTrace,
            Metadata = new Dictionary<string, object>
            {
                ["InvocationId"] = pending.Id,
                ["AttemptId"] = attempt.Id,
                ["Observation"] = observation
            }
            });
    }

    private enum ActivityRunKind { Returned, Threw, Unresolved, Skipped, Suspended }

    private sealed record ActivityRunResult(
        ActivityRunKind Kind,
        object? Output = null,
        Exception? Error = null,
        string? Reason = null,
        string? ChildWorkflowId = null)
    {
        public static ActivityRunResult Returned(object? output) =>
            new(ActivityRunKind.Returned, Output: output);
        public static ActivityRunResult Threw(Exception error) =>
            new(ActivityRunKind.Threw, Error: error);
        public static ActivityRunResult Unresolved(string reason) =>
            new(ActivityRunKind.Unresolved, Reason: reason);
        public static ActivityRunResult Skipped() => new(ActivityRunKind.Skipped);
        public static ActivityRunResult Suspended(string childWorkflowId) =>
            new(ActivityRunKind.Suspended, ChildWorkflowId: childWorkflowId);
    }

    private sealed record RunningActivity(
        ContinuationState Continuation,
        WorkflowStep Step,
        Task<ActivityRunResult> Task,
        CancellationTokenSource Cancellation,
        PreparedActivity? Prepared,
        int StepNumber)
    {
        public bool ActivityReturned { get; set; }
    }

    private static void Fork(WorkflowScopeCatalog scopes, WorkflowStep step, ExecutionCheckpoint checkpoint,
        ContinuationState parent)
    {
        if (step.ParallelBranches.Count == 0)
            throw new InvalidOperationException($"Parallel step '{step.Id}' has no branches");

        var join = new ParallelJoinState
        {
            StepId = scopes.Id(step),
            ParentContinuationId = parent.Id,
            Mode = step.ParallelJoinMode
        };
        checkpoint.Joins.Add(join);
        parent.Status = ContinuationStatus.Joining;

        for (var index = 0; index < step.ParallelBranches.Count; index++)
        {
            var child = new ContinuationState
            {
                ParentJoinId = join.Id,
                Previous = parent.Previous,
                RuntimePrevious = parent.RuntimePrevious,
                Stack = [new ScopePosition { ScopeId = scopes.BranchScope(step, index) }]
            };
            join.ChildContinuationIds.Add(child.Id);
            checkpoint.Continuations.Add(child);
        }
    }

    private static void CompleteJoin(
        WorkflowScopeCatalog scopes,
        ExecutionCheckpoint checkpoint,
        ContinuationState child,
        object workflowData)
    {
        if (child.ParentJoinId == null)
            return;

        var join = checkpoint.Joins.Single(item => item.Id == child.ParentJoinId);
        if (join.IsCompleting)
            return;
        var children = checkpoint.Continuations.Where(item => join.ChildContinuationIds.Contains(item.Id)).ToList();
        var allCompleted = children.All(item => item.Status == ContinuationStatus.Completed);
        var shouldJoin = join.Mode switch
        {
            ParallelJoinMode.WaitAll => allCompleted,
            ParallelJoinMode.WaitAny => true,
            ParallelJoinMode.WaitConditionally =>
                (scopes.Step(join.StepId).ParallelCompletionCondition?.Invoke(workflowData)
                 ?? throw new InvalidOperationException("Conditional parallel join has no completion condition")) ||
                allCompleted,
            _ => throw new ArgumentOutOfRangeException(nameof(join.Mode))
        };
        if (!shouldJoin)
            return;

        join.IsCompleting = true;
        foreach (var sibling in children.Where(item => item.Status != ContinuationStatus.Completed))
            CancelTree(checkpoint, sibling);
    }

    private static void CancelTree(ExecutionCheckpoint checkpoint, ContinuationState continuation)
    {
        if (continuation.Status is ContinuationStatus.Completed or ContinuationStatus.Cancelled)
            return;
        continuation.CancellationUnwind = true;
        continuation.RetryAfterUtc = null;
        if (continuation.PendingActivity != null &&
            continuation.Status == ContinuationStatus.WaitingResolution)
            return;
        if (TryScopeTransitions.IsInsideFinally(continuation) ||
            SagaScopeTransitions.IsInsideCompensation(continuation))
        {
            TryScopeTransitions.PreserveExistingFinally(continuation);
            SagaScopeTransitions.PreserveCompensation(continuation);
            return;
        }
        checkpoint.Waits.RemoveAll(wait => wait.ContinuationId == continuation.Id);
        continuation.AcceptedWait = null;
        foreach (var join in checkpoint.Joins.Where(item => item.ParentContinuationId == continuation.Id).ToList())
        {
            join.IsCompleting = true;
            foreach (var childId in join.ChildContinuationIds)
                CancelTree(checkpoint, checkpoint.Continuations.Single(item => item.Id == childId));
        }
        continuation.Status = checkpoint.Joins.Any(item => item.ParentContinuationId == continuation.Id)
            ? ContinuationStatus.Joining
            : ContinuationStatus.Cancelling;
    }

    private static void FinishCompletingJoins(ExecutionCheckpoint checkpoint)
    {
        foreach (var join in checkpoint.Joins.Where(item => item.IsCompleting).ToList())
        {
            var children = checkpoint.Continuations.Where(item => join.ChildContinuationIds.Contains(item.Id));
            if (!children.All(item => item.Status is ContinuationStatus.Completed or ContinuationStatus.Cancelled))
                continue;
            var parent = checkpoint.Continuations.Single(item => item.Id == join.ParentContinuationId);
            checkpoint.Joins.Remove(join);
            if (checkpoint.UnhandledError != null)
            {
                var finishingCleanup = TryScopeTransitions.IsInsideFinally(parent) ||
                    SagaScopeTransitions.IsInsideCompensation(parent);
                CancelTree(checkpoint, parent);
                if (finishingCleanup)
                {
                    if (!join.ResumeParentWithoutAdvance)
                        parent.Stack[^1].NextStepIndex++;
                    parent.Status = ContinuationStatus.Active;
                }
            }
            else if (parent.CancellationUnwind && !TryScopeTransitions.IsInsideFinally(parent) &&
                     !SagaScopeTransitions.IsInsideCompensation(parent))
                parent.Status = ContinuationStatus.Cancelling;
            else
            {
                if (!join.ResumeParentWithoutAdvance)
                    parent.Stack[^1].NextStepIndex++;
                parent.Status = ContinuationStatus.Active;
            }
        }
    }

    private static bool CaptureFailure(WorkflowScopeCatalog scopes, ExecutionCheckpoint checkpoint,
        ContinuationState continuation, Exception error, bool allowStepRetry = true)
    {
        if (allowStepRetry && !continuation.CancellationUnwind &&
            SagaScopeTransitions.HandleStepFailure(scopes, continuation, error))
            return true;
        if (continuation.CancellationUnwind)
        {
            if (SagaScopeTransitions.SkipFailedCompensation(scopes, continuation, error))
                return true;
            var cleanupBoundary = continuation.Stack.FindIndex(frame =>
                frame.TryState?.Phase == TryPhase.Finally);
            if (cleanupBoundary >= 0 &&
                CaptureScopeFailure(scopes, continuation, error, cleanupBoundary))
                return true;
            checkpoint.UnhandledError ??= SerializedException.From(error);
            checkpoint.RuntimeUnhandledError ??= error;
            TryScopeTransitions.AbortFailedFinally(continuation);
            TryScopeTransitions.ContinueCancellation(scopes, continuation);
            CancelAncestorJoins(checkpoint, continuation);
            return true;
        }
        while (true)
        {
            if (SagaScopeTransitions.SkipFailedCompensation(scopes, continuation, error) ||
                CaptureScopeFailure(scopes, continuation, error))
                return true;
            TryScopeTransitions.AbortFailedFinally(continuation);
            if (continuation.ParentJoinId == null)
            {
                if (!checkpoint.Joins.Any(join => join.IsCompleting))
                    return false;
                checkpoint.UnhandledError = SerializedException.From(error);
                checkpoint.RuntimeUnhandledError = error;
                continuation.Status = ContinuationStatus.Joining;
                CancelAncestorJoins(checkpoint, continuation);
                return true;
            }

            var join = checkpoint.Joins.Single(item => item.Id == continuation.ParentJoinId);
            join.IsCompleting = true;
            join.ResumeParentWithoutAdvance = true;
            foreach (var childId in join.ChildContinuationIds)
                CancelTree(checkpoint, checkpoint.Continuations.Single(item => item.Id == childId));
            continuation = checkpoint.Continuations.Single(item => item.Id == join.ParentContinuationId);
            if (continuation.CancellationUnwind)
                return CaptureFailure(scopes, checkpoint, continuation, error);
            if (TryScopeTransitions.Capture(scopes, continuation, error))
            {
                checkpoint.UnhandledError = null;
                checkpoint.RuntimeUnhandledError = null;
                continuation.Status = ContinuationStatus.Joining;
                return true;
            }
        }
    }

    private static bool CaptureScopeFailure(WorkflowScopeCatalog scopes,
        ContinuationState continuation, Exception error, int minimumHandlerIndex = -1)
    {
        var tryIndex = TryScopeTransitions.HandlerIndex(scopes, continuation, error);
        var sagaIndex = SagaScopeTransitions.HandlerIndex(continuation);
        if (tryIndex <= minimumHandlerIndex)
            tryIndex = -1;
        if (sagaIndex <= minimumHandlerIndex)
            sagaIndex = -1;
        if (sagaIndex > tryIndex)
        {
            SagaScopeTransitions.Capture(scopes, continuation, sagaIndex, error);
            return true;
        }
        return tryIndex >= 0 && TryScopeTransitions.Capture(scopes, continuation, error);
    }

    private static void CancelAncestorJoins(ExecutionCheckpoint checkpoint,
        ContinuationState continuation)
    {
        while (continuation.ParentJoinId != null)
        {
            var join = checkpoint.Joins.SingleOrDefault(item => item.Id == continuation.ParentJoinId);
            if (join == null)
                return;
            join.IsCompleting = true;
            foreach (var siblingId in join.ChildContinuationIds.Where(id => id != continuation.Id))
                CancelTree(checkpoint,
                    checkpoint.Continuations.Single(item => item.Id == siblingId));
            continuation = checkpoint.Continuations.Single(item => item.Id == join.ParentContinuationId);
        }
    }

    private static WorkflowExecutionResult Rejected(string instanceId, string reason) => new()
    {
        InstanceId = instanceId,
        Status = WorkflowExecutionStatus.Faulted,
        ErrorMessage = reason
    };

    private static WorkflowExecutionResult Busy(string instanceId) => new()
    {
        InstanceId = instanceId,
        Status = WorkflowExecutionStatus.Running,
        ErrorMessage = "The workflow instance is executing on another host"
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

    private async Task PersistUnwrappedAsync(WorkflowInstance instance,
        ExecutionCheckpoint checkpoint, object workflowData, WorkflowScopeCatalog scopes,
        bool preserveSavedWorkflowData = false)
    {
        try
        {
            await PersistAsync(instance, checkpoint, workflowData, scopes, true,
                preserveSavedWorkflowData);
        }
        catch (CheckpointPersistenceException error)
        {
            ExceptionDispatchInfo.Capture(error.InnerException!).Throw();
            throw;
        }
    }

    private async Task PersistAsync(WorkflowInstance instance, ExecutionCheckpoint checkpoint,
        object workflowData, WorkflowScopeCatalog scopes, bool persistState,
        bool preserveSavedWorkflowData = false)
    {
        if (!persistState)
            return;
        try
        {
            PrepareCheckpoint(checkpoint, scopes);
            var dataJson = preserveSavedWorkflowData
                ? instance.WorkflowDataJson
                : JsonSerializer.Serialize(workflowData, workflowData.GetType());
            var stateJson = JsonSerializer.Serialize(checkpoint);
            var snapshot = ExecutionSnapshotFactory.Create(instance, checkpoint, scopes);
            instance.WorkflowDataJson = dataJson;
            instance.ExecutionStateJson = stateJson;
            instance.ExecutionSnapshot = snapshot;
            instance.NextDueAtUtc = instance.Status is WorkflowStatus.Running or
                    WorkflowStatus.Suspended or WorkflowStatus.NeedsResolution
                ? new[] { checkpoint.TimedOut ? null : checkpoint.ExecutionDeadlineUtc }
                    .Concat(checkpoint.Waits.Select(wait => wait.DeadlineUtc))
                    .Concat(checkpoint.Waits.Any(wait => wait.ChildWorkflowId != null)
                        ? [DateTime.UtcNow.AddSeconds(5)] : [])
                    .Where(due => due != null).Min()
                : null;
        }
        catch (Exception ex)
        {
            throw new CheckpointDataException(ex);
        }

        try
        {
            var expectedRevision = instance.Revision;
            var commitId = Guid.NewGuid().ToString("N");
            for (var attempt = 0; attempt < 3; attempt++)
            {
                WorkflowCommitResult result;
                try
                {
                    result = await repository.CommitWorkflowInstanceAsync(
                        instance, expectedRevision, commitId);
                }
                catch (Exception) when (attempt < 2)
                {
                    continue;
                }

                if (result.Status == WorkflowCommitStatus.Conflict)
                    throw new InvalidOperationException(
                        $"Workflow instance '{instance.InstanceId}' changed at revision {result.Revision}");
                instance.Revision = result.Revision;
                return;
            }
        }
        catch (Exception ex)
        {
            throw new CheckpointPersistenceException(ex);
        }
    }

    private sealed class CheckpointDataException(Exception inner) : Exception(
        $"The workflow state could not be checkpointed: {inner.Message}", inner);

    private sealed class CheckpointPersistenceException(Exception inner) : Exception(
        "The workflow checkpoint could not be saved", inner);

    private static void PrepareCheckpoint(ExecutionCheckpoint checkpoint, WorkflowScopeCatalog scopes)
    {
        foreach (var continuation in checkpoint.Continuations)
        {
            if (continuation.Status is ContinuationStatus.Completed or ContinuationStatus.Cancelled)
            {
                continuation.Previous = null;
                continuation.AcceptedWait = null;
                continue;
            }

            // A completed scope restores its entry value, or ends the continuation.
            // Its last activity object will never be read again, so do not require
            // transient input properties on that object to be checkpointable.
            if (continuation.Stack.Count > 0)
            {
                var current = continuation.Stack[^1];
                if (current.NextStepIndex == scopes.Steps(current.ScopeId).Count &&
                    (current.RestorePreviousOnExit || continuation.Stack.Count == 1))
                {
                    continuation.Previous = null;
                    continuation.RuntimePrevious = null;
                }
            }

            if (continuation.RuntimePrevious != null)
                continuation.Previous = SerializedValue.From(continuation.RuntimePrevious);
            foreach (var frame in continuation.Stack)
            {
                if (frame.RuntimeEntryPrevious != null)
                    frame.EntryPrevious = SerializedValue.From(frame.RuntimeEntryPrevious);
                if (frame.TryState?.RuntimeEntryPrevious != null)
                    frame.TryState.EntryPrevious = SerializedValue.From(frame.TryState.RuntimeEntryPrevious);
            }
        }
    }
}
