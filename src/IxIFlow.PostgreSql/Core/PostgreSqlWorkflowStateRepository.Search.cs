using Dapper;
namespace IxIFlow.Core;

public sealed partial class PostgreSqlWorkflowStateRepository
{
    internal override string InstanceTable => "ixiflow_workflow_instances";
    internal override string SearchLimitPrefix => "";
    internal override string SearchLimitSuffix => "LIMIT @Limit";
    internal override string SearchColumn(string name) => name;
    internal override string SearchValues(DynamicParameters parameters, string name, string[] values)
    {
        parameters.Add(name, values);
        return $"= ANY(@{name})";
    }
}
