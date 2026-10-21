using JsonSerializer = IxIFlow.Core.Runtime.WorkflowJson;
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
    internal sealed partial class InstanceOperations(
        IServiceProvider services,
        IActivityExecutor activityExecutor,
        IWorkflowStateRepository repository,
        IWorkflowCatalog registry,
        InProcessInstanceGate gate,
        ExecutionLeaseRenewer renewer,
        ExecutionLeaseSettings? leaseSettings = null,
        TimeProvider? timeProvider = null,
        ExecutionLimits? limits = null,
        RunControl? control = null,
        CancellationWatch? watch = null,
        HostSettings? hostSettings = null)
    {
        private readonly IWorkflowStateRepository _store = GuardedStateRepository.Wrap(repository);
        private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
        private readonly ExecutionLeaseSettings _leaseSettings = leaseSettings ?? ExecutionLeaseSettings.Default;
        private readonly ExecutionLimits _limits = limits ?? ExecutionLimits.Default;
        private IPayloadStore PayloadStore => repository.PayloadStore;
        private PayloadReader NewPayloadReader() => new(ReadPayloadBodyAsync, new(_limits.MaxHydratedPayloadBytes));
        private async Task<ExecutionCheckpoint> ReadCheckpointAsync(WorkflowInstance instance, PayloadReader? reader = null) =>
            ExecutionCheckpoint.Read(await StoredCheckpoint.WholeStateAsync(instance, reader ?? NewPayloadReader()));
        private async Task<ReadOnlyMemory<byte>> ReadPayloadBodyAsync(PayloadReference reference)
        {
            if (reference.Bytes > _limits.MaxPayloadBytes || reference.Bytes > int.MaxValue)
                throw new WorkflowEngineException(WorkflowErrorKind.ResourceExhausted, "MaxPayloadBytes exceeded");
            var transfer = new ExecutionPayloadTransfer(PayloadStore, _limits);
            var reply = await transfer.ReadAsync(new(Guid.Empty, "result", reference));
            if (reply.Refusal is { } kind) throw new WorkflowEngineException(kind, $"Payload {reference.Digest} is unavailable");
            return reply.Body!;
        }
        private readonly RunControl _control = control ?? new RunControl();
        private readonly CancellationWatch _watch = watch ?? new CancellationWatch(repository, timeProvider);
        private WorkflowExecutor? _executor;
        private WorkflowExecutor Executor => LazyInitializer.EnsureInitialized(ref _executor,
            () => new WorkflowExecutor(services, new ExecutionHost(this)));
        private readonly RunnableNotifications? _notifications = services.GetService(typeof(RunnableNotifications)) as RunnableNotifications;

        private void Released(ExecutionLeaseReleaseResult result) => _notifications?.Publish(result.Notice);

        private readonly string? _defaultActor = (services.GetService(typeof(ClientActor)) as ClientActor)?.Name;

        private readonly HostSettings _hostSettings = (services.GetService(typeof(HostSettings)) as HostSettings) ?? HostSettings.Default;

        private async Task<ClaimQuery> ClaimQueryAsync() => new(_hostSettings.Id,
            await _hostSettings.GetCapabilitiesAsync(registry));

        private async Task<bool> MeetsDemandsAsync(string instanceId, IReadOnlyList<WorkflowDemand> demands, ClaimQuery query)
        {
            // A host clock must neither expire a pin early nor keep an expired pin alive.
            var now = demands.Any(demand => demand.ExpiresAtUtc != null || demand.ValueExpiresAtUtc != null)
                ? (await _store.GetPendingEventsAsync(instanceId, 0, 1)).StoreTimeUtc
                    ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "The store did not return its clock")
                : DateTime.MinValue;
            var unmet = demands.Where(demand => !DemandMatching.Meets([demand], query, now)).ToArray();
            if (unmet.Length == 0) return true;
            if (unmet.Any(demand => !demand.Key.StartsWith("workflow:", StringComparison.Ordinal))) return false;
            // Another catalog instance may have published since this host's last heartbeat.
            if (await _hostSettings.RefreshDemandCapabilitiesAsync(registry, _time) is not { } capabilities) return false;
            var refreshed = new ClaimQuery(_hostSettings.Id, capabilities);
            return DemandMatching.Meets(demands, refreshed, now);
        }

        private static WorkflowExecutionResult Scheduled(string instanceId) => new()
        { InstanceId = instanceId, Status = WorkflowExecutionStatus.Scheduled };

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
            CancellationReason reason, CancellationToken cancellationToken, Guid? expectedIncarnation = null)
        {
            InstanceIds.Validate(instanceId);
            ArgumentNullException.ThrowIfNull(reason);
            cancellationToken.ThrowIfCancellationRequested();
            reason = OperatorTrace.WithActor(reason, _defaultActor, Now);
            var cancellation = await _store.RequestCancellationAsync(instanceId, reason, expectedIncarnation);
            _notifications?.Publish(cancellation.Notice);
            if (!cancellation.Accepted)
            {
                var existing = await _store.GetWorkflowInstanceSummaryAsync(instanceId);
                RequireIncarnation(existing, expectedIncarnation);
                if (existing == null)
                    await RefuseRemovedAsync(instanceId);
                if (existing == null)
                    throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
                        $"Workflow instance '{instanceId}' does not exist");
                if (existing.Status is WorkflowStatus.Running or WorkflowStatus.Suspended or
                    WorkflowStatus.NeedsResolution or WorkflowStatus.Paused)
                    throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                        "The workflow instance changed while cancellation was requested; retry");
                return await ExistingResultAsync(existing, cancellationToken);
            }

            var instance = await _store.GetWorkflowInstanceSummaryAsync(instanceId);
            if (instance?.Status == WorkflowStatus.Running)
                return new WorkflowExecutionResult
                {
                    InstanceId = instanceId,
                    Status = WorkflowExecutionStatus.Running,
                    ErrorKind = WorkflowErrorKind.InvalidState,
                    ErrorMessage = "Cancellation was requested; the executing host will stop the workflow"
                };
            if (instance != null && WorkflowStoreRules.IsTerminal(instance.Status))
                return await ExistingResultAsync(instance, cancellationToken);
            return null;
        }

        /// <summary>The saved state of an instance as a result, or null when it does not exist.</summary>
        public async Task<WorkflowExecutionResult?> ReadAsync(string instanceId, CancellationToken cancellationToken = default)
        {
            InstanceIds.Validate(instanceId);
            var instance = await _store.GetWorkflowInstanceSummaryAsync(instanceId);
            return instance == null ? await RemovedResultAsync(instanceId) : await ReadResultAsync(instance, cancellationToken);
        }

        /// <summary>The status of an instance as a result would report it, without reading its data.</summary>
        public async Task<WorkflowExecutionStatus?> ReadStatusAsync(string instanceId)
        {
            InstanceIds.Validate(instanceId);
            var instance = await _store.GetWorkflowInstanceSummaryAsync(instanceId);
            return instance == null ? (await RemovedResultAsync(instanceId))?.Status : StatusOf(instance.Status);
        }

        private async Task<WorkflowExecutionResult?> RemovedResultAsync(string instanceId) =>
            await _store.GetTombstoneAsync(instanceId) != null
                ? new WorkflowExecutionResult { InstanceId = instanceId, Status = WorkflowExecutionStatus.Removed, ErrorKind = WorkflowErrorKind.InstanceRemoved,
                    ErrorMessage = "The workflow instance and its retained result were removed by retention" }
                : null;

        private async Task RefuseRemovedAsync(string instanceId)
        {
            if (await _store.GetTombstoneAsync(instanceId) != null)
                throw new WorkflowEngineException(WorkflowErrorKind.InstanceRemoved,
                    $"Workflow instance '{instanceId}' was removed; its ID is reserved").At(instanceId);
        }

        public async Task<WorkflowExecutionResult> RequestDebugAsync(string instanceId, DebugCommandKind kind,
            string actor, CancellationToken cancellationToken, string? continuationId = null, DebugStepKind? stepKind = null,
            Guid? expectedIncarnation = null)
        {
            InstanceIds.Validate(instanceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(actor);
            cancellationToken.ThrowIfCancellationRequested();
            if (!(hostSettings ?? HostSettings.Default).Debugging)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Debugging is disabled on this host");
            var instance = await _store.GetWorkflowInstanceAsync(instanceId);
            RequireIncarnation(instance, expectedIncarnation);
            if (instance == null) throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Workflow instance does not exist");
            if (kind == DebugCommandKind.Step)
            {
                var selected = (await ReadCheckpointAsync(instance)).Continuations
                    .SingleOrDefault(c => c.Id == continuationId);
                if (selected == null || selected.Status is ContinuationStatus.Completed or ContinuationStatus.Cancelled)
                    throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "The selected continuation is not live");
            }
            var state = (await _store.GetDebugStatesAsync([instanceId]))[instanceId];
            var generation = state.PauseGeneration + (kind is DebugCommandKind.Continue or DebugCommandKind.Step && instance.Status != WorkflowStatus.Paused &&
                (state.Commands.Any(c => c.AcknowledgedRevision == null && c.Kind == DebugCommandKind.Pause) ||
                 WorkflowDebugRules.Checkpoint(instance).Stop != null) ? 1 : 0);
            var requested = await _store.RequestDebugCommandAsync(instanceId,
                new(Guid.NewGuid().ToString("N"), kind, generation, actor, ContinuationId: continuationId, StepKind: stepKind)
                    { ExpectedIncarnation = instance.Incarnation });
            if (requested.Status is not (DebugCommandStatus.Accepted or DebugCommandStatus.AlreadyAccepted))
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, $"Debug command refused: {requested.Status}");
            if (kind != DebugCommandKind.Pause || instance.Status == WorkflowStatus.Running) return await ExistingResultAsync(instance, cancellationToken);
            using var entered = await gate.EnterAsync(instanceId, cancellationToken);
            using var signals = Executor.PrepareAssignment(Guid.NewGuid());
            await using var owner = await AcquireAsync(instance, signals);
            if (owner == null) return Busy(instanceId);
            using var assignment = Admit(instance, owner, signals.AssignmentId);
            var current = await _store.GetWorkflowInstanceAsync(instanceId);
            if (current == null || current.Incarnation != instance.Incarnation || current.Revision != instance.Revision ||
                current.Status != instance.Status) return current == null ? Busy(instanceId) : await ExistingResultAsync(current, cancellationToken);
            var checkpoint = await ReadCheckpointAsync(current);
            var latest = (await _store.GetDebugStatesAsync([instanceId]))[instanceId];
            var first = latest.Commands.FirstOrDefault(c => c.AcknowledgedRevision == null);
            if (first?.Id != requested.Command!.Id || first.PauseGeneration != checkpoint.Debug.PauseGeneration ||
                await _store.GetCancellationRequestAsync(instanceId) != null) return await ExistingResultAsync(current, cancellationToken);
            using var definitionLease = await registry.AcquireWorkflowDefinitionAsync(current.WorkflowName,
                current.WorkflowVersion, current.BundleDigest, cancellationToken);
            using var types = definitionLease.EnterTypes();
            var definition = definitionLease.Definition;
            var scopes = WorkflowDefinitionValidator.ForExecution(definition);
            if (scopes.StructureHash != checkpoint.StructureHash) return Rejected(instanceId, "Workflow structure differs");
            checkpoint.Signals = signals;
            checkpoint.Trace.Configure(current, checkpoint, _limits, _time, scopes);
            checkpoint.Debug = checkpoint.Debug with { PauseGeneration = checkpoint.Debug.PauseGeneration + 1, AppliedOrder = first.Order };
            current.Status = WorkflowStatus.Paused;
            checkpoint.Trace.Add(new ExecutionTraceEntry { Timestamp = Now, EntryType = TraceEntryType.Paused,
                Payload = new DebugCommandTrace(actor, first.Id, checkpoint.Debug.PauseGeneration) });
            await ExecutionCheckpointWriter.CommitAsync(assignment.Session, current, checkpoint, null, scopes, Now,
                preserveSavedWorkflowData: true, preserveContinuationState: true);
            return await ExistingResultAsync(current, cancellationToken);
        }

        public async Task<WorkflowExecutionResult> ResolveAsync(string instanceId,
            string invocationId, ActivityResolution resolution, CancellationToken cancellationToken, Guid? expectedIncarnation = null)
        {
            InstanceIds.Validate(instanceId);
            ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);
            ArgumentNullException.ThrowIfNull(resolution);
            if (await FinishTerminationAsync(instanceId, expectedIncarnation))
                return TerminatedResult(instanceId);
            using var lease = await gate.EnterAsync(instanceId, cancellationToken);
            var instance = await _store.GetWorkflowInstanceAsync(instanceId);
            RequireIncarnation(instance, expectedIncarnation);
            if (instance == null)
                await RefuseRemovedAsync(instanceId);
            if (instance?.Status != WorkflowStatus.NeedsResolution || !HasStructuredCheckpoint(instance))
                return Rejected(instanceId, "The workflow instance has no activity awaiting resolution");
            var claim = await ClaimQueryAsync();
            if (!await MeetsDemandsAsync(instanceId, WorkflowDemands.Saved(instance), claim)) return Scheduled(instanceId);

            using var definitionLease = await registry.AcquireWorkflowDefinitionAsync(instance.WorkflowName, instance.WorkflowVersion, instance.BundleDigest, cancellationToken);
            using var types = definitionLease.EnterTypes();
            var definition = definitionLease.Definition;
            var scopes = WorkflowDefinitionValidator.ForExecution(definition);
            var checkpoint = await ReadCheckpointAsync(instance);
            if (checkpoint.StructureHash != scopes.StructureHash)
                return Rejected(instanceId, scopes.Mismatch(definition, checkpoint.StructureHash));
            if (checkpoint.OperatorStop is { } stop)
                return new WorkflowExecutionResult { InstanceId = instanceId, Status = WorkflowExecutionStatus.Faulted,
                    ErrorMessage = stop.Describe(), Diagnostic = stop.Diagnostic };
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
            using var childDefinitionLease = step.StepType == WorkflowStepType.WorkflowInvocation && pending.ChildBundleDigest != null
                ? await registry.AcquireWorkflowDefinitionAsync(step.WorkflowName!, step.WorkflowVersion!.Value, pending.ChildBundleDigest, cancellationToken) : null;
            var childDataType = childDefinitionLease?.Definition.WorkflowDataType ?? (step.StepType == WorkflowStepType.WorkflowInvocation
                ? await ResolveChildDataTypeAsync(step) : null);
            var decision = PrepareResolution(scopes, step, resolution, childDataType, _defaultActor);
            var dataType = WorkflowTypeIdentity.Resolve(instance.WorkflowDataType)
                ?? throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                    $"Workflow data type '{instance.WorkflowDataType}' is unavailable");
            var workflowData = JsonSerializer.Deserialize(await NewPayloadReader().ReadAsync(instance.WorkflowDataJson), dataType)
                ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "Saved workflow data cannot be read");

            using var signals = Executor.PrepareAssignment(Guid.NewGuid());
            using var recycle = _control.Register(signals.AssignmentId, Executor.Deliver);
            await using var owner = await AcquireAsync(instance, signals, ExecutionClaimKind.Operator);
            if (owner == null)
                return Busy(instanceId);
            using var assignment = Admit(instance, owner, signals.AssignmentId, signals.AcquisitionToken);
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
            _control.Accept(instanceId);
            return await AssignAsync(instance, assignment, signals, cancellationToken);
        }

        public async Task<IReadOnlyList<PendingActivityInfo>> GetPendingActivitiesAsync(string instanceId, CancellationToken cancellationToken = default)
        {
            InstanceIds.Validate(instanceId);
            var instance = await _store.GetWorkflowInstanceAsync(instanceId);
            if (instance?.Status != WorkflowStatus.NeedsResolution || !HasStructuredCheckpoint(instance))
                return [];
            using var definitionLease = await registry.AcquireWorkflowDefinitionAsync(instance.WorkflowName, instance.WorkflowVersion, instance.BundleDigest, cancellationToken);
            using var types = definitionLease.EnterTypes();
            var definition = definitionLease.Definition;
            var scopes = WorkflowDefinitionValidator.ForExecution(definition);
            var checkpoint = await ReadCheckpointAsync(instance);
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
                    step.ActivityType?.Name ?? step.Name, InspectionRedactionPolicy.Default.Reason(pending.ResolutionDiagnostic),
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
            InstanceIds.Validate(instanceId);
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
        public async Task<WorkflowExecutionSnapshot?> GetSnapshotAsync(string instanceId, CancellationToken cancellationToken = default)
        {
            InstanceIds.Validate(instanceId);
            var instance = await _store.GetWorkflowInstanceAsync(instanceId);
            if (instance == null || !HasStructuredCheckpoint(instance))
                return null;
            using var definitionLease = await registry.AcquireWorkflowDefinitionAsync(instance.WorkflowName, instance.WorkflowVersion, instance.BundleDigest, cancellationToken);
            using var types = definitionLease.EnterTypes();
            var definition = definitionLease.Definition;
            var scopes = WorkflowDefinitionValidator.ForExecution(definition);
            var reader = NewPayloadReader();
            var checkpoint = await ReadCheckpointAsync(instance, reader);
            if (checkpoint.StructureHash != scopes.StructureHash)
                throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                    scopes.Mismatch(definition, checkpoint.StructureHash));
            var snapshot = ExecutionSnapshotFactory.Create(instance, checkpoint, scopes, Now);
            snapshot.Data = InspectionRedactionPolicy.Default.StoredValue(
                await reader.ReadAsync(instance.WorkflowDataJson), instance.WorkflowDataType, services);
            return snapshot;
        }

        private static void RequireIncarnation(WorkflowInstance? instance, Guid? expected)
        {
            if (expected is { } identity && instance?.Incarnation != identity)
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidState,
                    "The instance was removed or its ID reused before the operation was accepted");
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
                    ?? throw new ArgumentException("A Completed child invocation resolution requires invocationResult containing the child workflow data.", "invocationResult");
                if (result is JsonElement json)
                {
                    try
                    {
                        result = JsonSerializer.Deserialize(json.GetRawText(), childDataType!, JsonSerializerOptions.Web)
                            ?? throw new JsonException();
                    }
                    catch (Exception error) when (error is JsonException or NotSupportedException)
                    {
                        throw new ArgumentException("invocationResult must be valid JSON for the invoked child workflow's declared data type.", "invocationResult");
                    }
                }
                if (!childDataType!.IsInstanceOfType(result))
                    throw new ArgumentException("invocationResult must match the invoked child workflow's declared data type.", "invocationResult");
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
                var property = step.ActivityType!.GetProperty(name, System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.IgnoreCase)
                    ?? throw new ArgumentException($"Activity has no output property '{name}'", nameof(resolution));
                if (property.GetSetMethod(nonPublic: true) == null || property.GetIndexParameters().Length != 0)
                    throw new ArgumentException($"Activity output '{name}' is not writable", nameof(resolution));
                var output = value is JsonElement json ? JsonSerializer.Deserialize(json.GetRawText(), property.PropertyType, JsonSerializerOptions.Web) : value;
                if ((output == null && property.PropertyType.IsValueType &&
                     Nullable.GetUnderlyingType(property.PropertyType) == null) ||
                    (output != null && !property.PropertyType.IsInstanceOfType(output)))
                    throw new ArgumentException($"Activity output '{name}' has the wrong type", nameof(resolution));
                decision.OutputProperties.Add(property.Name, SerializedValue.From(output));
            }
            return decision;
        }

        public async Task<WorkflowExecutionResult> StartAsync<TData>(
            WorkflowDefinition definition,
            TData workflowData,
            WorkflowOptions options,
            CancellationToken cancellationToken,
            bool versionOmitted = false)
            where TData : class
        {
            ArgumentNullException.ThrowIfNull(workflowData);
            WorkflowDefinitionValidator.CheckRootDataType(definition, workflowData);
            if (options.InstanceId != null) InstanceIds.Validate(options.InstanceId);
            if (options.ExecutionTimeout is { } executionTimeout && executionTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(options),
                    "Execution timeout must be positive");
            if (options.InstanceId is { } repeatedId &&
                await _store.GetWorkflowInstanceSummaryAsync(repeatedId) is { } repeated)
            {
                WorkflowDefinitionValidator.Validate(definition);
                return await ExistingStartAsync(repeated, definition, WorkflowDefinitionValidator.ForExecution(definition), versionOmitted, cancellationToken);
            }
            using var definitionLease = definition.BundleDigest is { } digest
                ? await registry.AcquireWorkflowDefinitionAsync(definition.Name, definition.Version, digest, cancellationToken) : null;
            using var types = definitionLease?.EnterTypes();
            var registration = definitionLease?.Definition ?? await registry.GetWorkflowDefinitionAsync(definition.Name, definition.Version);
            if ((registration ?? definition).WorkflowDataType is { } dataType && WorkflowDefinitionValidator.AcceptsRootData(dataType, workflowData))
                WorkflowDefinitionValidator.GetContract(registration ?? definition).ValidateRequired(workflowData, dataType);
            var (instance, checkpoint, scopes) = WorkflowInstances.NewInstance(definition, workflowData, options, Now,
                (services.GetService(typeof(DemandSettings)) as DemandSettings)?.HostPinLifetime,
                registration?.RegistrationDemands);

            if (options.DebugStopOnEntryActor is { } debugActor)
            {
                if (!(hostSettings ?? HostSettings.Default).Debugging)
                    throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Debugging is disabled on this host");
                var branch = checkpoint.Continuations[0];
                checkpoint.Debug = new(1)
                {
                    Stop = new(branch.Id, scopes.StepTable.FirstOrDefault()?.CatalogId ?? "", "",
                        branch.Stack[0].ActivationId, 0, 0, Now) { Reason = "Pause" }
                };
                instance.Status = WorkflowStatus.Paused;
                checkpoint.Trace.Add(new ExecutionTraceEntry { Timestamp = Now, EntryType = TraceEntryType.Paused,
                    Payload = new DebugCommandTrace(debugActor, "launch", 1) });
            }

            try
            {
                using var lease = await gate.EnterAsync(instance.InstanceId, cancellationToken);
                if (options.InstanceId != null)
                    await RefuseRemovedAsync(instance.InstanceId);
                // A repeated start of an existing instance is answered without claiming it, so retries
                // never count as crashes of an instance another host lost (B-08).
                if (options.InstanceId != null &&
                    await _store.GetWorkflowInstanceSummaryAsync(instance.InstanceId) is { } started)
                    return await ExistingStartAsync(started, definition, scopes, versionOmitted, cancellationToken);
                using var signals = Executor.PrepareAssignment(Guid.NewGuid());
                using var recycle = _control.Register(signals.AssignmentId, Executor.Deliver);
                await using var owner = await AcquireAsync(instance, signals)
                        ?? (options.InstanceId == null
                            ? throw new WorkflowEngineException(WorkflowErrorKind.Infrastructure,
                                "The new workflow instance could not acquire execution ownership")
                            : null);
                if (owner == null)
                    return Busy(instance.InstanceId);
                using var assignment = Admit(instance, owner, signals.AssignmentId, signals.AcquisitionToken);
                // Another start may have saved the instance since the first read.
                if (options.InstanceId != null &&
                    await _store.GetWorkflowInstanceSummaryAsync(instance.InstanceId) is { } existing)
                    return await ExistingStartAsync(existing, definition, scopes, versionOmitted, cancellationToken);
                instance.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
                if (definition.BundleDigest == null) await registry.RegisterWorkflowAsync(definition);
                WorkflowCatalogRules.CheckStartable(await registry.GetWorkflowVersionAsync(definition.Name, definition.Version),
                    definition.Name, definition.Version);
                if (options.WhenUnschedulable == WhenUnschedulable.Fail)
                    await WorkflowDemands.RefuseUnschedulableAsync(_store, checkpoint.Demands, cancellationToken);
                var startClaim = await ClaimQueryAsync();
                var handoff = !await MeetsDemandsAsync(instance.InstanceId, checkpoint.Demands, startClaim);
                await PersistAsync(instance, checkpoint, workflowData, scopes, assignment, handoff ? TimeSpan.Zero : null);
                if (checkpoint.HostPinLifetime != null)
                {
                    var saved = await _store.GetWorkflowInstanceSummaryAsync(instance.InstanceId)
                        ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "The created instance disappeared");
                    checkpoint.Demands = WorkflowDemands.Saved(saved);
                    checkpoint.HostPinLifetime = null;
                    instance.ExecutionStateJson = saved.ExecutionStateJson;
                }
                // The start is durable from here; a caller waiting only for acceptance continues.
                _control.Accept(instance.InstanceId);
                if (instance.Status == WorkflowStatus.Paused) return await ExistingResultAsync(instance, cancellationToken);
                if (handoff)
                {
                    _control.Runnable(instance.InstanceId);
                    return Scheduled(instance.InstanceId);
                }
                return await AssignAsync(instance, assignment, signals, cancellationToken);
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
            EventAppendResult appended, CancellationToken cancellationToken = default) => appended.Status switch
        {
            EventAppendStatus.NotFound => throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
                $"Workflow instance '{instanceId}' does not exist").At(instanceId),
            EventAppendStatus.Closed => await AnsweredAsync(instanceId,
                new EventReceipt(eventId, EventOutcome.Closed, EventRejection.InstanceFinished), cancellationToken),
            EventAppendStatus.Rejected => await AnsweredAsync(instanceId,
                new EventReceipt(eventId, EventOutcome.Rejected, appended.Rejection), cancellationToken),
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
        public async Task<IReadOnlyList<OpenWait>> GetOpenWaitsAsync(WaitQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            if (query.InstanceId != null) InstanceIds.Validate(query.InstanceId);
            if (query.InstanceIds != null) InstanceIds.ValidateAll(query.InstanceIds);
            var waits = await _store.GetOpenWaitsAsync(query);
            var reader = NewPayloadReader();
            var result = new List<OpenWait>();
            var leases = new Dictionary<ArtifactDigest, WorkflowDefinitionLease>();
            try
            {
                foreach (var wait in waits)
                {
                    var instance = await _store.GetWorkflowInstanceSummaryAsync(wait.InstanceId);
                    WorkflowDefinitionLease? lease = null;
                    if (instance?.BundleDigest is { } digest && !leases.TryGetValue(digest, out lease))
                        leases.Add(digest, lease = await registry.AcquireWorkflowDefinitionAsync(instance.WorkflowName, instance.WorkflowVersion, digest, cancellationToken));
                    using var types = lease?.EnterTypes();
                    result.Add(new OpenWait(wait.InstanceId, wait.WaitId, wait.Kind, wait.Key, wait.EventType,
                        InspectionRedactionPolicy.Default.EventSchema(wait.EventType), wait.DeadlineUtc, wait.OpenedAtUtc,
                        ReplyToken.Create(wait.InstanceId, wait.WaitId), wait.DefaultPayloadJson == null ? null
                            : InspectionRedactionPolicy.Default.StoredValue(await reader.ReadAsync(wait.DefaultPayloadJson), wait.EventType, services).GetRawText()));
                }
                return result;
            }
            finally { foreach (var lease in leases.Values) lease.Dispose(); }
        }

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
            InstanceIds.Validate(instanceId);
            ArgumentNullException.ThrowIfNull(@event);
            ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
            var type = @event.GetType();
            return new WorkflowEvent(instanceId, eventId, WorkflowTypeIdentity.SavedName(type),
                JsonSerializer.Serialize(@event, type), key, targetWaitId, subject);
        }

        private async Task<EventAppendResult> AppendAsync(WorkflowEvent stored)
        {
            var appended = await _store.AppendEventAsync(stored with { PayloadThresholdBytes = _limits.PayloadThresholdBytes });
            _notifications?.Publish(appended.Notice);
            if (appended.Status is EventAppendStatus.Closed or EventAppendStatus.NotFound)
                await RefuseRemovedAsync(stored.InstanceId);
            return appended.Status == EventAppendStatus.Conflict
                ? throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
                    $"Event ID '{stored.EventId}' was already used for a different event").At(stored.InstanceId)
                : appended;
        }

        // An event answered without running the instance: its state as saved, with the receipt.
        private async Task<WorkflowExecutionResult> AnsweredAsync(string instanceId, EventReceipt receipt, CancellationToken cancellationToken)
        {
            var result = await ReadAsync(instanceId, cancellationToken) ?? throw new WorkflowEngineException(
                WorkflowErrorKind.InvalidRequest, $"Workflow instance '{instanceId}' does not exist").At(instanceId);
            if (result.Status == WorkflowExecutionStatus.Removed)
                throw new WorkflowEngineException(WorkflowErrorKind.InstanceRemoved,
                    $"Workflow instance '{instanceId}' was removed; its ID is reserved").At(instanceId);
            result.Event = receipt;
            return result;
        }

        public Task<WorkflowExecutionResult> RecoverAsync(string instanceId, CancellationToken cancellationToken, Guid? expectedIncarnation = null) =>
            ContinueAsync(instanceId, release: false, cancellationToken, expectedIncarnation: expectedIncarnation);

        /// <summary>
        /// Releases an instance stopped for an operator and runs it. Automatic recovery, including a
        /// parent checking its child, never releases a stop.
        /// </summary>
        public Task<WorkflowExecutionResult> ReleaseAsync(string instanceId, string? actor,
            CancellationToken cancellationToken, Guid? expectedIncarnation = null) =>
            ContinueAsync(instanceId, release: true, cancellationToken, actor: actor ?? _defaultActor, expectedIncarnation: expectedIncarnation);

        // With forEvent, an instance with nothing to run is answered as it is saved: another run took the
        // event already, or it was stored for an instance that is finished or stopped.
        private async Task<WorkflowExecutionResult> ContinueAsync(string instanceId, bool release,
            CancellationToken cancellationToken, bool forEvent = false, string? actor = null, Guid? expectedIncarnation = null)
        {
            InstanceIds.Validate(instanceId);
            if (await FinishTerminationAsync(instanceId, expectedIncarnation))
                return TerminatedResult(instanceId);
            using var lease = await gate.EnterAsync(instanceId, cancellationToken);
            var instance = await _store.GetWorkflowInstanceAsync(instanceId);
            RequireIncarnation(instance, expectedIncarnation);
            if (instance == null)
                await RefuseRemovedAsync(instanceId);
            var claim = await ClaimQueryAsync();
            if (instance != null && !WorkflowStoreRules.IsTerminal(instance.Status) &&
                HasStructuredCheckpoint(instance) && !await MeetsDemandsAsync(instanceId, WorkflowDemands.Saved(instance), claim))
                return Scheduled(instanceId);
            // Even an idle recovery must refuse a different loaded structure before answering
            // from saved positions. Read the schema envelope before interpreting its state.
            var checkedCheckpoint = HasStructuredCheckpoint(instance)
                ? ExecutionCheckpoint.ReadTrunk(instance.ExecutionStateJson) : null;
            var localDefinition = checkedCheckpoint != null && instance!.BundleDigest == null
                ? await registry.GetWorkflowDefinitionAsync(instance.WorkflowName, instance.WorkflowVersion) : null;
            using var definitionLease = checkedCheckpoint == null ? null : instance!.BundleDigest != null
                ? await registry.AcquireWorkflowDefinitionAsync(instance.WorkflowName, instance.WorkflowVersion, instance.BundleDigest, cancellationToken)
                : localDefinition == null ? null : new WorkflowDefinitionLease(localDefinition);
            using var types = definitionLease?.EnterTypes();
            var checkedDefinition = definitionLease?.Definition;
            var checkedScopes = checkedDefinition == null ? null
                : WorkflowDefinitionValidator.ForExecution(checkedDefinition);
            if (checkedScopes != null && checkedCheckpoint!.StructureHash != checkedScopes.StructureHash)
                return Rejected(instanceId, checkedScopes.Mismatch(checkedDefinition!, checkedCheckpoint.StructureHash));
            // Recovery repeats a finished instance's saved result, as starting it again does.
            if (!release && !forEvent && instance != null && WorkflowStoreRules.IsTerminal(instance.Status))
                return await ReadResultAsync(instance, cancellationToken);
            var cancellationReason = await _store.GetCancellationRequestAsync(instanceId);
            var debugState = (await _store.GetDebugStatesAsync([instanceId])).GetValueOrDefault(instanceId);
            if (instance?.Status == WorkflowStatus.Paused && cancellationReason == null &&
                debugState?.Commands.FirstOrDefault(c => c.AcknowledgedRevision == null) is { Kind: DebugCommandKind.EditData, Edit: { } edit } pendingEdit)
            {
                if (!(hostSettings ?? HostSettings.Default).Debugging)
                    return await ExistingResultAsync(instance, cancellationToken);
                await EditUnderGateAsync(instanceId, pendingEdit.Id, edit, pendingEdit.Actor, cancellationToken, instance.Incarnation);
                return await ExistingResultAsync((await _store.GetWorkflowInstanceAsync(instanceId))!, cancellationToken);
            }
            var pendingDebug = debugState?.Commands.Any(c => c.AcknowledgedRevision == null) == true;
            var debugCheckpoint = checkedCheckpoint?.Debug ?? new DebugCheckpoint();
            var pendingResume = debugState?.Commands.Any(c => c.AcknowledgedRevision == null &&
                c.Kind is DebugCommandKind.Continue or DebugCommandKind.Step) == true;
            var hasConditions = debugCheckpoint.Breakpoints.Any(b => b.Condition != null) ||
                debugState?.Commands.Any(c => c.AcknowledgedRevision == null &&
                    c.Breakpoints?.Any(b => b.Condition != null) == true) == true;
            if (cancellationReason == null && (debugCheckpoint.Stepping != null || pendingResume) &&
                (!(hostSettings ?? HostSettings.Default).Debugging ||
                 hasConditions && services.GetService(typeof(IBreakpointConditionCompiler)) == null))
                return await ExistingResultAsync(instance!, cancellationToken);
            var canApplyPausedDebug = pendingDebug;
            if (instance?.Status == WorkflowStatus.Paused && cancellationReason == null && !canApplyPausedDebug)
                return await ExistingResultAsync(instance, cancellationToken);
            var canRecover = instance?.Status == WorkflowStatus.Paused && (cancellationReason != null || canApplyPausedDebug) ||
                instance?.Status == WorkflowStatus.Ready && pendingDebug || instance?.Status is WorkflowStatus.Running or WorkflowStatus.NeedsResolution ||
                instance?.Status == WorkflowStatus.Suspended &&
                (cancellationReason != null || pendingDebug || await HasInboxWorkAsync(instanceId));
            // Another run may have decided the event since the instance was read, so it is read again.
            if (!canRecover || !HasStructuredCheckpoint(instance))
                return forEvent && await ReadAsync(instanceId, cancellationToken) is { } latest ? latest
                    : Rejected(instanceId, release
                        ? "The workflow instance is not stopped for an operator"
                        : "The workflow instance has no interrupted execution to recover");
            var checkpoint = checkedCheckpoint!;
            if (release && checkpoint.OperatorStop == null)
                return Rejected(instanceId, "The workflow instance is not stopped for an operator");
            // A request already applied is being unwound; only a new one changes what the run does.
            var newCancellation = cancellationReason != null && !checkpoint.CancellationRequested &&
                (instance!.Status == WorkflowStatus.Paused || checkpoint.Continuations[0].Status != ContinuationStatus.Completed);
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
            await using var owner = await AcquireAsync(instance, signals,
                release || !forEvent && instance.Status == WorkflowStatus.NeedsResolution
                    ? ExecutionClaimKind.Operator : ExecutionClaimKind.Runnable);
            if (owner == null)
                return Busy(instanceId);
            using var assignment = Admit(instance, owner, signals.AssignmentId, signals.AcquisitionToken);
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
                return await QuarantineAsync(instance, assignment);
            var stopCleared = checkpoint.OperatorStop != null;
            return await AssignAsync(instance, assignment, signals, cancellationToken,
                new RecoveryPreparation([], newCancellation ? cancellationReason : null,
                    stopCleared, release, actor) { ReactivateWaitingActivities = !forEvent || stopCleared }, checkpoint);
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
        private async Task<WorkflowExecutionResult> ReadResultAsync(WorkflowInstance instance, CancellationToken cancellationToken) =>
            ExecutionCheckpoint.StopOf(instance) is { } stop ? Stopped(instance, stop) : await ExistingResultAsync(instance, cancellationToken);

        private async Task<WorkflowExecutionResult> ExistingStartAsync(WorkflowInstance existing, WorkflowDefinition definition,
            WorkflowScopeCatalog scopes, bool versionOmitted, CancellationToken cancellationToken)
        {
            var checkpoint = ExecutionCheckpoint.ReadTrunk(existing.ExecutionStateJson);
            if (existing.WorkflowName != definition.Name || (!versionOmitted && existing.WorkflowVersion != definition.Version))
                throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest,
                    $"Workflow instance '{existing.InstanceId}' belongs to a different definition");
            using var savedDefinition = await registry.AcquireWorkflowDefinitionAsync(existing.WorkflowName, existing.WorkflowVersion, existing.BundleDigest, cancellationToken);
            using var types = savedDefinition.EnterTypes();
            definition = savedDefinition.Definition;
            scopes = WorkflowDefinitionValidator.ForExecution(definition);
            if (checkpoint.StructureHash != scopes.StructureHash)
                throw new WorkflowEngineException(WorkflowErrorKind.ArtifactUnavailable,
                    scopes.Mismatch(definition, checkpoint.StructureHash));
            _control.Accept(existing.InstanceId);
            // A stopped instance is answered without reading its data, which may be what stopped it.
            return checkpoint.OperatorStop is { } stop ? Stopped(existing, stop) : await ExistingResultAsync(existing, cancellationToken);
        }

        private static WorkflowExecutionResult Rejected(string instanceId, string reason, WorkflowErrorKind kind = WorkflowErrorKind.InvalidState) => new()
        {
            InstanceId = instanceId,
            Status = WorkflowExecutionStatus.Faulted,
            ErrorMessage = reason,
            ErrorKind = kind
        };

        private static WorkflowExecutionStatus StatusOf(WorkflowStatus status) => status switch
        {
            WorkflowStatus.Completed => WorkflowExecutionStatus.Success,
            WorkflowStatus.Suspended => WorkflowExecutionStatus.Suspended,
            WorkflowStatus.Paused => WorkflowExecutionStatus.Paused,
            WorkflowStatus.NeedsResolution => WorkflowExecutionStatus.NeedsResolution,
            WorkflowStatus.Failed => WorkflowExecutionStatus.Faulted,
            WorkflowStatus.Cancelled => WorkflowExecutionStatus.Cancelled,
            WorkflowStatus.TimedOut => WorkflowExecutionStatus.TimedOut,
            WorkflowStatus.Terminated => WorkflowExecutionStatus.Terminated,
            _ => WorkflowExecutionStatus.Running
        };

        private static InspectionError? ResultDiagnostic(WorkflowInstance instance) => HasStructuredCheckpoint(instance)
            ? ExecutionCheckpoint.ReadTrunk(instance.ExecutionStateJson).ResultDiagnostic : null;

        private async Task<WorkflowExecutionResult> ExistingResultAsync(WorkflowInstance instance, CancellationToken cancellationToken)
        {
            if (ExecutionCheckpoint.StopOf(instance) is { } stop) return Stopped(instance, stop);
            using var definition = instance.BundleDigest == null ? null
                : await registry.AcquireWorkflowDefinitionAsync(instance.WorkflowName, instance.WorkflowVersion, instance.BundleDigest, cancellationToken);
            using var types = definition?.EnterTypes();
            var reader = new PayloadReader(ReadPayloadBodyAsync, new(_limits.MaxHydratedPayloadBytes));
            var json = await reader.ReadAsync(instance.WorkflowDataJson);
            return new()
        {
            InstanceId = instance.InstanceId,
            Status = StatusOf(instance.Status),
            WorkflowData = WorkflowTypeIdentity.Resolve(instance.WorkflowDataType) is { } dataType
                ? JsonSerializer.Deserialize(json, dataType)
                : null,
            TraceEntries = [],
            Diagnostic = ResultDiagnostic(instance),
            ErrorMessage = instance.LastError,
            ErrorStackTrace = instance.LastErrorStackTrace
            };
        }

        // Runs nested in this scope's run, such as children and inline client calls, never park: the
        // slot they would need to resume is held by the run they are nested in.
        private int _runDepth;

        private async Task<WorkflowExecutionLease?> AcquireAsync(WorkflowInstance instance, ExecutionSignals signals,
            ExecutionClaimKind kind = ExecutionClaimKind.Runnable)
        {
            var owner = await WorkflowExecutionLease.TryAcquireAsync(_store, instance, _leaseSettings, _watch, renewer, await ClaimQueryAsync(),
                _ => Executor.Deliver(new Cancel(signals.AssignmentId, CancelKind.Ownership)),
                reason => Executor.Deliver(new Cancel(signals.AssignmentId, CancelKind.Workflow, reason)),
                sequence => Executor.Deliver(new Arrivals(signals.AssignmentId, sequence)), _time, kind, Released,
                state => Executor.Deliver(new DebugCommands(signals.AssignmentId, state)));
            if (owner != null) _control.ExecutionClaimed = true;
            return owner;
        }

        internal sealed class RunReceipt
        {
            public long Revision;
            public readonly List<ExecutionTraceEntry> Trace = [];
            public DateTime? WakeUtc;
            public WorkflowInstance? Instance;
        }

        private async Task<WorkflowExecutionResult> AssignAsync(WorkflowInstance instance,
            HostedRun assignment, ExecutionSignals signals, CancellationToken cancellationToken,
            RecoveryPreparation? preparation = null, ExecutionCheckpoint? checkpointMetadata = null)
        {
            var depth = Interlocked.Increment(ref _runDepth);
            using var cancellation = cancellationToken.UnsafeRegister(_ =>
                Executor.Deliver(new Cancel(signals.AssignmentId, CancelKind.Run)), null);
            try
            {
                if (preparation?.Released == true) assignment.AcceptOnCommit();
                var checkpoint = checkpointMetadata ?? ExecutionCheckpoint.ReadTrunk(instance.ExecutionStateJson);
                var assigned = instance.CopyForAssignment();
                assigned.Incarnation = instance.Incarnation;
                assigned.ExecutionStateJson = instance.ExecutionStateJson;
                var assign = new Assign(signals.AssignmentId, assigned, instance.PartsAt(instance.Revision),
                    checkpoint.StructureHash, assignment.Owner.Epoch, signals.InboxSequence, _limits, depth == 1, checkpoint.Debug.Breakpoints, preparation)
                {
                    DebugState = (await _store.GetDebugStatesAsync([instance.InstanceId])).GetValueOrDefault(instance.InstanceId),
                    DebuggingEnabled = (hostSettings ?? HostSettings.Default).Debugging
                };
                var ready = await Executor.ExecuteAsync(assign, CancellationToken.None);
                if (ready.AssignmentId != signals.AssignmentId)
                    throw new ExecutionOwnershipLostException(new InvalidOperationException("Ready is not assigned"));
                var readyAdmission = AdmitReady(ready);
                var outcome = ready.Outcome ?? throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "Missing assignment outcome");
                if (readyAdmission == ConflictReason.NotAssigned)
                    outcome = new(AssignmentOutcomeKind.OwnershipLost);
                if (outcome.Kind == AssignmentOutcomeKind.Interrupted)
                {
                    // Unknown may have applied; the next recovery starts with a new store read.
                    var error = _assignments[signals.AssignmentId].LastCommitError ?? new WorkflowEngineException(
                        WorkflowErrorKind.Infrastructure, outcome.ErrorMessage ?? "Execution was interrupted");
                    if (error is WorkflowEngineException engineError)
                        engineError.At(instance.InstanceId, outcome.StepId, outcome.AttemptId);
                    throw error;
                }
                if (outcome.Kind == AssignmentOutcomeKind.RunCancelled)
                    throw new OperationCanceledException(cancellationToken);
                var receipt = assignment.Receipt;
                // Acknowledged commits reconstruct the stored instance without retaining executor objects.
                // After ownership loss another host may have changed it, so only a fresh read is authoritative.
                var saved = outcome.Kind == AssignmentOutcomeKind.OwnershipLost
                    ? await _store.GetWorkflowInstanceAsync(instance.InstanceId)
                    : receipt.Instance ?? instance;
                if (saved == null || saved.Incarnation != instance.Incarnation)
                    return new WorkflowExecutionResult
                    {
                        InstanceId = instance.InstanceId,
                        Status = WorkflowExecutionStatus.Running,
                        ErrorKind = WorkflowErrorKind.InvalidState,
                        ErrorMessage = "Execution ownership was lost; the assigned incarnation is no longer available",
                        TraceEntries = receipt.Trace.ToList()
                    };
                if (outcome.Kind is not (AssignmentOutcomeKind.OwnershipLost or AssignmentOutcomeKind.Interrupted) &&
                    receipt.WakeUtc is { } due &&
                    (outcome.Kind == AssignmentOutcomeKind.Parked ||
                     saved.Status is WorkflowStatus.Suspended or WorkflowStatus.NeedsResolution))
                    _control.Schedule(saved.InstanceId, due);
                else if (WorkflowStoreRules.IsTerminal(saved.Status))
                    _control.Unschedule(saved.InstanceId);
                var result = await ExistingResultAsync(saved, cancellationToken);
                result.Status = outcome.Kind switch
                {
                    AssignmentOutcomeKind.OwnershipLost when WorkflowStoreRules.IsTerminal(saved.Status) => StatusOf(saved.Status),
                    AssignmentOutcomeKind.Parked => WorkflowExecutionStatus.Scheduled,
                    AssignmentOutcomeKind.Drained or AssignmentOutcomeKind.OwnershipLost or AssignmentOutcomeKind.Interrupted => WorkflowExecutionStatus.Running,
                    _ => StatusOf(saved.Status)
                };
                result.TraceEntries = receipt.Trace.ToList();
                result.ErrorMessage = outcome.ErrorMessage ?? result.ErrorMessage;
                result.ErrorType = outcome.ErrorType;
                result.ErrorStackTrace = outcome.ErrorStackTrace ?? result.ErrorStackTrace;
                result.ExecutionTime = Now - saved.StartedAt!.Value;
                return result;
            }
            finally
            {
                Interlocked.Decrement(ref _runDepth);
            }
        }

        internal static WorkflowExecutionResult Busy(string instanceId) => new()
        {
            InstanceId = instanceId,
            Status = WorkflowExecutionStatus.Running,
            ErrorKind = WorkflowErrorKind.InvalidState,
            ErrorMessage = "The workflow instance is executing on another host"
        };

        // A request whose checkpoint would exceed a limit is refused; the instance keeps its saved state.
        private async Task PersistAsync(WorkflowInstance instance, ExecutionCheckpoint checkpoint,
            object workflowData, WorkflowScopeCatalog scopes, HostedRun assignment, TimeSpan? parkFor = null)
        {
            try
            {
                await ExecutionCheckpointWriter.CommitAsync(assignment.Session, instance,
                    checkpoint, workflowData, scopes, Now, parkFor: parkFor);
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
            catch (PayloadHydrationLimitException error)
            {
                throw new WorkflowEngineException(WorkflowErrorKind.ResourceExhausted, error.Message, error);
            }
        }

        private async Task<CheckpointReply> CommitAsync(WorkflowInstance instance, CheckpointCommit commit,
            long epoch, Guid assignmentId, WorkflowInstance basedOn, RunReceipt receipt)
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
                    (services.GetService(typeof(WorkflowHost)) as WorkflowHost)?.NotifyChange(instance.InstanceId);
                    // Commits are incorporated in order; a replay cannot add trace or replace newer state.
                    if (result.Revision > receipt.Revision)
                    {
                        receipt.Revision = result.Revision;
                        receipt.Instance = commit.ReadTrunk();
                        if (commit.ApplyParts(basedOn.SavedParts).TryGetValue(CheckpointParts.DataKey, out var data))
                            receipt.Instance.WorkflowDataJson = data;
                        receipt.Instance.Revision = result.Revision;
                        receipt.Instance.Incarnation = commit.Incarnation;
                        receipt.Instance.RunnableAtUtc = result.RunnableAtUtc;
                        receipt.Trace.AddRange(commit.TraceEntries.Select(json =>
                            JsonSerializer.Deserialize<ExecutionTraceEntry>(json)!));
                        receipt.WakeUtc = result.RunnableAtUtc is { } due
                            ? Now + (due > result.StoreTimeUtc ? due - result.StoreTimeUtc : TimeSpan.Zero)
                            : null;
                        // A child the commit started, or a parent its completion woke, runs here without waiting
                        // for the poll (D-17).
                        foreach (var runnable in result.MadeRunnable)
                            _control.Runnable(runnable.InstanceId);
                    }
                    _notifications?.Publish(result.Notice);
                    if (result.Notice?.Idle == true) _control.Changed(commit.InstanceId);
                    foreach (var notice in result.MadeRunnable) _notifications?.Publish(notice);
                    return new CheckpointReply(assignmentId, CheckpointReplyKind.Ack, result.Revision,
                        PendingCancellation: result.PendingCancellation, Parked: result.Parked,
                        UnreadEvents: result.UnreadEvents);
                case WorkflowCommitStatus.TerminationRequested:
                    return new CheckpointReply(assignmentId, CheckpointReplyKind.Conflict, result.Revision,
                        "Termination superseded this transition", PendingCancellation: result.PendingCancellation,
                        Conflict: ConflictReason.Revision);
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
            ErrorKind = stop.Kind == OperatorStopKind.PayloadUnavailable ? WorkflowErrorKind.PayloadUnavailable : null,
            Diagnostic = stop.Diagnostic,
            ErrorMessage = stop.Describe()
        };

        // A claim that finds the instance lost by MaxCrashes owners in a row stops it instead of running
        // it (B-08). Nothing of the workflow runs and its data is not read; the last checkpoint stays.
        // The stop clears the live count, so a released instance gets MaxCrashes new attempts.
        private async Task<WorkflowExecutionResult> QuarantineAsync(WorkflowInstance instance,
            HostedRun assignment)
        {
            var owner = assignment.Owner;
            var stop = new OperatorStopState
            {
                Kind = OperatorStopKind.Quarantine,
                Reason = $"{owner.CrashCount} owners in a row lost it while it was running",
                StoppedAtUtc = Now,
                CrashCount = owner.CrashCount
            };
            var commit = CheckpointCommit.StopForOperator(instance, owner.Epoch, stop, clearsCrashCount: true);
            for (var attempt = 0; ; attempt++)
            {
                var reply = await assignment.Session.CheckpointAsync(commit);
                if (reply.Kind == CheckpointReplyKind.Ack) break;
                if (reply.Kind == CheckpointReplyKind.Conflict) throw new ExecutionOwnershipLostException(new InvalidOperationException(reply.Error));
                if (attempt == 2) throw new CheckpointInterruptedException(new IOException(reply.Error));
                await Task.Delay(25 * (1 << attempt));
            }
            return Stopped(instance, stop);
        }

        private void CheckLimit(CheckpointCommit commit, WorkflowInstance saved)
        {
            if (commit.PreservesPayloadPins)
            {
                if (commit.Parts.Count != 0 || commit.DeletedParts.Count != 0 || commit.ReplacesParts || !commit.Stopped)
                    throw new WorkflowEngineException(WorkflowErrorKind.InvalidRequest, "Invalid metadata-only stop");
                return;
            }
            var trunk = commit.ReadTrunk().ExecutionStateJson;
            var parts = commit.ApplyParts(saved.PartsAt(saved.Revision));
            var size = Encoding.UTF8.GetByteCount(trunk) + parts.Values.Sum(value => value.CheckpointBytes);
            if (size <= _limits.MaxCheckpointBytes) return;
            var sameData = parts.GetValueOrDefault(CheckpointParts.DataKey) == saved.WorkflowDataJson;
            var proposedMetadata = ExecutionCheckpoint.ReadTrunk(trunk);
            if (sameData && commit.Instance.Status == WorkflowStatus.Failed && proposedMetadata.Continuations.Count == 0 &&
                proposedMetadata.Waits.Count == 0 && proposedMetadata.Joins.Count == 0) return;
            if (sameData && commit.Stopped && ExecutionCheckpoint.ReadTrunk(saved.ExecutionStateJson).StoredTrunk &&
                saved.SavedParts is { } kept && parts.Count == kept.Count && parts.All(p => kept.GetValueOrDefault(p.Key) == p.Value))
            {
                var proposedNode = System.Text.Json.Nodes.JsonNode.Parse(trunk)!.AsObject();
                var savedNode = System.Text.Json.Nodes.JsonNode.Parse(saved.ExecutionStateJson)!.AsObject();
                proposedNode.Remove(nameof(ExecutionCheckpoint.OperatorStop));
                savedNode.Remove(nameof(ExecutionCheckpoint.OperatorStop));
                proposedNode.Remove(nameof(ExecutionCheckpoint.Demands));
                savedNode.Remove(nameof(ExecutionCheckpoint.Demands));
                if (System.Text.Json.Nodes.JsonNode.DeepEquals(proposedNode, savedNode)) return;
            }
            if (parts.Any(p => p.Key != CheckpointParts.DataKey && p.Value.Reference != null))
                throw new CheckpointLimitExceededException(size, _limits.MaxCheckpointBytes);
            // A stop marker may exceed the limit only when it keeps the entire saved checkpoint.
            // A terminal serialization failure may discard continuations while keeping saved data.
            var proposed = ExecutionCheckpoint.Read(StoredCheckpoint.AssembleState(trunk, parts));
            var previous = ExecutionCheckpoint.Read(StoredCheckpoint.WholeState(saved));
            // Release keeps saved data but applies recovery preparation and records the operator action.
            // Like recording the stop, clearing it must remain possible after the limit was lowered.
            if (sameData && previous.OperatorStop != null && proposed.OperatorStop == null) return;
            if (sameData && (commit.Stopped || previous.OperatorStop != null ||
                             commit.Instance.Status == WorkflowStatus.Terminated))
            {
                proposed.OperatorStop = null;
                previous.OperatorStop = null;
                // Stops add a safe identity, and termination prunes identities for closed activities.
                // Neither changes the saved execution positions or workflow data being compared.
                proposed.Diagnostics = null;
                previous.Diagnostics = null;
                proposed.ResultDiagnostic = null;
                previous.ResultDiagnostic = null;
                // Updates omit the immutable placement set; it is not an execution-state change.
                proposed.Demands = [];
                previous.Demands = [];
                if (JsonSerializer.Serialize(proposed) == JsonSerializer.Serialize(previous)) return;
            }
            if (sameData && commit.Instance.Status == WorkflowStatus.Failed && proposed.Continuations.Count == 0 &&
                proposed.Waits.Count == 0 && proposed.Joins.Count == 0) return;
            throw new CheckpointLimitExceededException(size, _limits.MaxCheckpointBytes);
        }

        private async Task<StartChildReply> ValidateChildAsync(StartChild request, WorkflowInstance child, CancellationToken cancellationToken)
        {
            using var definitionLease = await registry.AcquireWorkflowDefinitionAsync(child.WorkflowName, child.WorkflowVersion, child.BundleDigest, cancellationToken);
            using var types = definitionLease.EnterTypes();
            var definition = definitionLease.Definition;
            WorkflowVersion? row;
            try
            {
                // A child start is refused like a client start when its version is a draft or retired.
                row = await registry.GetWorkflowVersionAsync(child.WorkflowName, child.WorkflowVersion);
                WorkflowCatalogRules.CheckStartable(row, child.WorkflowName, child.WorkflowVersion);
            }
            catch (WorkflowEngineException error)
            {
                return new(request.AssignmentId, Refusal: error.Kind, Message: error.Message);
            }
            if (definition.Name != child.WorkflowName || definition.Version != child.WorkflowVersion)
                return new(request.AssignmentId, Refusal: WorkflowErrorKind.ArtifactUnavailable,
                    Message: "The child definition has a different declared identity");
            _ = WorkflowDefinitionValidator.ForExecution(definition);
            var hash = await registry.GetWorkflowStructureHashAsync(child.WorkflowName, child.WorkflowVersion);
            var checkpoint = ExecutionCheckpoint.Read(child.ExecutionStateJson);
            if (child.WorkflowDataType != WorkflowTypeIdentity.SavedName(definition.WorkflowDataType!))
                return new(request.AssignmentId, Refusal: WorkflowErrorKind.ArtifactUnavailable,
                    Message: $"Workflow {definition.Name} v{definition.Version} differs from its registered definition: " +
                        $"registered data type '{WorkflowTypeIdentity.SavedName(definition.WorkflowDataType!)}', " +
                        $"loaded data type '{child.WorkflowDataType}'");
            if (hash != request.StructureHash || checkpoint.StructureHash != hash)
                return new(request.AssignmentId, Refusal: WorkflowErrorKind.ArtifactUnavailable,
                    Message: $"Workflow {definition.Name} v{definition.Version} differs from its registered definition: " +
                        $"registered structure hash '{hash}', loaded structure hash " +
                        $"'{(hash != request.StructureHash ? request.StructureHash : checkpoint.StructureHash)}'");
            if (row!.ContractHash != request.ContractHash)
                return new(request.AssignmentId, Refusal: WorkflowErrorKind.ArtifactMismatch,
                    Message: $"Workflow {definition.Name} v{definition.Version} contract hash differs: registered '{row.ContractHash}', invoked '{request.ContractHash}'");
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

        internal ConflictReason? AdmitReady(Ready ready)
        {
            ExecutionContract.RequireVersion(ready.ContractVersion);
            return _assignments.TryGetValue(ready.AssignmentId, out var assignment) &&
                (assignment.ReleasedBy(ready.LastAcknowledgedCommit) || assignment.IsActive(assignment.InstanceId))
                ? null : ConflictReason.NotAssigned;
        }

        internal HostedRun Admit(WorkflowInstance instance, WorkflowExecutionLease owner, Guid identity, CancellationToken acquisitionToken = default)
        {
            var run = new HostedRun(this, instance, owner, identity, acquisitionToken);
            if (!_assignments.TryAdd(identity, run)) throw new InvalidOperationException("Assignment already admitted");
            return run;
        }

        /// <summary>Routes requests only to their admitted assignment. No rejection touches the store.</summary>
        internal sealed class ExecutionHost(InstanceOperations operations) : IExecutionHost
        {
            private HostedRun? Find(Guid id, string instanceId) =>
                operations._assignments.TryGetValue(id, out var run) && run.IsActive(instanceId) ? run : null;

            public Task<CheckpointReply> CheckpointAsync(Checkpoint request) =>
                operations._assignments.TryGetValue(request.AssignmentId, out var run)
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
            public Task<ReadPayloadReply> ReadPayloadAsync(ReadPayload request) =>
                Find(request.AssignmentId, request.InstanceId) is { } run
                    ? run.PayloadTransfer.ReadAsync(request)
                    : Task.FromResult(new ReadPayloadReply(request.AssignmentId, Conflict: ConflictReason.NotAssigned));
        }

        internal sealed class HostedRun(InstanceOperations operations, WorkflowInstance instance,
            WorkflowExecutionLease owner, Guid identity, CancellationToken acquisitionToken) : IDisposable
        {
            private readonly CommitQueue _queue = new();
            internal readonly RunReceipt Receipt = new();
            private readonly Dictionary<string, (long Revision, string Hash, Task<CheckpointReply> Task)> _outstanding = [];
            private sealed record RetryableCommit(string CommitId, long Revision, string Hash);
            private RetryableCommit? _retryable;
            private WorkflowInstance _saved = instance;
            private AcknowledgedCommit? _releasedBy;
            private bool _acceptOnCommit;
            public void AcceptOnCommit() => _acceptOnCommit = true;
            private ExecutionPayloadTransfer? _payloadTransfer;
            public ExecutionPayloadTransfer PayloadTransfer => _payloadTransfer ??= new(operations.PayloadStore, operations._limits);
            public Exception? LastCommitError { get; set; }
            public WorkflowExecutionLease Owner => owner;
            public string InstanceId => instance.InstanceId;
            public ExecutionSession Session { get; } = new(new ExecutionHost(operations), identity,
                instance.InstanceId, owner.Epoch, operations._limits, false) { SavedInstance = instance };

            public void Dispose()
            {
                operations._assignments.TryRemove(identity, out _);
            }

            public bool IsActive(string instanceId)
            {
                if (instanceId != instance.InstanceId || !operations._assignments.ContainsKey(identity) ||
                    Volatile.Read(ref _releasedBy) != null) return false;
                try { owner.ThrowIfLost(); return true; }
                catch (ExecutionOwnershipLostException) { return false; }
            }

            public bool ReleasedBy(AcknowledgedCommit? acknowledged) =>
                acknowledged != null && acknowledged == Volatile.Read(ref _releasedBy);

            private bool CanCommit(CheckpointCommit commit)
            {
                if (IsActive(commit.InstanceId)) return true;
                // A lost reply may have released the lease. Only that exact unresolved commit may
                // cross the store's fencing check after loss; it cannot introduce a new transition.
                var retryable = Volatile.Read(ref _retryable);
                return commit.InstanceId == instance.InstanceId && operations._assignments.ContainsKey(identity) &&
                    retryable != null && retryable.CommitId == commit.CommitId &&
                    retryable.Revision == commit.ExpectedRevision &&
                    retryable.Hash == Convert.ToHexString(commit.ContentHash);
            }

            public Task<CheckpointReply> CheckpointAsync(Checkpoint request)
            {
                var commit = request.Commit;
                if (request.AssignmentId != identity || !CanCommit(commit) ||
                    commit.Epoch != owner.Epoch || commit.Incarnation != instance.Incarnation)
                    return Task.FromResult(new CheckpointReply(request.AssignmentId, CheckpointReplyKind.Conflict,
                        Conflict: ConflictReason.NotAssigned));
                Task preceding;
                TaskCompletionSource<CheckpointReply> completion;
                lock (_queue.Sync)
                {
                    var hash = Convert.ToHexString(commit.ContentHash);
                    if (_outstanding.TryGetValue(commit.CommitId, out var pending))
                        return pending.Revision == commit.ExpectedRevision && pending.Hash == hash
                            ? pending.Task
                            : Task.FromResult(new CheckpointReply(identity, CheckpointReplyKind.Conflict,
                                Conflict: ConflictReason.Revision));
                    completion = new TaskCompletionSource<CheckpointReply>(TaskCreationOptions.RunContinuationsAsynchronously);
                    preceding = _queue.Tail;
                    _outstanding.Add(commit.CommitId, (commit.ExpectedRevision, hash, completion.Task));
                    _queue.Tail = completion.Task;
                }
                // Registration and ordering are complete before entering the store. Synchronous
                // stores can finish inline, without scheduling an extra work item for every commit.
                _ = ApplyAsync(preceding, request, completion);
                return completion.Task;
            }

            private async Task ApplyAsync(Task preceding, Checkpoint request, TaskCompletionSource<CheckpointReply> completion)
            {
                try
                {
                    try { await preceding; } catch { /* A failed request does not break the queue. */ }
                    var commit = request.Commit;
                    CheckpointReply reply;
                    if (!CanCommit(commit) || request.AssignmentId != identity ||
                        commit.Epoch != owner.Epoch || commit.Incarnation != instance.Incarnation)
                        reply = new(identity, CheckpointReplyKind.Conflict, Conflict: ConflictReason.NotAssigned);
                    else
                    {
                        if (request.Payloads is { Count: > 0 } payloads) commit = PayloadTransfer.PrepareCommit(commit, payloads);
                        reply = await ApplyCommitAsync(commit);
                    }
                    lock (_queue.Sync) _outstanding.Remove(commit.CommitId);
                    completion.TrySetResult(reply);
                }
                catch (WorkflowEngineException error) when (error.Kind == WorkflowErrorKind.Infrastructure)
                {
                    LastCommitError = error;
                    lock (_queue.Sync) _outstanding.Remove(request.Commit.CommitId);
                    completion.TrySetResult(new(identity, CheckpointReplyKind.Unknown, Error: error.Message));
                }
                catch (Exception error)
                {
                    lock (_queue.Sync) _outstanding.Remove(request.Commit.CommitId);
                    completion.TrySetException(error);
                }
            }

            private async Task<CheckpointReply> ApplyCommitAsync(CheckpointCommit commit)
            {
                operations.CheckLimit(commit, _saved);
                var reply = await operations.CommitAsync(instance, commit, owner.Epoch, identity, _saved, Receipt);
                if (reply.Kind is CheckpointReplyKind.Unknown or CheckpointReplyKind.Retry)
                    Volatile.Write(ref _retryable, new RetryableCommit(commit.CommitId,
                        commit.ExpectedRevision, Convert.ToHexString(commit.ContentHash)));
                else if (Volatile.Read(ref _retryable)?.CommitId == commit.CommitId)
                    Volatile.Write(ref _retryable, null);
                if (reply.Kind == CheckpointReplyKind.Ack && reply.Revision > _saved.Revision)
                {
                    _saved = commit.ReadSavedInstance(_saved);
                    _saved.Incarnation = commit.Incarnation;
                    if (_acceptOnCommit)
                    {
                        _acceptOnCommit = false;
                        operations._control.Accept(instance.InstanceId);
                    }
                    var keepsLease = reply.UnreadEvents && _saved.Status == WorkflowStatus.Suspended;
                    if ((_saved.Status != WorkflowStatus.Running || reply.Parked) && !keepsLease)
                    {
                        Volatile.Write(ref _releasedBy, new AcknowledgedCommit(commit.CommitId, reply.Revision));
                        owner.StopRenewing();
                    }
                }
                if (reply.PendingCancellation is { } reason) owner.ObserveCancellation(reason);
                if (reply.Kind == CheckpointReplyKind.Conflict && commit.Instance.Status != WorkflowStatus.Terminated &&
                    reply.PendingCancellation?.Mode == WorkflowControlMode.Terminate)
                {
                    try
                    {
                        if (await operations.PrepareTerminationAsync(instance) is { } prepared)
                        {
                            // Already in the commit queue: preserve the original executor conflict even
                            // if preparing or committing the host's terminal transition fails.
                            var termination = prepared.Commit(owner.Epoch);
                            for (var attempt = 1; ; attempt++)
                            {
                                var ended = await ApplyCommitAsync(termination);
                                if (ended.Kind == CheckpointReplyKind.Ack ||
                                    ended.Kind is not (CheckpointReplyKind.Retry or CheckpointReplyKind.Unknown) || attempt == 3)
                                    break;
                            }
                        }
                    }
                    catch (Exception error)
                    {
                        LastCommitError = error;
                    }
                }
                return reply;
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
                return await operations.ValidateChildAsync(request, child, acquisitionToken);
            }
        }
    }
}
