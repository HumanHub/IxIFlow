using System.Text.Json;
using System.Collections.Concurrent;
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
        CancellationWatch? watch = null,
        HostSettings? hostSettings = null)
    {
        private readonly IWorkflowStateRepository _store = GuardedStateRepository.Wrap(repository);
        private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
        private readonly ChildWorkflows _children = new(services, registry, timeProvider ?? TimeProvider.System);
        private readonly ExecutionLeaseSettings _leaseSettings = leaseSettings ?? ExecutionLeaseSettings.Default;
        private readonly ExecutionLimits _limits = limits ?? ExecutionLimits.Default;
        private readonly RunControl _control = control ?? new RunControl();
        private readonly CancellationWatch _watch = watch ?? new CancellationWatch(repository, timeProvider);
        private WorkflowExecutor? _executor;
        private WorkflowExecutor Executor => LazyInitializer.EnsureInitialized(ref _executor,
            () => new WorkflowExecutor(services, new ExecutionHost(this)));

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
            reason = OperatorTrace.WithActor(reason, _defaultActor, Now);
            if (!await _store.RequestCancellationAsync(instanceId, reason))
            {
                var existing = await _store.GetWorkflowInstanceSummaryAsync(instanceId);
                if (existing == null)
                    await RefuseRemovedAsync(instanceId);
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
            if (instance != null && WorkflowStoreRules.IsTerminal(instance.Status))
                return ExistingResult(instance);
            return null;
        }

        /// <summary>The saved state of an instance as a result, or null when it does not exist.</summary>
        public async Task<WorkflowExecutionResult?> ReadAsync(string instanceId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
            var instance = await _store.GetWorkflowInstanceSummaryAsync(instanceId);
            return instance == null ? await RemovedResultAsync(instanceId) : ReadResult(instance);
        }

        /// <summary>The status of an instance as a result would report it, without reading its data.</summary>
        public async Task<WorkflowExecutionStatus?> ReadStatusAsync(string instanceId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
            var instance = await _store.GetWorkflowInstanceSummaryAsync(instanceId);
            return instance == null ? (await RemovedResultAsync(instanceId))?.Status : StatusOf(instance.Status);
        }

        private async Task<WorkflowExecutionResult?> RemovedResultAsync(string instanceId) =>
            await _store.GetTombstoneAsync(instanceId) != null
                ? new WorkflowExecutionResult { InstanceId = instanceId, Status = WorkflowExecutionStatus.Removed,
                    ErrorMessage = "The workflow instance and its retained result were removed by retention" }
                : null;

        private async Task RefuseRemovedAsync(string instanceId)
        {
            if (await _store.GetTombstoneAsync(instanceId) != null)
                throw new WorkflowEngineException(WorkflowErrorKind.InstanceRemoved,
                    $"Workflow instance '{instanceId}' was removed; its ID is reserved").At(instanceId);
        }

        public async Task<WorkflowExecutionResult> ResolveAsync(string instanceId,
            string invocationId, ActivityResolution resolution, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);
            ArgumentNullException.ThrowIfNull(resolution);
            using var lease = await gate.EnterAsync(instanceId, cancellationToken);
            var instance = await _store.GetWorkflowInstanceAsync(instanceId);
            if (instance == null)
                await RefuseRemovedAsync(instanceId);
            if (instance?.Status != WorkflowStatus.NeedsResolution || !HasStructuredCheckpoint(instance))
                return Rejected(instanceId, "The workflow instance has no activity awaiting resolution");

            var definition = await registry.GetWorkflowDefinitionAsync(instance.WorkflowName, instance.WorkflowVersion)
                ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                    "The registered workflow definition is unavailable");
            var scopes = WorkflowDefinitionValidator.ForExecution(definition);
            var checkpoint = ExecutionCheckpoint.Read(instance.ExecutionStateJson);
            if (checkpoint.StructureHash != scopes.StructureHash)
                return Rejected(instanceId, scopes.Mismatch(definition, checkpoint.StructureHash));
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

            using var signals = Executor.PrepareAssignment(Guid.NewGuid());
            using var recycle = _control.Register(signals.AssignmentId, Executor.Deliver);
            await using var owner = await AcquireAsync(instance, signals);
            if (owner == null)
                return Busy(instanceId);
            using var assignment = Admit(instance, owner, signals.AssignmentId);
            var current = await _store.GetWorkflowInstanceSummaryAsync(instanceId);
            if (current?.Incarnation != instance.Incarnation || current.Revision != instance.Revision ||
                current.Status != instance.Status)
                return current?.Status == WorkflowStatus.Running
                    ? Busy(instanceId)
                    : Rejected(instanceId, "The activity changed before resolution could be saved");

            checkpoint.Trace.Configure(instance, checkpoint, _limits, _time, scopes);
            OperatorTrace.Resolved(checkpoint, pending, decision, Now);
            pending.Resolution = decision;
            pending.ResolutionReason = null;
            continuation.Status = ContinuationStatus.Active;
            instance.Status = WorkflowStatus.Running;
            var cancellationReason = await _store.GetCancellationRequestAsync(instanceId);
            if (cancellationReason != null && !checkpoint.CancellationRequested &&
                checkpoint.Continuations[0].Status != ContinuationStatus.Completed)
                OperatorTrace.ApplyCancellation(instance, checkpoint, cancellationReason, Now);
            await PersistAsync(instance, checkpoint, workflowData, scopes, assignment);
            return await AssignAsync(instance, owner, signals, cancellationToken);
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
            var scopes = WorkflowDefinitionValidator.ForExecution(definition);
            var checkpoint = ExecutionCheckpoint.Read(instance.ExecutionStateJson);
            if (checkpoint.StructureHash != scopes.StructureHash)
                throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                    scopes.Mismatch(definition, checkpoint.StructureHash));
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

        /// <summary>The instance's whole trace, in the order its commits stored it.</summary>
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
            var scopes = WorkflowDefinitionValidator.ForExecution(definition);
            var checkpoint = ExecutionCheckpoint.Read(instance.ExecutionStateJson);
            if (checkpoint.StructureHash != scopes.StructureHash)
                throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                    scopes.Mismatch(definition, checkpoint.StructureHash));
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
            if (options.InstanceId != null && string.IsNullOrWhiteSpace(options.InstanceId))
                throw new ArgumentException("An explicit workflow instance ID cannot be empty", nameof(options));
            if (options.ExecutionTimeout is { } executionTimeout && executionTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(options),
                    "Execution timeout must be positive");
            var (instance, checkpoint, scopes) = WorkflowInstances.NewInstance(definition, workflowData, options, Now);

            try
            {
                using var lease = await gate.EnterAsync(instance.InstanceId, cancellationToken);
                if (options.InstanceId != null)
                    await RefuseRemovedAsync(instance.InstanceId);
                // A repeated start of an existing instance is answered without claiming it, so retries
                // never count as crashes of an instance another host lost (B-08).
                if (options.InstanceId != null &&
                    await _store.GetWorkflowInstanceSummaryAsync(instance.InstanceId) is { } started)
                    return ExistingStart(started, definition, scopes);
                using var signals = Executor.PrepareAssignment(Guid.NewGuid());
                using var recycle = _control.Register(signals.AssignmentId, Executor.Deliver);
                await using var owner = await AcquireAsync(instance, signals)
                        ?? (options.InstanceId == null
                            ? throw new WorkflowEngineException(WorkflowErrorKind.Infrastructure,
                                "The new workflow instance could not acquire execution ownership")
                            : null);
                if (owner == null)
                    return Busy(instance.InstanceId);
                using var assignment = Admit(instance, owner, signals.AssignmentId);
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
                await registry.RegisterWorkflowAsync(definition);
                await PersistAsync(instance, checkpoint, workflowData, scopes, assignment);
                // The start is durable from here; a caller waiting only for acceptance continues.
                _control.Accept(instance.InstanceId);
                return await AssignAsync(instance, owner, signals, cancellationToken);
            }
            catch (ExecutionOwnershipLostException)
            {
                return Busy(instance.InstanceId);
            }
            catch (WorkflowEngineException error) when (WorkflowEngineException.IsEngineFailure(error))
            {
                error.At(instance.InstanceId);
                throw;
            }
        }

        /// <summary>
        /// Stores an event in the instance's inbox before acknowledging it, then runs the instance so its
        /// owner decides the event at its next pass. The result is the instance's state and
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
        /// Stores an event without running the instance, bypassing its queue and slots. The caller
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

        /// <summary>Open waits that match the query, across instances.</summary>
        public Task<IReadOnlyList<StoredWait>> GetOpenWaitsAsync(WaitQuery query) => _store.GetOpenWaitsAsync(query);

        /// <summary>
        /// Stores a reply addressed to one wait by its token's wait ID, with the token's subject,
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
            if (appended.Status is EventAppendStatus.Closed or EventAppendStatus.NotFound)
                await RefuseRemovedAsync(stored.InstanceId);
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
            if (result.Status == WorkflowExecutionStatus.Removed)
                throw new WorkflowEngineException(WorkflowErrorKind.InstanceRemoved,
                    $"Workflow instance '{instanceId}' was removed; its ID is reserved").At(instanceId);
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
            if (instance == null)
                await RefuseRemovedAsync(instanceId);
            // Even an idle recovery must refuse a different loaded structure before answering
            // from saved positions. Read the schema envelope before interpreting its state.
            var checkedCheckpoint = HasStructuredCheckpoint(instance)
                ? ExecutionCheckpoint.Read(instance.ExecutionStateJson) : null;
            var checkedDefinition = checkedCheckpoint == null ? null
                : await registry.GetWorkflowDefinitionAsync(instance!.WorkflowName, instance.WorkflowVersion);
            var checkedScopes = checkedDefinition == null ? null
                : WorkflowDefinitionValidator.ForExecution(checkedDefinition);
            if (checkedScopes != null && checkedCheckpoint!.StructureHash != checkedScopes.StructureHash)
                return Rejected(instanceId, checkedScopes.Mismatch(checkedDefinition!, checkedCheckpoint.StructureHash));
            // Recovery repeats a finished instance's saved result, as starting it again does.
            if (!release && !forEvent && instance != null && WorkflowStoreRules.IsTerminal(instance.Status))
                return ReadResult(instance);
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
            var checkpoint = checkedCheckpoint!;
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

            var definition = checkedDefinition;
            if (definition == null)
                return Rejected(instanceId, "The registered workflow definition is unavailable");
            var scopes = checkedScopes!;
            // Ownership comes before the workflow data is read, so an instance whose data or code kills
            // its host is still counted by the next claim, and quarantined (B-08).
            using var signals = Executor.PrepareAssignment(Guid.NewGuid());
            using var recycle = _control.Register(signals.AssignmentId, Executor.Deliver);
            await using var owner = await AcquireAsync(instance, signals);
            if (owner == null)
                return Busy(instanceId);
            using var assignment = Admit(instance, owner, signals.AssignmentId);
            var current = await _store.GetWorkflowInstanceSummaryAsync(instanceId);
            if (current?.Incarnation != instance.Incarnation || current.Revision != instance.Revision ||
                current.Status != instance.Status)
                return current?.Status == WorkflowStatus.Running
                    ? Busy(instanceId)
                    : Rejected(instanceId, "The workflow instance changed before recovery could start");
            // How long the instance waited to be claimed, on the store's clock as this run last read it (D-39).
            if (instance.RunnableAtUtc is { } runnableAt && checkpoint.Clock.ToStore(Now) - runnableAt is var waited &&
                waited >= TimeSpan.Zero)
                Telemetry.BacklogAge.Record(waited.TotalSeconds, Telemetry.Workflow(instance));
            // Only a claim of a running instance can see crashes: every commit that leaves Running
            // records progress and clears the count, and other entry points refuse running instances.
            if (owner.CrashCount >= (hostSettings ?? HostSettings.Default).MaxCrashes)
                return await QuarantineAsync(instance, checkpoint, scopes, assignment);
            var stopCleared = checkpoint.OperatorStop != null;
            var reactivated = checkpoint.Continuations.Where(item =>
                item.PendingActivity is { ChildWorkflowId: null } &&
                (item.Status == ContinuationStatus.Cancelling ||
                 item.Status == ContinuationStatus.WaitingResolution && (!forEvent || stopCleared)))
                .Select(item => item.Id).ToArray();
            return await AssignAsync(instance, owner, signals, cancellationToken,
                new RecoveryPreparation(reactivated, newCancellation ? cancellationReason : null,
                    stopCleared, release, actor));
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
            if (existing.WorkflowName != definition.Name || existing.WorkflowVersion != definition.Version)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
                    $"Workflow instance '{existing.InstanceId}' belongs to a different definition");
            if (checkpoint.StructureHash != scopes.StructureHash)
                throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                    scopes.Mismatch(definition, checkpoint.StructureHash));
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

        private Task<WorkflowExecutionLease?> AcquireAsync(WorkflowInstance instance, ExecutionSignals signals) =>
            WorkflowExecutionLease.TryAcquireAsync(_store, instance, _leaseSettings, _watch,
                _ => Executor.Deliver(new Cancel(signals.AssignmentId, CancelKind.Ownership)),
                reason => Executor.Deliver(new Cancel(signals.AssignmentId, CancelKind.Workflow, reason)),
                sequence => Executor.Deliver(new Arrivals(signals.AssignmentId, sequence)), _time);

        private sealed class RunReceipt
        {
            public readonly HashSet<string> Commits = [];
            public readonly List<ExecutionTraceEntry> Trace = [];
            public DateTime? WakeUtc;
            public WorkflowInstance? Instance;
        }

        private readonly ConcurrentDictionary<string, RunReceipt> _receipts = new(StringComparer.Ordinal);

        private async Task<WorkflowExecutionResult> AssignAsync(WorkflowInstance instance,
            WorkflowExecutionLease owner, ExecutionSignals signals, CancellationToken cancellationToken,
            RecoveryPreparation? preparation = null)
        {
            var depth = Interlocked.Increment(ref _runDepth);
            using var cancellation = cancellationToken.Register(() =>
                Executor.Deliver(new Cancel(signals.AssignmentId, CancelKind.Run)));
            try
            {
                var checkpoint = ExecutionCheckpoint.Read(StoredCheckpoint.WholeState(instance));
                var assigned = JsonSerializer.Deserialize<WorkflowInstance>(JsonSerializer.Serialize(instance))!;
                assigned.Incarnation = instance.Incarnation;
                assigned.ExecutionStateJson = StoredCheckpoint.WholeState(instance);
                var assign = new Assign(signals.AssignmentId, assigned, instance.PartsAt(instance.Revision),
                    checkpoint.StructureHash, owner.Epoch, signals.InboxSequence, _limits, depth == 1, [], preparation);
                var ready = await Executor.ExecuteAsync(assign, CancellationToken.None);
                if (ready.AssignmentId != signals.AssignmentId)
                    throw new ExecutionOwnershipLostException(new InvalidOperationException("Ready is not assigned"));
                var readyAdmission = AdmitReady(ready);
                var outcome = ready.Outcome ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "Missing assignment outcome");
                if (readyAdmission == ConflictReason.NotAssigned)
                    outcome = new(AssignmentOutcomeKind.OwnershipLost);
                if (outcome.Kind == AssignmentOutcomeKind.Interrupted)
                {
                    // Unknown may have applied. Neither the executor's last Ack nor the receipt projection
                    // is authoritative; recovery always starts with a new store read.
                    _ = await _store.GetWorkflowInstanceAsync(instance.InstanceId);
                    var error = _assignments[signals.AssignmentId].LastCommitError ?? new WorkflowEngineException(
                        WorkflowErrorKind.Infrastructure, outcome.ErrorMessage ?? "Execution was interrupted");
                    if (error is WorkflowEngineException engineError)
                        engineError.At(instance.InstanceId, outcome.StepId, outcome.AttemptId);
                    throw error;
                }
                if (outcome.Kind == AssignmentOutcomeKind.RunCancelled)
                    throw new OperationCanceledException(cancellationToken);
                _receipts.TryGetValue(instance.InstanceId, out var receipt);
                // Acknowledged commits reconstruct the stored instance without retaining executor objects.
                // After ownership loss another host may have changed it, so only a fresh read is authoritative.
                var saved = outcome.Kind is AssignmentOutcomeKind.OwnershipLost or AssignmentOutcomeKind.Interrupted
                    ? await _store.GetWorkflowInstanceAsync(instance.InstanceId)
                    : receipt?.Instance;
                if (saved == null)
                    throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                        "The running workflow instance is missing from the store");
                if (outcome.Kind is not (AssignmentOutcomeKind.OwnershipLost or AssignmentOutcomeKind.Interrupted) &&
                    receipt?.WakeUtc is { } due &&
                    (outcome.Kind == AssignmentOutcomeKind.Parked ||
                     saved.Status is WorkflowStatus.Suspended or WorkflowStatus.NeedsResolution))
                    _control.Schedule(saved.InstanceId, due);
                else if (WorkflowStoreRules.IsTerminal(saved.Status))
                    _control.Unschedule(saved.InstanceId);
                var result = ExistingResult(saved);
                result.Status = outcome.Kind switch
                {
                    AssignmentOutcomeKind.Parked => WorkflowExecutionStatus.Scheduled,
                    AssignmentOutcomeKind.Drained or AssignmentOutcomeKind.OwnershipLost or AssignmentOutcomeKind.Interrupted => WorkflowExecutionStatus.Running,
                    _ => StatusOf(saved.Status)
                };
                result.TraceEntries = receipt?.Trace.ToList() ?? [];
                result.ErrorMessage = outcome.ErrorMessage ?? result.ErrorMessage;
                result.ErrorType = outcome.ErrorType;
                result.ErrorStackTrace = outcome.ErrorStackTrace ?? result.ErrorStackTrace;
                result.ExecutionTime = Now - saved.StartedAt!.Value;
                return result;
            }
            finally
            {
                _receipts.TryRemove(instance.InstanceId, out _);
                Interlocked.Decrement(ref _runDepth);
            }
        }

        internal static WorkflowExecutionResult Busy(string instanceId) => new()
        {
            InstanceId = instanceId,
            Status = WorkflowExecutionStatus.Running,
            ErrorMessage = "The workflow instance is executing on another host"
        };

        // A request whose checkpoint would exceed a limit is refused; the instance keeps its saved state.
        private async Task PersistAsync(WorkflowInstance instance, ExecutionCheckpoint checkpoint,
            object workflowData, WorkflowScopeCatalog scopes, HostedRun assignment)
        {
            try
            {
                await ExecutionCheckpointWriter.CommitAsync(assignment.Session, instance,
                    checkpoint, workflowData, scopes, Now);
            }
            catch (CheckpointInterruptedException error) when (error.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(assignment.LastCommitError ?? error.InnerException).Throw();
                throw;
            }
            catch (CheckpointLimitExceededException error)
            {
                throw new WorkflowEngineException(WorkflowErrorKind.ResourceExhausted, error.Message, error);
            }
        }

        private async Task<CheckpointReply> CommitAsync(WorkflowInstance instance, CheckpointCommit commit,
            long epoch, Guid assignmentId, WorkflowInstance basedOn)
        {
            if (epoch == 0)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                    $"A run of workflow instance '{commit.InstanceId}' without an execution lease cannot commit");
            if (commit.InstanceId != instance.InstanceId)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                    $"A commit for workflow instance '{commit.InstanceId}' reached the run of '{instance.InstanceId}'");
            if (commit.Epoch != epoch || commit.Incarnation != instance.Incarnation)
                return new CheckpointReply(assignmentId, CheckpointReplyKind.Conflict, Error:
                    $"A commit from incarnation {commit.Incarnation} at execution epoch {commit.Epoch} cannot change " +
                    $"workflow instance '{commit.InstanceId}' in incarnation {instance.Incarnation} owned at epoch {epoch}");
            WorkflowCommitResult result;
            try
            {
                result = await _store.CommitWorkflowInstanceAsync(commit);
            }
            catch (WorkflowEngineException error) when (error.Kind == WorkflowErrorKind.Infrastructure)
            {
                if (_assignments.TryGetValue(assignmentId, out var assigned)) assigned.LastCommitError = error;
                return new CheckpointReply(assignmentId, CheckpointReplyKind.Unknown, Error: error.Message);
            }

            switch (result.Status)
            {
                case WorkflowCommitStatus.Applied or WorkflowCommitStatus.AlreadyApplied:
                    var receipt = _receipts.GetOrAdd(instance.InstanceId, _ => new RunReceipt());
                    if (receipt.Commits.Add(commit.CommitId))
                    {
                        receipt.Instance = CheckpointParts.Summarize(commit.Payload,
                            commit.ApplyParts(basedOn.SavedParts).GetValueOrDefault(CheckpointParts.DataKey), result.Revision);
                        receipt.Instance.RunnableAtUtc = result.RunnableAtUtc;
                        receipt.Trace.AddRange(commit.TraceEntries.Select(json =>
                            JsonSerializer.Deserialize<ExecutionTraceEntry>(json)!));
                    }
                    receipt.WakeUtc = result.RunnableAtUtc is { } due
                        ? Now + (due > result.StoreTimeUtc ? due - result.StoreTimeUtc : TimeSpan.Zero)
                        : null;
                    // A child the commit started, or a parent its completion woke, runs here without waiting
                    // for the poll (D-17).
                    foreach (var runnable in result.MadeRunnable)
                        _control.Runnable(runnable);
                    return new CheckpointReply(assignmentId, CheckpointReplyKind.Ack, result.Revision,
                        PendingCancellation: result.PendingCancellation, Parked: result.Parked,
                        UnreadEvents: result.UnreadEvents);
                case WorkflowCommitStatus.EventsChanged:
                    return new CheckpointReply(assignmentId, CheckpointReplyKind.Conflict, result.Revision,
                        $"An inbox event workflow instance '{commit.InstanceId}' settles is no longer pending", Conflict: ConflictReason.EventsChanged);
                case WorkflowCommitStatus.Conflict:
                    return new CheckpointReply(assignmentId, CheckpointReplyKind.Conflict, result.Revision,
                        $"Workflow instance '{commit.InstanceId}' changed at revision {result.Revision}", Conflict: ConflictReason.Revision);
                case WorkflowCommitStatus.StaleEpoch:
                    return new CheckpointReply(assignmentId, CheckpointReplyKind.Conflict, result.Revision,
                        $"Workflow instance '{commit.InstanceId}' is no longer owned by incarnation {commit.Incarnation} " +
                        $"at execution epoch {commit.Epoch}", Conflict: ConflictReason.StaleEpoch);
                default:
                    var error = new WorkflowEngineException(
                        WorkflowErrorKind.Infrastructure,
                        $"Saving workflow instance '{commit.InstanceId}' was not applied; the store asked for a retry");
                    if (_assignments.TryGetValue(assignmentId, out var assigned)) assigned.LastCommitError = error;
                    return new CheckpointReply(assignmentId, CheckpointReplyKind.Retry, Error: error.Message);
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
            ExecutionCheckpoint checkpoint, WorkflowScopeCatalog scopes, HostedRun assignment)
        {
            var owner = assignment.Owner;
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
            await ExecutionCheckpointWriter.CommitAsync(assignment.Session, instance,
                checkpoint, null, scopes, Now, preserveSavedWorkflowData: true, enforceLimit: false);
            return Stopped(instance, stop);
        }

        private void CheckLimit(CheckpointCommit commit, WorkflowInstance saved)
        {
            var trunk = commit.ReadTrunk().ExecutionStateJson;
            var parts = commit.ApplyParts(saved.PartsAt(saved.Revision));
            var size = Encoding.UTF8.GetByteCount(trunk) + parts.Values.Sum(value => (long)Encoding.UTF8.GetByteCount(value));
            if (size <= _limits.MaxCheckpointBytes) return;
            // A stop marker may exceed the limit only when it keeps the entire saved checkpoint.
            // A terminal serialization failure may discard continuations while keeping saved data.
            var proposed = ExecutionCheckpoint.Read(StoredCheckpoint.AssembleState(trunk, parts));
            var previous = ExecutionCheckpoint.Read(StoredCheckpoint.WholeState(saved));
            var sameData = parts.GetValueOrDefault(CheckpointParts.DataKey) == saved.WorkflowDataJson;
            if (sameData && (commit.Stopped || previous.OperatorStop != null))
            {
                proposed.OperatorStop = null;
                previous.OperatorStop = null;
                if (JsonSerializer.Serialize(proposed) == JsonSerializer.Serialize(previous)) return;
            }
            if (sameData && commit.Instance.Status == WorkflowStatus.Failed && proposed.Continuations.Count == 0 &&
                proposed.Waits.Count == 0 && proposed.Joins.Count == 0) return;
            throw new CheckpointLimitExceededException(size, _limits.MaxCheckpointBytes);
        }

        private async Task<StartChildReply> ValidateChildAsync(StartChild request, WorkflowInstance child)
        {
            var definition = await registry.GetWorkflowDefinitionAsync(child.WorkflowName, child.WorkflowVersion);
            if (definition == null)
                return new(request.AssignmentId, Refusal: WorkflowErrorKind.ArtifactUnavailable,
                    Message: $"Workflow definition not found: {child.WorkflowName} v{child.WorkflowVersion}");
            if (definition.Metadata.TryGetValue("IsActive", out var active) && active is false)
                return new(request.AssignmentId, Refusal: WorkflowErrorKind.ArtifactUnavailable,
                    Message: $"Workflow {definition.Name} v{definition.Version} is deactivated");
            if (definition.Name != child.WorkflowName || definition.Version != child.WorkflowVersion)
                return new(request.AssignmentId, Refusal: WorkflowErrorKind.ArtifactUnavailable,
                    Message: "The child definition has a different declared identity");
            _ = WorkflowDefinitionValidator.ForExecution(definition);
            var hash = await registry.GetWorkflowStructureHashAsync(child.WorkflowName, child.WorkflowVersion);
            var checkpoint = ExecutionCheckpoint.Read(child.ExecutionStateJson);
            if (hash != request.StructureHash || checkpoint.StructureHash != hash ||
                child.WorkflowDataType != definition.WorkflowDataType!.AssemblyQualifiedName)
                return new(request.AssignmentId, Refusal: WorkflowErrorKind.ArtifactUnavailable,
                    Message: "The child workflow structure hash or data type differs from its registered definition");
            var size = StoredCheckpoint.Of(child).Bytes;
            if (size > _limits.MaxCheckpointBytes)
                return new(request.AssignmentId, Refusal: WorkflowErrorKind.ResourceExhausted,
                    Message: new CheckpointLimitExceededException(size, _limits.MaxCheckpointBytes).Message);
            return new(request.AssignmentId, request.Start);
        }

        private readonly ConcurrentDictionary<Guid, HostedRun> _assignments = new();
        private sealed class CommitQueue
        {
            public readonly object Sync = new();
            public Task Tail = Task.CompletedTask;
        }
        private readonly ConcurrentDictionary<string, CommitQueue> _commitQueues = new(StringComparer.Ordinal);

        internal ConflictReason? AdmitReady(Ready ready)
        {
            ExecutionContract.RequireVersion(ready.ContractVersion);
            return _assignments.TryGetValue(ready.AssignmentId, out var assignment) && assignment.IsActive(assignment.InstanceId)
                ? null : ConflictReason.NotAssigned;
        }

        internal HostedRun Admit(WorkflowInstance instance, WorkflowExecutionLease owner, Guid identity)
        {
            var run = new HostedRun(this, instance, owner, identity);
            if (!_assignments.TryAdd(identity, run)) throw new InvalidOperationException("Assignment already admitted");
            return run;
        }

        /// <summary>Routes requests only to the currently active assignment. No rejection touches the store.</summary>
        internal sealed class ExecutionHost(InstanceOperations operations) : IExecutionHost
        {
            private HostedRun? Find(Guid id, string instanceId) =>
                operations._assignments.TryGetValue(id, out var run) && run.IsActive(instanceId) ? run : null;

            public Task<CheckpointReply> CheckpointAsync(Checkpoint request) =>
                Find(request.AssignmentId, request.Commit.InstanceId) is { } run
                    ? run.CheckpointAsync(request)
                    : Task.FromResult(new CheckpointReply(request.AssignmentId, CheckpointReplyKind.Conflict,
                        Conflict: ConflictReason.NotAssigned));
            public Task<StartChildReply> StartChildAsync(StartChild request) =>
                Find(request.AssignmentId, request.InstanceId) is { } run
                    ? run.StartChildAsync(request)
                    : Task.FromResult(new StartChildReply(request.AssignmentId, Conflict: ConflictReason.NotAssigned));
            public async Task<ReadInboxReply> ReadInboxAsync(ReadInbox request) =>
                Find(request.AssignmentId, request.InstanceId) is { }
                    ? new(request.AssignmentId, await operations._store.GetPendingEventsAsync(
                        request.InstanceId, request.AfterSequence, request.Limit))
                    : new(request.AssignmentId, Conflict: ConflictReason.NotAssigned);
            public async Task<ReadWaitReply> ReadWaitAsync(ReadWait request) =>
                Find(request.AssignmentId, request.InstanceId) is { }
                    ? new(request.AssignmentId, await operations._store.GetWaitAsync(request.InstanceId, request.WaitId))
                    : new(request.AssignmentId, Conflict: ConflictReason.NotAssigned);
        }

        internal sealed class HostedRun(InstanceOperations operations, WorkflowInstance instance,
            WorkflowExecutionLease owner, Guid identity) : IDisposable
        {
            private readonly CommitQueue _queue = operations._commitQueues.GetOrAdd(instance.InstanceId, _ => new());
            private readonly Dictionary<string, (long Revision, string Hash, Task<CheckpointReply> Task)> _outstanding = [];
            private WorkflowInstance _saved = instance;
            public Exception? LastCommitError { get; set; }
            public WorkflowExecutionLease Owner => owner;
            public string InstanceId => instance.InstanceId;
            public ExecutionSession Session { get; } = new(new ExecutionHost(operations), identity,
                instance.InstanceId, owner.Epoch, operations._limits, false);

            public void Dispose() => operations._assignments.TryRemove(identity, out _);

            public bool IsActive(string instanceId)
            {
                if (instanceId != instance.InstanceId || !operations._assignments.ContainsKey(identity)) return false;
                try { owner.ThrowIfLost(); return true; }
                catch (ExecutionOwnershipLostException) { return false; }
            }

            public Task<CheckpointReply> CheckpointAsync(Checkpoint request)
            {
                var commit = request.Commit;
                if (request.AssignmentId != identity || !IsActive(commit.InstanceId) ||
                    commit.Epoch != owner.Epoch || commit.Incarnation != instance.Incarnation)
                    return Task.FromResult(new CheckpointReply(request.AssignmentId, CheckpointReplyKind.Conflict,
                        Conflict: ConflictReason.NotAssigned));
                lock (_queue.Sync)
                {
                    var hash = Convert.ToHexString(commit.ContentHash);
                    if (_outstanding.TryGetValue(commit.CommitId, out var pending))
                        return pending.Revision == commit.ExpectedRevision && pending.Hash == hash
                            ? pending.Task
                            : Task.FromResult(new CheckpointReply(identity, CheckpointReplyKind.Conflict,
                                Conflict: ConflictReason.Revision));
                    var completion = new TaskCompletionSource<CheckpointReply>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var preceding = _queue.Tail;
                    _outstanding.Add(commit.CommitId, (commit.ExpectedRevision, hash, completion.Task));
                    _queue.Tail = completion.Task;
                    _ = ApplyAsync(preceding, request, completion);
                    return completion.Task;
                }
            }

            private async Task ApplyAsync(Task preceding, Checkpoint request, TaskCompletionSource<CheckpointReply> completion)
            {
                // Yield before entering the store so admission, dedup registration and ordering are established first.
                await Task.Yield();
                try
                {
                    try { await preceding; } catch { /* A failed request does not break the queue. */ }
                    var commit = request.Commit;
                    CheckpointReply reply;
                    if (!IsActive(commit.InstanceId) || request.AssignmentId != identity ||
                        commit.Epoch != owner.Epoch || commit.Incarnation != instance.Incarnation)
                        reply = new(identity, CheckpointReplyKind.Conflict, Conflict: ConflictReason.NotAssigned);
                    else
                    {
                        operations.CheckLimit(commit, _saved);
                        reply = await operations.CommitAsync(instance, commit, owner.Epoch, identity, _saved);
                        if (reply.Kind == CheckpointReplyKind.Ack && reply.Revision >= _saved.Revision)
                        {
                            _saved = commit.ReadInstance(_saved);
                            _saved.Incarnation = commit.Incarnation;
                        }
                        if (reply.PendingCancellation is { } reason) owner.ObserveCancellation(reason);
                    }
                    lock (_queue.Sync) _outstanding.Remove(commit.CommitId);
                    completion.TrySetResult(reply);
                }
                catch (Exception error)
                {
                    lock (_queue.Sync) _outstanding.Remove(request.Commit.CommitId);
                    completion.TrySetException(error);
                }
            }

            public async Task<StartChildReply> StartChildAsync(StartChild request)
            {
                if (request.AssignmentId != identity || request.Epoch != owner.Epoch || !IsActive(request.InstanceId))
                    return new(identity, Conflict: ConflictReason.NotAssigned);
                var child = request.Start.Child;
                if (child == null || child.ParentInstanceId != instance.InstanceId ||
                    child.InstanceId != request.Start.ChildInstanceId || child.ParentWaitId != request.Start.ParentWaitId)
                    return new(identity, Refusal: WorkflowErrorKind.InvalidRequest, Message: "Invalid child start");
                // Identity, activation and the registered hash are host decisions. No executor definition crosses here.
                return await operations.ValidateChildAsync(request, child);
            }
        }
    }
}
