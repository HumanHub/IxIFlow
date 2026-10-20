using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

public sealed partial class WorkflowHost
{
    /// <summary>
    /// Runs instance operations inline on the current scope's <see cref="InstanceOperations"/>. The host uses it for
    /// each work item, and a call made from inside a slot, such as an activity calling the client, uses it to run in
    /// that slot. Children are independent instances that the host schedules on their own.
    /// </summary>
    internal sealed class SlotClient
    {
        public Task<DeliverySummary?> GetDeliveryAsync(string eventId, string publisherName) => _runner.GetDeliveryAsync(eventId, publisherName);
        public Task<DeliveryPage> ListDeliveriesAsync(DeliveryQuery query) => _runner.ListDeliveriesAsync(query);
        public Task<bool> RetryDeliveryAsync(string eventId, string publisherName, string actor) => _runner.RetryDeliveryAsync(eventId, publisherName, actor);
        public Task<IReadOnlyList<OutboxPublisherState>> ListPublishersAsync() => _runner.ListPublishersAsync();
        public Task<bool> EnablePublisherAsync(string publisherName, string actor) => _runner.EnablePublisherAsync(publisherName, actor);
        public Task UnregisterPublisherAsync(string publisherName, string actor) => _runner.UnregisterPublisherAsync(publisherName, actor);
        public Task<InstancePage> SearchInstancesAsync(InstanceQuery query) => _runner.SearchInstancesAsync(query);
        public Task<IReadOnlyList<StoredTraceEntry>> GetTracePageAsync(string id, long after, int limit) => _runner.GetTracePageAsync(id, after, limit);
        public Task<WorkflowControlReceipt> RequestTerminationAsync(string id, string code, string? description, string? actor, Guid? expectedIncarnation = null) => _runner.RequestTerminationAsync(id, code, description, actor, expectedIncarnation);
        public Task<bool> FinishTerminationAsync(string id, Guid incarnation) => _runner.FinishTerminationAsync(id, expectedIncarnation: incarnation);
        private readonly InstanceOperations _runner;

        public SlotClient(InstanceOperations runner)
        {
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        }

        public Task<WorkflowExecutionResult> RunAsync<TWorkflowData>(
            WorkflowDefinition definition,
            TWorkflowData workflowData,
            WorkflowOptions? options = null,
            CancellationToken cancellationToken = default,
            bool versionOmitted = false)
            where TWorkflowData : class
        {
            ArgumentNullException.ThrowIfNull(definition);
            ArgumentNullException.ThrowIfNull(workflowData);
            return _runner.StartAsync(definition, workflowData, options ?? new WorkflowOptions(),
                cancellationToken, versionOmitted);
        }

        public Task<WorkflowExecutionResult> ResumeAsync<TEventData>(
            string instanceId,
            TEventData @event,
            CancellationToken cancellationToken = default)
            where TEventData : class =>
            ForInstanceAsync(instanceId, _runner.ResumeAsync(instanceId, null, @event, cancellationToken));

        public Task<WorkflowExecutionResult> ResumeAsync<TEventData>(
            string instanceId,
            string key,
            TEventData @event,
            CancellationToken cancellationToken = default)
            where TEventData : class
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            return ForInstanceAsync(instanceId, _runner.ResumeAsync(instanceId, key, @event, cancellationToken));
        }

