namespace IxIFlow.Core;

public partial class InMemoryWorkflowStateRepository
{
    public Task<InstancePage> QueryInstancesAsync(InstanceQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var cursor = query.Validate();
        lock (_claimLock)
        {
            var rows = _summaries.Values.Where(row => _instances.ContainsKey(row.InstanceId))
                .Select(row => row with { RunnableAtUtc = _instances[row.InstanceId].RunnableAtUtc })
                .Where(row =>
                    (query.InstanceId == null || row.InstanceId == query.InstanceId) &&
                    (query.Statuses == null || query.Statuses.Contains(row.Status)) &&
                    (query.StopKind == null || row.StopKind == query.StopKind) &&
                    (query.WorkflowName == null || row.WorkflowName == query.WorkflowName) &&
                    (query.WorkflowVersion == null || row.WorkflowVersion == query.WorkflowVersion) &&
                    (query.CorrelationId == null || row.CorrelationId == query.CorrelationId) &&
                    (query.ParentInstanceId == null || row.ParentInstanceId == query.ParentInstanceId) &&
                    (query.CreatedFrom == null || row.CreatedAtUtc >= query.CreatedFrom) &&
                    (query.CreatedTo == null || row.CreatedAtUtc < query.CreatedTo) &&
                    (query.EndedFrom == null || row.EndedAtUtc >= query.EndedFrom) &&
                    (query.EndedTo == null || row.EndedAtUtc < query.EndedTo) &&
                    (cursor == null || row.CreatedAtUtc < cursor.Value.Created ||
                        row.CreatedAtUtc == cursor.Value.Created && string.CompareOrdinal(row.InstanceId, cursor.Value.Id) > 0))
                .OrderByDescending(row => row.CreatedAtUtc).ThenBy(row => row.InstanceId, StringComparer.Ordinal)
                .Take(query.Limit == int.MaxValue ? int.MaxValue : query.Limit + 1).ToArray();
            return Task.FromResult(query.Page(rows));
        }
    }
}
