using IxIFlow.Core.Runtime;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Core;

public sealed partial class WorkflowHost
{
    private readonly Dictionary<DebugCommandReceipt, TaskCompletionSource> _debugWakes = [];

    // Enclose the entire accepted-command operation, including slot entry and scope/lease
    // disposal. No return or exception inside recovery can bypass this result guard.
    internal async Task<WorkflowExecutionResult> WithDebugCommandResultAsync(
        Func<DebugCommandReceipt?> receipt, Func<Task<WorkflowExecutionResult>> operation)
    {
        WorkflowExecutionResult? result = null;
        ExceptionDispatchInfo? failure = null;

        try
        {
            result = await operation();
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            failure = ExceptionDispatchInfo.Capture(error);
        }

        // Cancellation belongs to the attempt. Once its receipt has settled, reading the
        // durable outcome must not inherit that cancellation or the attempt's failed scope.
        if (receipt() is { } accepted &&
            await ReadAsync(client => client.ReadSettledDebugCommandResultAsync(accepted)) is { } saved)
            return saved;

        failure?.Throw();

        return result!;
    }

    internal async Task WakeDebugCommandIfNeededAsync(DebugCommandReceipt receipt)
    {
        try
        {
            if (!await ReadAsync(client => client.DebugCommandNeedsRecoveryAsync(receipt)))
                return;
        }
        catch (Exception error)
        {
            // A failed reconciliation must not discard accepted work or replace the caller's error.
            _inspectionPolicy.Log(_logger, _sensitiveDiagnostics, LogLevel.Error, error,
                "Could not reconcile debug command for {InstanceId}", receipt.InstanceId);
        }

        lock (_lock)
        {
            if (!_accepting || _debugWakes.ContainsKey(receipt))
                return;

            _debugWakes.Add(receipt, NewSignal());
            ThreadPool.UnsafeQueueUserWorkItem(command => _ = ResumeDebugCommandAsync(command), receipt, preferLocal: false);
        }
    }