        public Task<WorkflowExecutionResult> ResumeAsync<TEventData>(
            string instanceId, string? key, TEventData @event, string eventId,
            CancellationToken cancellationToken = default)
            where TEventData : class
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
            return ForInstanceAsync(instanceId,
                _runner.ResumeAsync(instanceId, key, @event, cancellationToken, eventId));
        }

        /// <summary>Stores an event without running the instance.</summary>
        public Task<EventAppendResult> DeliverAsync<TEventData>(string instanceId, string? key, TEventData @event,
            string eventId) where TEventData : class =>
            _runner.DeliverAsync(instanceId, key, @event, eventId);

        /// <summary>Stores a reply addressed to one wait without running the instance.</summary>
        public Task<EventAppendResult> DeliverReplyAsync<TReply>(string instanceId, string waitId, TReply reply,
            string eventId, string? subject) where TReply : class =>
            _runner.DeliverReplyAsync(instanceId, waitId, reply, eventId, subject);

        /// <summary>The result for an event the inbox answered without keeping it, or null when it was stored.</summary>
        public Task<WorkflowExecutionResult?> AnswerAsync(string instanceId, string eventId, EventAppendResult appended) =>
            _runner.AnswerAsync(instanceId, eventId, appended);

        /// <summary>Runs the instance so its owner decides a stored event.</summary>
        public Task<WorkflowExecutionResult> TakeEventAsync(string instanceId, string eventId,
            CancellationToken cancellationToken = default) =>
            ForInstanceAsync(instanceId, _runner.TakeEventAsync(instanceId, eventId, cancellationToken));

        public Task<EventReceipt?> ReadEventAsync(string instanceId, string eventId) =>
            _runner.ReadEventAsync(instanceId, eventId);

        public Task<IReadOnlyList<StoredWait>> GetOpenWaitsAsync(WaitQuery query) => _runner.GetOpenWaitsAsync(query);

        public Task<WorkflowExecutionResult> RecoverAsync(
            string instanceId,
            CancellationToken cancellationToken = default, Guid? expectedIncarnation = null) =>
            ForInstanceAsync(instanceId, _runner.RecoverAsync(instanceId, cancellationToken, expectedIncarnation));

        public Task<WorkflowExecutionResult> ReleaseAsync(
            string instanceId,
            string? actor = null,
            CancellationToken cancellationToken = default, Guid? expectedIncarnation = null) =>
            ForInstanceAsync(instanceId, _runner.ReleaseAsync(instanceId, actor, cancellationToken, expectedIncarnation));

        public Task<WorkflowExecutionResult> CancelAsync(
            string instanceId, CancellationReason reason,
            CancellationToken cancellationToken = default) =>
            ForInstanceAsync(instanceId, _runner.CancelAsync(instanceId, reason, cancellationToken));

        /// <summary>
        /// Saves a cancellation request. Returns the result when nothing has to run, or null when
        /// the instance is idle and must be recovered to unwind.
        /// </summary>
        public async Task<WorkflowExecutionResult?> RequestCancellationAsync(string instanceId,
            CancellationReason reason, CancellationToken cancellationToken = default, Guid? expectedIncarnation = null)
        {
            try
            {
                return await _runner.RequestCancellationAsync(instanceId, reason, cancellationToken, expectedIncarnation);
            }
            catch (WorkflowEngineException error) when (WorkflowEngineException.IsEngineFailure(error))
            {
                error.At(instanceId);
                throw;
            }
        }

        /// <summary>The saved state of an instance as a result, or null when it does not exist.</summary>
        public Task<WorkflowExecutionResult?> ReadAsync(string instanceId) => _runner.ReadAsync(instanceId);

        public Task<WorkflowExecutionStatus?> ReadStatusAsync(string instanceId) => _runner.ReadStatusAsync(instanceId);

        public Task<WorkflowExecutionResult> RequestDebugAsync(string id, DebugCommandKind kind, string actor, CancellationToken token, Guid? expectedIncarnation = null) =>
            _runner.RequestDebugAsync(id, kind, actor, token, expectedIncarnation: expectedIncarnation);

        public Task<WorkflowExecutionResult> RequestStepAsync(string id, string continuationId, DebugStepKind kind,
            string actor, CancellationToken token, Guid? expectedIncarnation = null) =>
            _runner.RequestDebugAsync(id, DebugCommandKind.Step, actor, token, continuationId, kind, expectedIncarnation);

        public Task<DataEditResult> EditDataAsync(string id, string editId, long expectedRevision,
            DataEditTarget target, IReadOnlyList<DataPatch> patches, string actor, CancellationToken token, Guid? expectedIncarnation = null) =>
            _runner.EditDataAsync(id, editId, expectedRevision, target, patches, actor, token, expectedIncarnation);

        public Task<IReadOnlyList<Breakpoint>> SetBreakpointsAsync(string id, IReadOnlyList<Breakpoint> breakpoints, string actor, CancellationToken token, Guid? expectedIncarnation = null) =>
            _runner.SetBreakpointsAsync(id, breakpoints, actor, token, expectedIncarnation);
        public Task<IReadOnlyList<Breakpoint>> GetBreakpointsAsync(string id, CancellationToken token) =>
            _runner.GetBreakpointsAsync(id, token);

        public Task<WorkflowExecutionResult> ResolveAsync(
            string instanceId, string invocationId, ActivityResolution resolution,
            CancellationToken cancellationToken = default, Guid? expectedIncarnation = null) =>
            ForInstanceAsync(instanceId, _runner.ResolveAsync(instanceId, invocationId, resolution, cancellationToken, expectedIncarnation));

        public Task<IReadOnlyList<ExecutionTraceEntry>> GetTraceAsync(string instanceId) =>
            _runner.GetTraceAsync(instanceId);

        public Task<InstanceInspection?> InspectAsync(string id, CancellationToken token) => _runner.InspectAsync(id, token);
        public Task<IReadOnlyList<StepTableEntry>> GetStepTableAsync(string id, CancellationToken token) => _runner.GetStepTableAsync(id, token);
        public Task<TraceReplay> OpenReplayAsync(string id, int maxEntries, CancellationToken token) => _runner.OpenReplayAsync(id, maxEntries, token);
        public Task<string?> GetSourceAsync(string id, string path, CancellationToken token) => _runner.GetSourceAsync(id, path, token);
        public Task<(WorkflowDefinition, object)> PrepareDebugLaunchAsync(string workflow, int version, System.Text.Json.JsonElement input) =>
            _runner.PrepareDebugLaunchAsync(workflow, version, input);
        public Task<IReadOnlyList<StoredTraceEntry>> GetTraceAsync(string id, long afterSequence, int limit,
            CancellationToken token) => _runner.GetTracePageAsync(id, afterSequence, limit, token);

        public async Task<WorkflowExecutionSnapshot?> GetSnapshotAsync(string instanceId)
        {
            try
            {
                return await _runner.GetSnapshotAsync(instanceId);
            }
            catch (WorkflowEngineException error) when (WorkflowEngineException.IsEngineFailure(error))
            {
                error.At(instanceId);
                throw;
            }
        }

        public async Task<IReadOnlyList<PendingActivityInfo>> GetPendingActivitiesAsync(string instanceId)
        {
            try
            {
                return await _runner.GetPendingActivitiesAsync(instanceId);
            }
            catch (WorkflowEngineException error) when (WorkflowEngineException.IsEngineFailure(error))
            {
                error.At(instanceId);
                throw;
            }
        }

        // An engine failure names the instance it belongs to. A commit that lost a race means
        // another owner is running the instance.
        private static async Task<WorkflowExecutionResult> ForInstanceAsync(string instanceId,
            Task<WorkflowExecutionResult> operation)
        {
            try
            {
                return await operation;
            }
            catch (ExecutionOwnershipLostException)
            {
                return InstanceOperations.Busy(instanceId);
            }
            catch (WorkflowEngineException error) when (WorkflowEngineException.IsEngineFailure(error))
            {
                error.At(instanceId);
                throw;
            }
        }
    }
}
