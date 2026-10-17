using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

public sealed partial class WorkflowHost
{
    /// <summary>
    /// What the host does to one instance, in the scope of a work item: it validates requests, holds the instance gate and execution lease,
    /// commits checkpoints, and prepares the children a run starts. The <see cref="WorkflowExecutor"/>
    /// runs the instance between commits.
    /// </summary>
    internal sealed class InstanceOperations(
        IServiceProvider services,
        IActivityExecutor activityExecutor,
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
        private readonly ChildWorkflows _children = new(services, registry, timeProvider ?? TimeProvider.System);
        private readonly ExecutionLeaseSettings _leaseSettings = leaseSettings ?? ExecutionLeaseSettings.Default;
        private readonly ExecutionLimits _limits = limits ?? ExecutionLimits.Default;
        private readonly RunControl _control = control ?? new RunControl();
        private readonly CancellationWatch _watch = watch ?? new CancellationWatch(repository, timeProvider);
        private readonly WorkflowExecutor _executor = new(services, activityExecutor,
            timeProvider ?? TimeProvider.System);

        private readonly string? _defaultActor = (services.GetService(typeof(ClientActor)) as ClientActor)?.Name;

        private DateTime Now => _time.GetUtcNow().UtcDateTime;

        public static bool HasStructuredCheckpoint(
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] WorkflowInstance? instance) =>
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
            reason = OperatorTrace.WithActor(reason, _defaultActor);
            if (!await _store.RequestCancellationAsync(instanceId, reason))
            {
                var existing = await _store.GetWorkflowInstanceSummaryAsync(instanceId);
                if (existing == null)
                    throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
                        $"Workflow instance '{instanceId}' does not exist");
                if (existing.Status is WorkflowStatus.Running or WorkflowStatus.Suspended or
                    WorkflowStatus.NeedsResolution)
                    throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                        "The workflow instance changed while cancellation was requested; retry");
                return ExistingResult(existing);
            }

            var instance = await _store.GetWorkflowInstanceSummaryAsync(instanceId);
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
            var instance = await _store.GetWorkflowInstanceSummaryAsync(instanceId);
            return instance == null ? null : ReadResult(instance);
        }

        /// <summary>The status of an instance as a result would report it, without reading its data.</summary>
        public async Task<WorkflowExecutionStatus?> ReadStatusAsync(string instanceId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
            var instance = await _store.GetWorkflowInstanceSummaryAsync(instanceId);
            return instance == null ? null : StatusOf(instance.Status);
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
                ? null : await _store.GetWorkflowInstanceSummaryAsync(pending.ChildWorkflowId);
            if (ChildIsActive(child))
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                    $"Resolve child workflow '{pending.ChildWorkflowId}' before recovering its parent invocation");
            var childDataType = step.StepType == WorkflowStepType.WorkflowInvocation
                ? await ResolveChildDataTypeAsync(step)
                : null;
            var decision = PrepareResolution(scopes, step, resolution, childDataType, _defaultActor);
            var dataType = WorkflowTypeIdentity.Resolve(instance.WorkflowDataType)
                ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                    $"Workflow data type '{instance.WorkflowDataType}' is unavailable");
            var workflowData = JsonSerializer.Deserialize(instance.WorkflowDataJson, dataType)
                ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "Saved workflow data cannot be read");

            await using var owner = await WorkflowExecutionLease.TryAcquireAsync(
                _store, instance, _leaseSettings, _watch, _time);
            if (owner == null)
                return WorkflowExecutor.Busy(instanceId);
            var current = await _store.GetWorkflowInstanceSummaryAsync(instanceId);
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
                OperatorTrace.ApplyCancellation(instance, checkpoint, cancellationReason, Now);
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
                    ? null : await _store.GetWorkflowInstanceSummaryAsync(pending.ChildWorkflowId);
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

        /// <summary>The instance's whole trace, in the order its commits stored it (G-01).</summary>
        public async Task<IReadOnlyList<ExecutionTraceEntry>> GetTraceAsync(string instanceId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
            var trace = new List<ExecutionTraceEntry>();
            for (long after = 0;;)
            {
                var page = await _store.GetTraceAsync(instanceId, after);
                trace.AddRange(page.Select(entry => entry.Entry));
                if (page.Count == 0)
                    return trace;
                after = page[^1].Sequence;
            }
        }

        /// <summary>
        /// Where the instance's branches are, computed from its saved checkpoint and its registered definition; none
        /// for an instance that does not exist or has no structured checkpoint.
        /// </summary>
        public async Task<WorkflowExecutionSnapshot?> GetSnapshotAsync(string instanceId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
            var instance = await _store.GetWorkflowInstanceAsync(instanceId);
            if (instance == null || !HasStructuredCheckpoint(instance))
                return null;
            var definition = await registry.GetWorkflowDefinitionAsync(instance.WorkflowName, instance.WorkflowVersion)
                ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                    "The registered workflow definition is unavailable");
            var scopes = new WorkflowScopeCatalog(definition);
            var checkpoint = ExecutionCheckpoint.Read(instance.ExecutionStateJson);
            if (checkpoint.DefinitionFingerprint != scopes.Fingerprint)
                throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                    "The registered workflow definition differs from the saved version");
            return ExecutionSnapshotFactory.Create(instance, checkpoint, scopes, Now);
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
            WorkflowStep step, ActivityResolution resolution, Type? childDataType, string? defaultActor)
        {
            var decision = new ActivityResolutionState
            {
                Kind = resolution.Kind,
                Note = resolution.Note,
                DecidedBy = resolution.DecidedBy ?? defaultActor
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
            var (instance, checkpoint, scopes) = NewInstance(definition, workflowData, options, Now);
            if (!options.PersistState && scopes.RequiresPersistence)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
                    "A workflow with waits or child workflows requires state persistence");

            try
            {
                using var lease = await gate.EnterAsync(instance.InstanceId, cancellationToken);
                // A repeated start of an existing instance is answered without claiming it, so retries
                // never count as crashes of an instance another host lost (B-08).
                if (options.InstanceId != null &&
                    await _store.GetWorkflowInstanceSummaryAsync(instance.InstanceId) is { } started)
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
                    await _store.GetWorkflowInstanceSummaryAsync(instance.InstanceId) is { } existing)
                    return ExistingStart(existing, definition, scopes);
                var registered = await registry.GetWorkflowDefinitionAsync(definition.Name, definition.Version);
                if (registered?.Metadata.TryGetValue("IsActive", out var isActive) == true &&
                    isActive is false)
                    throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                        $"Workflow {definition.Name} v{definition.Version} is deactivated");
                instance.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
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

        /// <summary>
        /// A new instance of <paramref name="definition"/> with <paramref name="workflowData"/>, as a start
        /// saves it, and its initial checkpoint: a client's start, or a child a parent's commit starts (D-17).
        /// </summary>
        public static (WorkflowInstance Instance, ExecutionCheckpoint Checkpoint, WorkflowScopeCatalog Scopes)
            NewInstance(WorkflowDefinition definition, object workflowData, WorkflowOptions options, DateTime now)
        {
            WorkflowDefinitionIdentity.AssignImplicitName(definition);
            var instance = new WorkflowInstance
            {
                InstanceId = options.InstanceId ?? Guid.NewGuid().ToString("N"),
                WorkflowName = definition.Name,
                WorkflowVersion = definition.Version,
                WorkflowDataType = workflowData.GetType().AssemblyQualifiedName!,
                WorkflowDataJson = BranchData.Snapshot(workflowData),
                Status = WorkflowStatus.Running,
                CorrelationId = options.CorrelationId ?? Guid.NewGuid().ToString("N"),
                ParentInstanceId = options.ParentInstanceId,
                CreatedAt = now,
                StartedAt = now,
                TotalSteps = definition.Steps.Count,
                TracingEnabled = options.EnableTracing,
                Properties = new Dictionary<string, object>(options.Properties)
            };
            var scopes = new WorkflowScopeCatalog(definition);
            var checkpoint = new ExecutionCheckpoint
            {
                DefinitionFingerprint = scopes.Fingerprint,
                // The store stamps the execution deadline when the start is committed (D-35); a run that
                // saves nothing keeps its own.
                ExecutionTimeout = options.ExecutionTimeout,
                ExecutionTimerId = options.ExecutionTimeout != null ? Guid.NewGuid().ToString("N") : null,
                ExecutionDeadlineUtc = instance.StartedAt + options.ExecutionTimeout,
                Continuations =
                [
                    new ContinuationState
                    {
                        Stack = [new ScopePosition { ScopeId = "root" }]
                    }
                ]
            };
            return (instance, checkpoint, scopes);
        }

        /// <summary>
        /// Stores an event in the instance's inbox before acknowledging it, then runs the instance so its
        /// owner decides the event at its next pass (D-32, D-33). The result is the instance's state and
        /// the event's receipt. The event stays pending while another host runs the instance or while it is
        /// stopped for an operator; that owner, or the release, decides it.
        /// </summary>
        public async Task<WorkflowExecutionResult> ResumeAsync<TEvent>(
            string instanceId,
            string? key,
            TEvent @event,
            CancellationToken cancellationToken,
            string? eventId = null)
            where TEvent : class
        {
            eventId ??= Guid.NewGuid().ToString("N");
            var appended = await DeliverAsync(instanceId, key, @event, eventId);
            return await AnswerAsync(instanceId, eventId, appended)
                ?? await TakeEventAsync(instanceId, eventId, cancellationToken);
        }

        /// <summary>
        /// Stores an event without running the instance, bypassing its queue and slots (D-32). The caller
        /// runs or wakes the instance; its owner decides the event at its next pass.
        /// </summary>
        public Task<EventAppendResult> DeliverAsync<TEvent>(string instanceId, string? key, TEvent @event,
            string eventId) where TEvent : class =>
            AppendAsync(EventFor(instanceId, key, @event, eventId));

        /// <summary>
        /// The result for an event the inbox answered without keeping it, or null when it was stored for
        /// the owner to decide.
        /// </summary>
        public async Task<WorkflowExecutionResult?> AnswerAsync(string instanceId, string eventId,
            EventAppendResult appended) => appended.Status switch
        {
            EventAppendStatus.NotFound => throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
                $"Workflow instance '{instanceId}' does not exist").At(instanceId),
            EventAppendStatus.Closed => await AnsweredAsync(instanceId,
                new EventReceipt(eventId, EventOutcome.Closed, EventRejection.InstanceFinished)),
            EventAppendStatus.Rejected => await AnsweredAsync(instanceId,
                new EventReceipt(eventId, EventOutcome.Rejected, appended.Rejection)),
            _ => null
        };

        /// <summary>
        /// Runs the instance so its owner decides a stored event, and returns its state with the event's
        /// receipt. The event stays pending while another host owns the instance or it is stopped.
        /// </summary>
        public async Task<WorkflowExecutionResult> TakeEventAsync(string instanceId, string eventId,
            CancellationToken cancellationToken)
        {
            var result = await ContinueAsync(instanceId, release: false, cancellationToken, forEvent: true);
            result.Event = await ReadEventAsync(instanceId, eventId);
            return result;
        }

        /// <summary>What became of an event sent to an instance, or null when it was never stored.</summary>
        public async Task<EventReceipt?> ReadEventAsync(string instanceId, string eventId) =>
            await _store.GetEventAsync(instanceId, eventId) is { } stored ? EventReceipt.Of(stored) : null;

        /// <summary>Open waits that match the query, across instances (D-13, D-14).</summary>
        public Task<IReadOnlyList<StoredWait>> GetOpenWaitsAsync(WaitQuery query) => _store.GetOpenWaitsAsync(query);

        /// <summary>
        /// Stores a reply addressed to one wait by its token's wait ID, with the token's subject (D-33),
        /// without running the instance.
        /// </summary>
        public Task<EventAppendResult> DeliverReplyAsync<TReply>(string instanceId, string waitId, TReply reply,
            string eventId, string? subject) where TReply : class =>
            AppendAsync(EventFor(instanceId, null, reply, eventId, waitId, subject));

        private static WorkflowEvent EventFor<TEvent>(string instanceId, string? key, TEvent @event, string eventId,
            string? targetWaitId = null, string? subject = null)
            where TEvent : class
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
            ArgumentNullException.ThrowIfNull(@event);
            ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
            var type = @event.GetType();
            return new WorkflowEvent(instanceId, eventId, type.AssemblyQualifiedName!,
                JsonSerializer.Serialize(@event, type), key, targetWaitId, subject);
        }

        private async Task<EventAppendResult> AppendAsync(WorkflowEvent stored)
        {
            var appended = await _store.AppendEventAsync(stored);
            return appended.Status == EventAppendStatus.Conflict
                ? throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
                    $"Event ID '{stored.EventId}' was already used for a different event").At(stored.InstanceId)
                : appended;
        }

        // An event answered without running the instance: its state as saved, with the receipt.
        private async Task<WorkflowExecutionResult> AnsweredAsync(string instanceId, EventReceipt receipt)
        {
            var result = await ReadAsync(instanceId) ?? throw new WorkflowEngineException(
                WorkflowErrorKind.InvalidRequest, $"Workflow instance '{instanceId}' does not exist").At(instanceId);
            result.Event = receipt;
            return result;
        }

        public Task<WorkflowExecutionResult> RecoverAsync(string instanceId, CancellationToken cancellationToken) =>
            ContinueAsync(instanceId, release: false, cancellationToken);

        /// <summary>
        /// Releases an instance stopped for an operator and runs it. Automatic recovery, including a
        /// parent checking its child, never releases a stop.
        /// </summary>
        public Task<WorkflowExecutionResult> ReleaseAsync(string instanceId, string? actor,
            CancellationToken cancellationToken) =>
            ContinueAsync(instanceId, release: true, cancellationToken, actor: actor ?? _defaultActor);

        // With forEvent, an instance with nothing to run is answered as it is saved: another run took the
        // event already, or it was stored for an instance that is finished or stopped.
        private async Task<WorkflowExecutionResult> ContinueAsync(string instanceId, bool release,
            CancellationToken cancellationToken, bool forEvent = false, string? actor = null)
        {
            using var lease = await gate.EnterAsync(instanceId, cancellationToken);
            var instance = await _store.GetWorkflowInstanceAsync(instanceId);
            var cancellationReason = await _store.GetCancellationRequestAsync(instanceId);
            var canRecover = instance?.Status is WorkflowStatus.Running or WorkflowStatus.NeedsResolution ||
                instance?.Status == WorkflowStatus.Suspended &&
                (cancellationReason != null || await HasInboxWorkAsync(instanceId));
            // Another run may have decided the event since the instance was read, so it is read again.
            if (!canRecover || !HasStructuredCheckpoint(instance))
                return forEvent && await ReadAsync(instanceId) is { } latest ? latest
                    : Rejected(instanceId, release
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
            var current = await _store.GetWorkflowInstanceSummaryAsync(instanceId);
            if (current?.Revision != instance.Revision || current.Status != instance.Status)
                return current?.Status == WorkflowStatus.Running
                    ? WorkflowExecutor.Busy(instanceId)
                    : Rejected(instanceId, "The workflow instance changed before recovery could start");
            // How long the instance waited to be claimed, on the store's clock as this run last read it (D-39).
            if (instance.RunnableAtUtc is { } runnableAt && checkpoint.Clock.ToStore(Now) - runnableAt is var waited &&
                waited >= TimeSpan.Zero)
                Telemetry.BacklogAge.Record(waited.TotalSeconds, Telemetry.Workflow(instance));
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
                if (release)
                    OperatorTrace.Released(instance, actor, Now);
            }
            // A run for an event only takes it: an activity waiting for an operator stays waiting.
            foreach (var continuation in checkpoint.Continuations.Where(item =>
                         item.PendingActivity is { ChildWorkflowId: null } &&
                         (item.Status == ContinuationStatus.Cancelling ||
                          item.Status == ContinuationStatus.WaitingResolution && (!forEvent || stopCleared))))
                continuation.Status = ContinuationStatus.Active;
            instance.Status = WorkflowStatus.Running;
            if (newCancellation)
                OperatorTrace.ApplyCancellation(instance, checkpoint, cancellationReason!, Now);
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

        // An idle instance has work when an event waits for it or the store finds one of its deadlines due
        // (D-35). A host that looked too early, because its clock runs ahead of the store's, looks again when
        // the store's clock reaches the earliest deadline.
        private async Task<bool> HasInboxWorkAsync(string instanceId)
        {
            var page = await _store.GetPendingEventsAsync(instanceId, 0, 1);
            if (page.Events.Count > 0 || page.Deadlines?.Keys.Any(page.IsDue) == true)
                return true;
            if (page is { StoreTimeUtc: { } storeTime, Deadlines.Count: > 0 })
                _control.Schedule(instanceId, Now + (page.Deadlines.Values.Min() - storeTime));
            return false;
        }

        // A stopped instance is answered without reading its data, which may be what stopped it.
        private static WorkflowExecutionResult ReadResult(WorkflowInstance instance) =>
            ExecutionCheckpoint.StopOf(instance) is { } stop ? Stopped(instance, stop) : ExistingResult(instance);

        private static WorkflowExecutionResult ExistingStart(WorkflowInstance existing, WorkflowDefinition definition,
            WorkflowScopeCatalog scopes)
        {
            var checkpoint = ExecutionCheckpoint.ReadTrunk(existing.ExecutionStateJson);
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

        private static WorkflowExecutionStatus StatusOf(WorkflowStatus status) => status switch
        {
            WorkflowStatus.Completed => WorkflowExecutionStatus.Success,
            WorkflowStatus.Suspended => WorkflowExecutionStatus.Suspended,
            WorkflowStatus.NeedsResolution => WorkflowExecutionStatus.NeedsResolution,
            WorkflowStatus.Failed => WorkflowExecutionStatus.Faulted,
            WorkflowStatus.Cancelled => WorkflowExecutionStatus.Cancelled,
            WorkflowStatus.TimedOut => WorkflowExecutionStatus.TimedOut,
            WorkflowStatus.Terminated => WorkflowExecutionStatus.Failed,
            _ => WorkflowExecutionStatus.Running
        };

        private static WorkflowExecutionResult ExistingResult(WorkflowInstance instance) => new()
        {
            InstanceId = instance.InstanceId,
            Status = StatusOf(instance.Status),
            WorkflowData = WorkflowTypeIdentity.Resolve(instance.WorkflowDataType) is { } dataType
                ? JsonSerializer.Deserialize(instance.WorkflowDataJson, dataType)
                : null,
            TraceEntries = [],
            ErrorMessage = instance.LastError,
            ErrorStackTrace = instance.LastErrorStackTrace
        };

        // Runs nested in this scope's run, such as children and inline client calls, never park: the
        // slot they would need to resume is held by the run they are nested in.
        private int _runDepth;

        private async Task<WorkflowExecutionResult> RunAndSaveAsync(WorkflowDefinition definition,
            WorkflowInstance instance, ExecutionCheckpoint checkpoint, object workflowData, bool persistState,
            WorkflowExecutionLease? owner, CancellationToken cancellationToken)
        {
            var depth = Interlocked.Increment(ref _runDepth);
            try
            {
                var result = await _executor.RunAsync(definition, instance, checkpoint, workflowData,
                    new HostedRun(this, instance, owner, persistState, canPark: depth == 1), cancellationToken);
                // The local host resumes what it saved with a runnable time, on time (C-12), and forgets
                // what has finished.
                if (persistState && checkpoint.LocalWakeUtc is { } due &&
                    result.Status is WorkflowExecutionStatus.Scheduled or WorkflowExecutionStatus.Suspended or
                        WorkflowExecutionStatus.NeedsResolution)
                    _control.Schedule(instance.InstanceId, due);
                else if (persistState && instance.Status is WorkflowStatus.Completed or WorkflowStatus.Failed or
                             WorkflowStatus.Cancelled or WorkflowStatus.TimedOut or WorkflowStatus.Terminated)
                    _control.Unschedule(instance.InstanceId);
                return result;
            }
            finally
            {
                Interlocked.Decrement(ref _runDepth);
            }
        }

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
                    // A child the commit started, or a parent its completion woke, runs here without waiting
                    // for the poll (D-17).
                    foreach (var runnable in result.MadeRunnable)
                        _control.Runnable(runnable);
                    return new CheckpointReply(CheckpointReplyKind.Acknowledged, result.Revision,
                        PendingCancellation: result.PendingCancellation, Parked: result.Parked,
                        UnreadEvents: result.UnreadEvents);
                case WorkflowCommitStatus.EventsChanged:
                    return new CheckpointReply(CheckpointReplyKind.EventsChanged, result.Revision,
                        new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                            $"An inbox event workflow instance '{commit.InstanceId}' settles is no longer pending"));
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
            TraceEntries = [],
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
            // Like the resource stop, this saves the last checkpoint plus a marker, so no limit applies.
            await ExecutionCheckpointWriter.CommitAsync(new HostedRun(this, instance, owner, true), instance,
                checkpoint, null, scopes, Now, preserveSavedWorkflowData: true, enforceLimit: false);
            return Stopped(instance, stop);
        }

        /// <summary>The host side of one run: the instance's lease, its saved state and its children.</summary>
        private sealed class HostedRun(InstanceOperations host, WorkflowInstance instance, WorkflowExecutionLease? owner,
            bool persistState, bool canPark = false) : IExecutionHost
        {
            public bool PersistsState => persistState;
            public bool CanPark => canPark && persistState && owner != null;
            public CancellationToken Drain => host._control.Drain;
            public long Epoch => owner?.Epoch ?? 0;
            public ExecutionLimits Limits => host._limits;
            public Task? OwnershipLoss => owner?.Loss;
            public Task<CancellationReason>? CancellationSignal => owner?.Cancellation;
            public long ObservedInboxSequence => owner?.InboxSequence ?? 0;
            public Task? InboxChanged => owner?.InboxChanged;

            public CancellationReason? ObservedCancellation =>
                owner?.Cancellation.IsCompletedSuccessfully == true ? owner.Cancellation.Result : null;

            public void ThrowIfOwnershipLost() => owner?.ThrowIfLost();

            public Task<CheckpointReply> CommitAsync(CheckpointCommit commit) =>
                host.CommitAsync(instance, commit, Epoch);

            public void ObserveCancellation(CancellationReason reason) => owner?.ObserveCancellation(reason);

            public Task<WorkflowDefinition> ResolveChildAsync(WorkflowStep step, bool starting) =>
                host._children.ResolveAsync(step, starting);

            public ChildStart StartChild(WorkflowDefinition definition, object data, WorkflowInstance parent,
                string childInstanceId, string parentWaitId) =>
                host._children.Start(definition, data, parent, childInstanceId, parentWaitId, host._limits);

            public Task<InboxPage> ReadInboxAsync(long afterSequence, int limit) =>
                host._store.GetPendingEventsAsync(instance.InstanceId, afterSequence, limit);

            public Task<StoredWait?> ReadWaitAsync(string waitId) =>
                host._store.GetWaitAsync(instance.InstanceId, waitId);
        }
    }
}