    private async Task ResumeDebugCommandAsync(DebugCommandReceipt receipt)
    {
        var attempt = 0;

        try
        {
            while (!_draining.IsCancellationRequested)
            {
                WorkflowExecutionResult? result = null;

                try
                {
                    if (!await ReadAsync(client => client.DebugCommandNeedsRecoveryAsync(receipt)))
                        return;

                    result = await WithDebugCommandResultAsync(() => receipt, () => RunAsync(receipt.InstanceId,
                        (client, _, token) => client.RecoverDebugCommandAsync(receipt, token),
                        _draining.Token, recoveryProbe: true));
                }
                catch (OperationCanceledException) when (_draining.IsCancellationRequested || _stopped.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception error)
                {
                    _inspectionPolicy.Log(_logger, _sensitiveDiagnostics, LogLevel.Error, error,
                        "Could not apply debug command for {InstanceId}", receipt.InstanceId);
                }

                try
                {
                    // Reconcile even when only result construction threw after a successful
                    // commit, so that exception does not create a new retry.
                    if (!await ReadAsync(client => client.DebugCommandNeedsRecoveryAsync(receipt)))
                        return;
                }
                catch (Exception error)
                {
                    _inspectionPolicy.Log(_logger, _sensitiveDiagnostics, LogLevel.Error, error,
                        "Could not reconcile debug command for {InstanceId}", receipt.InstanceId);
                }

                // An earlier edit/pause committed and released its lease. The accepted command
                // still needs its own assignment, even though that pass returned Paused.
                if (result?.Status == WorkflowExecutionStatus.Paused)
                {
                    attempt = 0;

                    continue;
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt++, 5)))), _time, _draining.Token);
            }
        }
        catch (OperationCanceledException) when (_draining.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_lock)
                if (_debugWakes.Remove(receipt, out var done))
                    done.TrySetResult();
        }
    }

    internal sealed partial class InstanceOperations
    {
        // A store notification can let another host settle this receipt before the caller
        // enters its slot. Recovery then returns that incarnation's fresh saved outcome.
        public Task<WorkflowExecutionResult> RecoverDebugCommandAsync(DebugCommandReceipt receipt, CancellationToken token) =>
            ContinueAsync(receipt.InstanceId, release: false, token, expectedIncarnation: receipt.Incarnation, debugCommand: receipt);

        public async Task<WorkflowExecutionResult?> ReadSettledDebugCommandResultAsync(DebugCommandReceipt receipt) =>
            (await ReconcileDebugCommandAsync(receipt)).Settled
                ? await ReadDebugCommandResultAsync(receipt, CancellationToken.None)
                : null;

        private async Task<WorkflowExecutionResult> ReadDebugCommandResultAsync(DebugCommandReceipt receipt, CancellationToken token)
        {
            var current = await _store.GetWorkflowInstanceAsync(receipt.InstanceId);
            RequireIncarnation(current, receipt.Incarnation);

            return await ReadResultAsync(current!, token);
        }

        public async Task<bool> DebugCommandNeedsRecoveryAsync(DebugCommandReceipt receipt, WorkflowInstance? instance = null) =>
            (await ReconcileDebugCommandAsync(receipt, instance)).NeedsRecovery;

        private async Task<(bool NeedsRecovery, bool Settled, WorkflowInstance? Instance)> ReconcileDebugCommandAsync(
            DebugCommandReceipt receipt, WorkflowInstance? instance = null, CancellationToken cancellationToken = default)
        {
            instance ??= await _store.GetWorkflowInstanceAsync(receipt.InstanceId, cancellationToken: cancellationToken);

            if (instance == null || instance.Incarnation != receipt.Incarnation || WorkflowStoreRules.IsTerminal(instance.Status))
                return (false, true, instance);

            var checkpoint = WorkflowDebugRules.Checkpoint(instance);

            // The committed prefix already proves this Pause is pending. Leave control delivery
            // to the active owner; the local recovery queues behind its run before reading controls.
            if (receipt.Kind == DebugCommandKind.Pause && instance.Status == WorkflowStatus.Running &&
                checkpoint.AppliedOrder < receipt.Order)
                return (true, false, instance);

            var state = (await _store.GetDebugStatesAsync([receipt.InstanceId])).GetValueOrDefault(receipt.InstanceId);
            var command = state?.Commands.SingleOrDefault(c => c.Id == receipt.CommandId && c.Order == receipt.Order);

            if (command == null)
                return (false, true, instance);

            // The command read can be newer than the instance read. Reconcile against at least
            // its acknowledgement revision before deciding that execution has settled.
            if (command.AcknowledgedRevision > instance.Revision)
            {
                instance = await _store.GetWorkflowInstanceAsync(receipt.InstanceId, cancellationToken: cancellationToken);

                if (instance == null || instance.Incarnation != receipt.Incarnation || WorkflowStoreRules.IsTerminal(instance.Status))
                    return (false, true, instance);

                checkpoint = WorkflowDebugRules.Checkpoint(instance);
            }

            // Continue/Step acknowledge before dispatch. A crash after that commit is still
            // this command's work; a later applied command belongs to a different receipt.
            if (command.AcknowledgedRevision != null &&
                (instance.Status != WorkflowStatus.Running || state!.Commands.Any(c => c.Order > receipt.Order &&
                    c.Order <= checkpoint.AppliedOrder && c.Kind != DebugCommandKind.SetBreakpoints)))
                return (false, true, instance);

            // Applying an edit does not dispatch workflow code or evaluate breakpoint conditions.
            // This also lets a later resume recover an earlier edit before its execution is held.
            if (state!.Commands.FirstOrDefault(c => c.AcknowledgedRevision == null) is
                    { Kind: DebugCommandKind.EditData } edit && edit.Order <= receipt.Order)
                return ((hostSettings ?? HostSettings.Default).Debugging, false, instance);

            var pendingResume = state!.Commands.Any(c => c.AcknowledgedRevision == null &&
                c.Kind is DebugCommandKind.Continue or DebugCommandKind.Step);

            if ((checkpoint.Stepping != null || pendingResume) &&
                (!(hostSettings ?? HostSettings.Default).Debugging ||
                    (checkpoint.Breakpoints.Any(b => b.Condition != null) || state.Commands.Any(c =>
                        c.AcknowledgedRevision == null && c.Breakpoints?.Any(b => b.Condition != null) == true)) &&
                    services.GetService(typeof(IBreakpointConditionCompiler)) == null))
                return (false, false, instance);

            return (true, false, instance);
        }
    }
}