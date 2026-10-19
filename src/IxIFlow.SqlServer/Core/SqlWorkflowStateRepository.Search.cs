using System.Data;
using Dapper;
namespace IxIFlow.Core;

public sealed partial class SqlWorkflowStateRepository
{
    internal override string InstanceTable => "dbo.IxIFlowWorkflowInstances";
    internal override string SearchLimitPrefix => "TOP (@Limit)";
    internal override string SearchLimitSuffix => "";
    internal override string SearchColumn(string name) => string.Concat(name.Split('_').Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    internal override string SearchValues(DynamicParameters parameters, string name, string[] values)
    {
        var table = new DataTable();
        table.Columns.Add("Value", typeof(string));
        foreach (var value in values) table.Rows.Add(value);
        parameters.Add(name, table.AsTableValuedParameter("dbo.IxIFlowSearchValues"));
        return $"IN (SELECT Value FROM @{name})";
    }
}
