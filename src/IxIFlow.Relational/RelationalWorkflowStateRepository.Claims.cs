using Dapper;
using IxIFlow.Core;

namespace IxIFlow.Relational;

public abstract partial class RelationalWorkflowStateRepository
{
    internal abstract string RunnableSql { get; }
    internal abstract string ClaimEligibilitySql { get; }
    internal abstract string SetLeaseHostSql { get; }
    internal abstract string StampHostSql { get; }
    internal virtual string ClaimStampHostSql => StampHostSql;
    internal DynamicParameters WithExclusions(DynamicParameters parameters, ClaimQuery query)
    {
        parameters.Add("ExcludedInstanceIds", System.Text.Json.JsonSerializer.Serialize(query.ExcludedInstanceIds));
        return parameters;
    }
    internal abstract DynamicParameters PlacementParameters(ClaimQuery query, DateTime now, int limit);
}
