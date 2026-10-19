namespace IxIFlow.Core;

public partial class InMemoryWorkflowStateRepository
{
    public async Task<WorkflowControlRequests> GetControlRequestsAsync(IReadOnlyCollection<string> instanceIds)
    {
        var cancellations = GetCancellationRequestsAsync(instanceIds);
        var debug = GetDebugStatesAsync(instanceIds);
        await Task.WhenAll(cancellations, debug);
        return new(await cancellations, await debug);
    }
    private readonly Dictionary<string, List<DebugCommand>> _debugCommands = new(StringComparer.Ordinal);
    private IReadOnlyList<DebugCommand> CommandsOf(string id) => _debugCommands.TryGetValue(id, out var commands) ? commands : [];
    public Task<DebugCommandResult> RequestDebugCommandAsync(string instanceId, DebugCommandRequest command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentNullException.ThrowIfNull(command);
        lock (_claimLock)
        {
            _instances.TryGetValue(instanceId, out var instance);
            var commands = CommandsOf(instanceId);
            var result = WorkflowDebugRules.Validate(instance, commands, command);
            if (result.Status == DebugCommandStatus.Accepted)
            {
                _debugCommands[instanceId] = commands.Append(result.Command!).ToList();
                if (WorkflowDebugRules.NeedsWake(instance!.Status, _debugCommands[instanceId])) instance.RunnableAtUtc = Now;
            }
            return Task.FromResult(result);
        }
    }
    public virtual Task<IReadOnlyDictionary<string, WorkflowDebugState>> GetDebugStatesAsync(IReadOnlyCollection<string> instanceIds)
    {
        ArgumentNullException.ThrowIfNull(instanceIds);
        lock (_claimLock)
        {
            var result = new Dictionary<string, WorkflowDebugState>(StringComparer.Ordinal);
            foreach (var id in instanceIds.Distinct(StringComparer.Ordinal))
                if (_instances.TryGetValue(id, out var instance))
                {
                    var commands = CommandsOf(id);
                    result[id] = new(WorkflowDebugRules.Version(commands),
                        WorkflowDebugRules.Checkpoint(instance).PauseGeneration, commands.ToArray());
                }
            return Task.FromResult<IReadOnlyDictionary<string, WorkflowDebugState>>(result);
        }
    }
}
