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
        public Task<UnschedulablePage> GetUnschedulableAsync(int limit, UnschedulableCursor? cursor, CancellationToken cancellationToken) =>
            _runner.GetUnschedulableAsync(limit, cursor, cancellationToken);

        public Task<DeliverySummary?>
            GetDeliveryAsync(string eventId, string publisherName, CancellationToken cancellationToken = default) =>
            _runner.GetDeliveryAsync(eventId, publisherName, cancellationToken: cancellationToken);

        public Task<DeliveryPage> ListDeliveriesAsync(DeliveryQuery query, CancellationToken cancellationToken = default) =>
            _runner.ListDeliveriesAsync(query, cancellationToken: cancellationToken);

        public Task<bool> RetryDeliveryAsync(string eventId, string publisherName, string actor,
            CancellationToken cancellationToken = default) =>
            _runner.RetryDeliveryAsync(eventId, publisherName, actor, cancellationToken: cancellationToken);

        public Task<IReadOnlyList<OutboxPublisherState>> ListPublishersAsync(CancellationToken cancellationToken = default) =>
            _runner.ListPublishersAsync(cancellationToken: cancellationToken);

        public Task<bool> EnablePublisherAsync(string publisherName, string actor, CancellationToken cancellationToken = default) =>
            _runner.EnablePublisherAsync(publisherName, actor, cancellationToken: cancellationToken);

        public Task UnregisterPublisherAsync(string publisherName, string actor, CancellationToken cancellationToken = default) =>
            _runner.UnregisterPublisherAsync(publisherName, actor, cancellationToken: cancellationToken);

        public Task<InstancePage> SearchInstancesAsync(InstanceQuery query, CancellationToken cancellationToken = default) =>
            _runner.SearchInstancesAsync(query, cancellationToken: cancellationToken);

        public Task<IReadOnlyList<StoredTraceEntry>> ReadTracePageAsync(string id, long after, int limit,
            CancellationToken cancellationToken = default) =>
            _runner.ReadTracePageAsync(id, after, limit, cancellationToken: cancellationToken);

        public Task<WorkflowControlReceipt> RequestTerminationAsync(string id, string code, string? description, string? actor,
            Guid? expectedIncarnation = null, CancellationToken cancellationToken = default) => _runner.RequestTerminationAsync(id, code,
            description, actor, expectedIncarnation, cancellationToken: cancellationToken);

        public Task<bool> FinishTerminationAsync(string id, Guid incarnation) =>
            _runner.FinishTerminationAsync(id, expectedIncarnation: incarnation);

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
            ForInstanceAsync(instanceId, _runner.ResumeAsync(instanceId, null, @event, cancellationToken),
                cancellationToken: cancellationToken);

        public Task<WorkflowExecutionResult> ResumeAsync<TEventData>(
            string instanceId,
            string key,
            TEventData @event,
            CancellationToken cancellationToken = default)
            where TEventData : class
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);

            return ForInstanceAsync(instanceId, _runner.ResumeAsync(instanceId, key, @event, cancellationToken),
                cancellationToken: cancellationToken);
        }

        public Task<WorkflowExecutionResult> ResumeAsync<TEventData>(
            string instanceId, string? key, TEventData @event, string eventId,
            CancellationToken cancellationToken = default)
            where TEventData : class
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(eventId);

            return ForInstanceAsync(instanceId,
                _runner.ResumeAsync(instanceId, key, @event, cancellationToken, eventId), cancellationToken: cancellationToken);
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
        public Task<WorkflowExecutionResult?> AnswerAsync(string instanceId, string eventId, EventAppendResult appended,
            CancellationToken cancellationToken = default) =>
            _runner.AnswerAsync(instanceId, eventId, appended, cancellationToken);

        /// <summary>Runs the instance so its owner decides a stored event.</summary>
        public Task<WorkflowExecutionResult> TakeEventAsync(string instanceId, string eventId,
            CancellationToken cancellationToken = default) =>
            ForInstanceAsync(instanceId, _runner.TakeEventAsync(instanceId, eventId, cancellationToken),
                cancellationToken: cancellationToken);

        public Task<EventReceipt?> ReadEventAsync(string instanceId, string eventId, CancellationToken cancellationToken = default) =>
            _runner.ReadEventAsync(instanceId, eventId, cancellationToken: cancellationToken);

        public Task<IReadOnlyList<OpenWait>> GetOpenWaitsAsync(WaitQuery query, CancellationToken cancellationToken = default) =>
            _runner.GetOpenWaitsAsync(query, cancellationToken);

        public Task<WorkflowExecutionResult> RecoverAsync(
            string instanceId,
            CancellationToken cancellationToken = default, Guid? expectedIncarnation = null) =>
            ForInstanceAsync(instanceId, _runner.RecoverAsync(instanceId, cancellationToken, expectedIncarnation),
                cancellationToken: cancellationToken);

        public Task<WorkflowExecutionResult> ReleaseAsync(
            string instanceId,
            string? actor = null,
            CancellationToken cancellationToken = default, Guid? expectedIncarnation = null) =>
            ForInstanceAsync(instanceId, _runner.ReleaseAsync(instanceId, actor, cancellationToken, expectedIncarnation),
                cancellationToken: cancellationToken);

        public Task<WorkflowExecutionResult> CancelAsync(
            string instanceId, CancellationReason reason,
            CancellationToken cancellationToken = default) =>
            ForInstanceAsync(instanceId, _runner.CancelAsync(instanceId, reason, cancellationToken), cancellationToken: cancellationToken);

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
        public Task<WorkflowExecutionResult?> ReadAsync(string instanceId, CancellationToken cancellationToken = default) =>
            _runner.ReadAsync(instanceId, cancellationToken);

        public Task<WorkflowExecutionStatus?> ReadStatusAsync(string instanceId, CancellationToken cancellationToken = default) =>
            _runner.ReadStatusAsync(instanceId, cancellationToken: cancellationToken);

        public Task<WorkflowExecutionResult> RequestDebugAsync(string id, DebugCommandKind kind, string actor, CancellationToken token,
            Guid? expectedIncarnation = null,
            Action<DebugCommandReceipt>? accepted = null) =>
            _runner.RequestDebugAsync(id, kind, actor, token, expectedIncarnation: expectedIncarnation, accepted: accepted);

        public Task<WorkflowExecutionResult> RequestStepAsync(string id, string continuationId, DebugStepKind kind,
            string actor, CancellationToken token, Guid? expectedIncarnation = null, Action<DebugCommandReceipt>? accepted = null) =>
            _runner.RequestDebugAsync(id, DebugCommandKind.Step, actor, token, continuationId, kind, expectedIncarnation, accepted);

        public Task<WorkflowExecutionResult?> ReadSettledDebugCommandResultAsync(DebugCommandReceipt receipt) =>
            _runner.ReadSettledDebugCommandResultAsync(receipt);

        public Task<bool> DebugCommandNeedsRecoveryAsync(DebugCommandReceipt receipt) => _runner.DebugCommandNeedsRecoveryAsync(receipt);

        public Task<WorkflowExecutionResult> RecoverDebugCommandAsync(DebugCommandReceipt receipt, CancellationToken token) =>
            ForInstanceAsync(receipt.InstanceId, _runner.RecoverDebugCommandAsync(receipt, token));

        public Task<DataEditResult> EditDataAsync(string id, string editId, long expectedRevision,
            DataEditTarget target, IReadOnlyList<DataPatch> patches, string actor, CancellationToken token,
            Guid? expectedIncarnation = null,
            Action<DebugCommandReceipt>? accepted = null) =>
            _runner.EditDataAsync(id, editId, expectedRevision, target, patches, actor, token, expectedIncarnation, accepted);

        public Task<IReadOnlyList<Breakpoint>> SetBreakpointsAsync(string id, IReadOnlyList<Breakpoint> breakpoints, string actor,
            CancellationToken token, Guid? expectedIncarnation = null,
            Action<DebugCommandReceipt>? accepted = null) =>
            _runner.SetBreakpointsAsync(id, breakpoints, actor, token, expectedIncarnation, accepted);

        public Task<IReadOnlyList<Breakpoint>> GetBreakpointsAsync(string id, CancellationToken token) =>
            _runner.GetBreakpointsAsync(id, token);

        public Task<WorkflowExecutionResult> ResolveAsync(
            string instanceId, string invocationId, ActivityResolution resolution,
            CancellationToken cancellationToken = default, Guid? expectedIncarnation = null) =>
            ForInstanceAsync(instanceId, _runner.ResolveAsync(instanceId, invocationId, resolution, cancellationToken, expectedIncarnation),
                cancellationToken: cancellationToken);

        public Task<IReadOnlyList<ExecutionTraceEntry>> GetTraceAsync(string instanceId, CancellationToken cancellationToken = default) =>
            _runner.GetTraceAsync(instanceId, cancellationToken: cancellationToken);

        public Task<InstanceInspection?> InspectAsync(string id, CancellationToken token) => _runner.InspectAsync(id, token);

        public Task<bool?> ReplySubjectMatchesAsync(string id, string waitId, string? subject, CancellationToken token) =>
            _runner.ReplySubjectMatchesAsync(id, waitId, subject, token);


        public Task<IReadOnlyList<StepTableEntry>> GetStepTableAsync(string id, CancellationToken token) =>
            _runner.GetStepTableAsync(id, token);

        public Task<IReadOnlyList<SwitchTableEntry>> GetSwitchTableAsync(string id, CancellationToken token) =>
            _runner.GetSwitchTableAsync(id, token);

        public Task<IReadOnlyList<GraphTableEntry>> GetGraphTableAsync(string id, CancellationToken token) =>
            _runner.GetGraphTableAsync(id, token);

        public Task<TraceReplay> OpenReplayAsync(string id, int maxEntries, CancellationToken token) =>
            _runner.OpenReplayAsync(id, maxEntries, token);

        public Task<string?> GetSourceAsync(string id, string path, CancellationToken token) => _runner.GetSourceAsync(id, path, token);

        public Task<IReadOnlyList<StoredTraceEntry>> GetTraceAsync(string id, long afterSequence, int limit,
            CancellationToken token) => _runner.GetTracePageAsync(id, afterSequence, limit, token);

        public async Task<WorkflowExecutionSnapshot?> GetSnapshotAsync(string instanceId, CancellationToken cancellationToken = default)
        {
            try
            {
                return await _runner.GetSnapshotAsync(instanceId, cancellationToken);
            }
            catch (WorkflowEngineException error) when (WorkflowEngineException.IsEngineFailure(error))
            {
                error.At(instanceId);

                throw;
            }
        }

        public async Task<IReadOnlyList<PendingActivityInfo>> GetPendingActivitiesAsync(string instanceId,
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await _runner.GetPendingActivitiesAsync(instanceId, cancellationToken);
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
            Task<WorkflowExecutionResult> operation, CancellationToken cancellationToken = default)
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