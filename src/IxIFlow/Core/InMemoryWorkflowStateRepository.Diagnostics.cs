namespace IxIFlow.Core;

public partial class InMemoryWorkflowStateRepository
{
    private readonly Dictionary<string, Dictionary<string, WorkflowDiagnostic>> _diagnostics = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<WorkflowDiagnostic>> GetDiagnosticsAsync(string instanceId)
    {
        lock (_claimLock)
            return Task.FromResult<IReadOnlyList<WorkflowDiagnostic>>(
                _diagnostics.TryGetValue(instanceId, out var rows)
                    ? rows.Values.OrderBy(item => item.DiagnosticId, StringComparer.Ordinal).ToArray() : []);
    }
}
