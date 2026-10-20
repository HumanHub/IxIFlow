namespace IxIFlow.Core;

public partial class InMemoryWorkflowStateRepository
{
    private readonly Dictionary<string, Dictionary<string, WorkflowDiagnostic>> _diagnostics = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<WorkflowDiagnostic>> GetDiagnosticsForIncarnationAsync(string instanceId, Guid incarnation)
    {
        lock (_claimLock)
            return Task.FromResult<IReadOnlyList<WorkflowDiagnostic>>(
                _instances.TryGetValue(instanceId, out var instance) && instance.Incarnation == incarnation &&
                _diagnostics.TryGetValue(instanceId, out var rows)
                    ? rows.Values.OrderBy(item => item.DiagnosticId, StringComparer.Ordinal).ToArray() : []);
    }
}
