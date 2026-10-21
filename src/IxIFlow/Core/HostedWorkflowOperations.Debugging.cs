namespace IxIFlow.Core;

internal sealed partial class HostedWorkflowOperations
{
    public IDisposable WatchChanges(string instanceId, Action changed) => host.WatchChanges(instanceId, changed);
    public Task<IReadOnlyList<StepTableEntry>> GetStepTableAsync(string instanceId, CancellationToken cancellationToken = default) =>
        host.ReadAsync(client => client.GetStepTableAsync(instanceId, cancellationToken));
    public Task<TraceReplay> OpenReplayAsync(string instanceId, int maxEntries = 10000, CancellationToken cancellationToken = default) =>
        host.ReadAsync(client => client.OpenReplayAsync(instanceId, maxEntries, cancellationToken));
    public Task<string?> GetSourceAsync(string instanceId, string path, CancellationToken cancellationToken = default) =>
        host.ReadAsync(client => client.GetSourceAsync(instanceId, path, cancellationToken));
    public async Task<string> LaunchAsync(string instanceId, string workflow, int version, System.Text.Json.JsonElement input,
        bool stopOnEntry, string actor, CancellationToken cancellationToken = default)
    {
        InstanceIds.Validate(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if (!(hostSettings ?? Runtime.HostSettings.Default).Debugging) throw new InvalidOperationException("Debugging is disabled");
        return await client.StartAsync(workflow, version, (object)input.Clone(), new WorkflowOptions
        { InstanceId = instanceId, DebugStopOnEntryActor = stopOnEntry ? actor : null }, cancellationToken);
    }
    public Task<InstanceInspection?> InspectAsync(string instanceId, CancellationToken cancellationToken = default) =>
        host.ReadAsync(client => client.InspectAsync(instanceId, cancellationToken));
    public async Task<WorkflowExecutionResult> PauseAsync(string instanceId, string actor, CancellationToken cancellationToken = default, Guid? expectedIncarnation = null)
    {
        var incarnation = await IncarnationAsync(instanceId, expectedIncarnation);
        return await WithDebugReceiptAsync(accepted => host.ReadAsync(client =>
            client.RequestDebugAsync(instanceId, DebugCommandKind.Pause, actor, cancellationToken, incarnation, accepted)));
    }

    public async Task<WorkflowExecutionResult> ContinueAsync(string instanceId, string actor, CancellationToken cancellationToken = default, Guid? expectedIncarnation = null)
    {
        var incarnation = await IncarnationAsync(instanceId, expectedIncarnation);
        return await WithDebugReceiptAsync(async accepted =>
        {
            DebugCommandReceipt? receipt = null;
            await host.ReadAsync(client => client.RequestDebugAsync(instanceId, DebugCommandKind.Continue, actor,
                cancellationToken, incarnation, value => { receipt = value; accepted(value); }));
            return await host.RunAsync(instanceId, (slot, _, token) => slot.RecoverDebugCommandAsync(receipt!, token), cancellationToken);
        });
    }

    public async Task RequestContinueAsync(string instanceId, string actor, CancellationToken cancellationToken = default, Guid? expectedIncarnation = null)
    {
        var incarnation = await IncarnationAsync(instanceId, expectedIncarnation);
        await WithDebugReceiptAsync(accepted => host.ReadAsync(client =>
            client.RequestDebugAsync(instanceId, DebugCommandKind.Continue, actor, cancellationToken, incarnation, accepted)));
    }

    public async Task<WorkflowExecutionResult> StepAsync(string instanceId, string continuationId, DebugStepKind kind,
        string actor, CancellationToken cancellationToken = default, Guid? expectedIncarnation = null)
    {
        var incarnation = await IncarnationAsync(instanceId, expectedIncarnation);
        ArgumentException.ThrowIfNullOrWhiteSpace(continuationId);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        return await WithDebugReceiptAsync(async accepted =>
        {
            DebugCommandReceipt? receipt = null;
            await host.ReadAsync(client => client.RequestStepAsync(instanceId, continuationId, kind, actor,
                cancellationToken, incarnation, value => { receipt = value; accepted(value); }));
            return await host.RunAsync(instanceId, (slot, _, token) => slot.RecoverDebugCommandAsync(receipt!, token), cancellationToken);
        });
    }

    public async Task<DataEditResult> EditDataAsync(string instanceId, string editId, long expectedRevision,
        DataEditTarget target, IReadOnlyList<DataPatch> patches, string actor, CancellationToken cancellationToken = default, Guid? expectedIncarnation = null)
    {
        var incarnation = await IncarnationAsync(instanceId, expectedIncarnation);
        return await WithDebugReceiptAsync(accepted => host.ReadAsync(client =>
            client.EditDataAsync(instanceId, editId, expectedRevision, target, patches, actor, cancellationToken, incarnation, accepted)));
    }

    public async Task<IReadOnlyList<Breakpoint>> SetBreakpointsAsync(string instanceId, IReadOnlyList<Breakpoint> breakpoints,
        string actor, CancellationToken cancellationToken = default, Guid? expectedIncarnation = null)
    {
        var incarnation = await IncarnationAsync(instanceId, expectedIncarnation);
        return await WithDebugReceiptAsync(accepted => host.ReadAsync(client =>
            client.SetBreakpointsAsync(instanceId, breakpoints, actor, cancellationToken, incarnation, accepted)));
    }

    private async Task<T> WithDebugReceiptAsync<T>(Func<Action<DebugCommandReceipt>, Task<T>> operation)
    {
        DebugCommandReceipt? receipt = null;
        try { return await operation(accepted => receipt = accepted); }
        finally
        {
            // Acceptance is recorded before result hydration, gate entry, bundle acquisition,
            // or any other cancellable/fallible post-acceptance work.
            if (receipt != null) await host.WakeDebugCommandIfNeededAsync(receipt);
        }
    }

    public Task<IReadOnlyList<Breakpoint>> GetBreakpointsAsync(string instanceId, CancellationToken cancellationToken = default) =>
        host.ReadAsync(client => client.GetBreakpointsAsync(instanceId, cancellationToken));

}
