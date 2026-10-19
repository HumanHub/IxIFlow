using Dapper;
using IxIFlow.Core;

namespace IxIFlow.Relational;

public abstract partial class RelationalWorkflowStateRepository
{
    public async Task<InstancePage> QueryInstancesAsync(InstanceQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var cursor = query.Validate();
        var parameters = new DynamicParameters(new { Limit = query.Limit == int.MaxValue ? int.MaxValue : query.Limit + 1 });
        var filters = new List<string>();
        void Filter(string column, string parameter, object? value, string comparison = "=")
        {
            if (value == null) return;
            filters.Add($"{SearchColumn(column)} {comparison} @{parameter}");
            parameters.Add(parameter, value, column is "created_at_utc" or "ended_at_utc" ? TimeDbType : null);
        }
        Filter("instance_id", "InstanceId", query.InstanceId);
        Filter("workflow_name", "WorkflowName", query.WorkflowName);
        Filter("workflow_version", "WorkflowVersion", query.WorkflowVersion);
        Filter("stop_kind", "StopKind", query.StopKind?.ToString());
        Filter("correlation_id", "CorrelationId", query.CorrelationId);
        Filter("parent_instance_id", "ParentInstanceId", query.ParentInstanceId);
        Filter("created_at_utc", "CreatedFrom", At(query.CreatedFrom), ">=");
        Filter("created_at_utc", "CreatedTo", At(query.CreatedTo), "<");
        Filter("ended_at_utc", "EndedFrom", At(query.EndedFrom), ">=");
        Filter("ended_at_utc", "EndedTo", At(query.EndedTo), "<");
        if (query.Statuses is { } statuses)
            filters.Add(SearchColumn("status") + " " + SearchValues(parameters, "Statuses", statuses.Distinct().Select(status => status.ToString()).ToArray()));
        if (cursor is { } after)
        {
            parameters.Add("CursorCreated", At(after.Created), TimeDbType);
            parameters.Add("CursorId", after.Id);
            filters.Add($"{SearchColumn("created_at_utc")} <= @CursorCreated");
            filters.Add($"({SearchColumn("created_at_utc")} < @CursorCreated OR ({SearchColumn("created_at_utc")} = @CursorCreated AND {SearchColumn("instance_id")} > @CursorId))");
        }
        string[] columns = ["instance_id", "workflow_name", "workflow_version", "status", "stop_kind", "stop_crash_count",
            "correlation_id", "parent_instance_id", "created_at_utc", "ended_at_utc", "runnable_at_utc", "revision", "incarnation", "parent_wait_id"];
        string[] aliases = ["InstanceId", "WorkflowName", "WorkflowVersion", "Status", "StopKind", "CrashCount", "CorrelationId",
            "ParentInstanceId", "CreatedAtUtc", "EndedAtUtc", "RunnableAtUtc", "Revision", "Incarnation", "ParentWaitId"];
        var projection = string.Join(", ", columns.Select((column, index) => $"{SearchColumn(column)} AS {aliases[index]}"));
        // Predicates are assembled only from internal column/operator names; all caller values are parameters.
        var sql = $"SELECT {SearchLimitPrefix} {projection} FROM {InstanceTable} " +
            (filters.Count == 0 ? "" : "WHERE " + string.Join(" AND ", filters)) +
            $" ORDER BY {SearchColumn("created_at_utc")} DESC, {SearchColumn("instance_id")} ASC " +
            SearchLimitSuffix;
        await EnsureSchemaAsync();
        await using var connection = await OpenConnectionAsync();
        var rows = await connection.QueryAsync<SearchRow>(sql, parameters);
        return query.Page(rows.Select(row => row.Read()).ToArray());
    }

    private sealed class SearchRow
    {
        public string InstanceId { get; set; } = "";
        public string WorkflowName { get; set; } = "";
        public int WorkflowVersion { get; set; }
        public WorkflowStatus Status { get; set; }
        public WorkflowStopKind? StopKind { get; set; }
        public int? CrashCount { get; set; }
        public string? CorrelationId { get; set; }
        public string? ParentInstanceId { get; set; }
        public string? ParentWaitId { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime? EndedAtUtc { get; set; }
        public DateTime? RunnableAtUtc { get; set; }
        public long Revision { get; set; }
        public Guid Incarnation { get; set; }
        public InstanceSummary Read() => new(InstanceId, WorkflowName, WorkflowVersion, Status, StopKind, CrashCount,
            CorrelationId, ParentInstanceId, Utc(CreatedAtUtc), EndedAtUtc is { } end ? Utc(end) : null,
            RunnableAtUtc is { } run ? Utc(run) : null, Revision, Incarnation) { ParentWaitId = ParentWaitId };
    }
}
