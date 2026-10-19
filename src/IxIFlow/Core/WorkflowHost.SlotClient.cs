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
        private readonly InstanceOperations _runner;

        public SlotClient(InstanceOperations runner)
        {
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        }

        public Task<WorkflowExecutionResult> RunAsync<TWorkflowData>(
            WorkflowDefinition definition,
            TWorkflowData workflowData,
            WorkflowOptions? options = null,
            CancellationToken cancellationToken = default)
            where TWorkflowData : class
        {
            ArgumentNullException.ThrowIfNull(definition);
            ArgumentNullException.ThrowIfNull(workflowData);
            return _runner.StartAsync(definition, workflowData, options ?? new WorkflowOptions(),
                cancellationToken);
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
            CancellationToken cancellationToken = default) =>
            ForInstanceAsync(instanceId, _runner.RecoverAsync(instanceId, cancellationToken));

        public Task<WorkflowExecutionResult> ReleaseAsync(
            string instanceId,
            string? actor = null,
            CancellationToken cancellationToken = default) =>
            ForInstanceAsync(instanceId, _runner.ReleaseAsync(instanceId, actor, cancellationToken));

        public Task<WorkflowExecutionResult> CancelAsync(
            string instanceId, CancellationReason reason,
            CancellationToken cancellationToken = default) =>
            ForInstanceAsync(instanceId, _runner.CancelAsync(instanceId, reason, cancellationToken));

        /// <summary>
        /// Saves a cancellation request. Returns the result when nothing has to run, or null when
        /// the instance is idle and must be recovered to unwind.
        /// </summary>
        public async Task<WorkflowExecutionResult?> RequestCancellationAsync(string instanceId,
            CancellationReason reason, CancellationToken cancellationToken = default)
        {
            try
            {
                return await _runner.RequestCancellationAsync(instanceId, reason, cancellationToken);
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

        public Task<WorkflowExecutionResult> RequestDebugAsync(string id, DebugCommandKind kind, string actor, CancellationToken token) =>
            _runner.RequestDebugAsync(id, kind, actor, token);

        public Task<WorkflowExecutionResult> RequestStepAsync(string id, string continuationId, DebugStepKind kind,
            string actor, CancellationToken token) =>
            _runner.RequestDebugAsync(id, DebugCommandKind.Step, actor, token, continuationId, kind);

        public Task<IReadOnlyList<Breakpoint>> SetBreakpointsAsync(string id, IReadOnlyList<Breakpoint> breakpoints, string actor, CancellationToken token) =>
            _runner.SetBreakpointsAsync(id, breakpoints, actor, token);
        public Task<IReadOnlyList<Breakpoint>> GetBreakpointsAsync(string id, CancellationToken token) =>
            _runner.GetBreakpointsAsync(id, token);

        public Task<WorkflowExecutionResult> ResolveAsync(
            string instanceId, string invocationId, ActivityResolution resolution,
            CancellationToken cancellationToken = default) =>
            ForInstanceAsync(instanceId, _runner.ResolveAsync(instanceId, invocationId, resolution, cancellationToken));

        public Task<IReadOnlyList<ExecutionTraceEntry>> GetTraceAsync(string instanceId) =>
            _runner.GetTraceAsync(instanceId);

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
