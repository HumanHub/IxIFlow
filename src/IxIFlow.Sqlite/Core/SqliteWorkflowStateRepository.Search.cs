using Dapper;
namespace IxIFlow.Core;

public sealed partial class SqliteWorkflowStateRepository
{
    internal override string DeliveryTable => "ixiflow_outbox_deliveries";
    internal override string OutboxEventTable => "ixiflow_outbox_events";
    internal override string InstanceTable => "ixiflow_workflow_instances";
    internal override string SearchLimitPrefix => "";
    internal override string SearchLimitSuffix => "LIMIT @Limit";
    internal override string SearchColumn(string name) => name;
    internal override string SearchValues(DynamicParameters parameters, string name, string[] values)
    {
        parameters.Add(name, values);
        return $"IN @{name}";
    }
}
