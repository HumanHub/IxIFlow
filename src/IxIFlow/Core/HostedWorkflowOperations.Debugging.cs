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
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var (definition, data) = await host.ReadAsync(client => client.PrepareDebugLaunchAsync(workflow, version, input));
        return await client.StartAsync(definition, data, new WorkflowOptions
        { InstanceId = instanceId, DebugStopOnEntryActor = stopOnEntry ? actor : null }, cancellationToken);
    }
    public Task<InstanceInspection?> InspectAsync(string instanceId, CancellationToken cancellationToken = default) =>
        host.ReadAsync(client => client.InspectAsync(instanceId, cancellationToken));
    public async Task<WorkflowExecutionResult> PauseAsync(string instanceId, string actor, CancellationToken cancellationToken = default)
    {
        var result = await host.ReadAsync(client => client.RequestDebugAsync(instanceId, DebugCommandKind.Pause, actor, cancellationToken));
        if (result.Status != WorkflowExecutionStatus.Paused) host.WakeNow(instanceId);
        return result;
    }

    public async Task<WorkflowExecutionResult> ContinueAsync(string instanceId, string actor, CancellationToken cancellationToken = default)
    {
        await host.ReadAsync(client => client.RequestDebugAsync(instanceId, DebugCommandKind.Continue, actor, cancellationToken));
        return await client.RecoverAsync(instanceId, cancellationToken);
    }

    public async Task RequestContinueAsync(string instanceId, string actor, CancellationToken cancellationToken = default)
    {
        await host.ReadAsync(client => client.RequestDebugAsync(instanceId, DebugCommandKind.Continue, actor, cancellationToken));
        host.WakeNow(instanceId);
    }

    public async Task<WorkflowExecutionResult> StepAsync(string instanceId, string continuationId, DebugStepKind kind,
        string actor, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(continuationId);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        await host.ReadAsync(client => client.RequestStepAsync(instanceId, continuationId, kind, actor, cancellationToken));
        return await client.RecoverAsync(instanceId, cancellationToken);
    }

    public Task<DataEditResult> EditDataAsync(string instanceId, string editId, long expectedRevision,
        DataEditTarget target, IReadOnlyList<DataPatch> patches, string actor, CancellationToken cancellationToken = default) =>
        host.ReadAsync(client => client.EditDataAsync(instanceId, editId, expectedRevision, target, patches, actor, cancellationToken));

    public async Task<IReadOnlyList<Breakpoint>> SetBreakpointsAsync(string instanceId, IReadOnlyList<Breakpoint> breakpoints,
        string actor, CancellationToken cancellationToken = default)
    {
        var resolved = await host.ReadAsync(client => client.SetBreakpointsAsync(instanceId, breakpoints, actor, cancellationToken));
        host.WakeNow(instanceId);
        return resolved;
    }

    public Task<IReadOnlyList<Breakpoint>> GetBreakpointsAsync(string instanceId, CancellationToken cancellationToken = default) =>
        host.ReadAsync(client => client.GetBreakpointsAsync(instanceId, cancellationToken));

}
