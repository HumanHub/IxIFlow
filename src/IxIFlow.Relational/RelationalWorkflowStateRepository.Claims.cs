using Dapper;
using IxIFlow.Core;

namespace IxIFlow.Relational;

public abstract partial class RelationalWorkflowStateRepository
{
    internal abstract string RunnableSql { get; }
    internal abstract string ClaimEligibilitySql { get; }
    internal abstract string SetLeaseHostSql { get; }
    internal abstract string StampHostSql { get; }
    internal abstract DynamicParameters PlacementParameters(ClaimQuery query, DateTime now, int limit);
}
