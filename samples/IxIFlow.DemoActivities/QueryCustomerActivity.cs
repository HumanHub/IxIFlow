using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using System.Text.Json;
using IxIFlow.ActivitySdk;
using IxIFlow.Core;

namespace IxIFlow.DemoActivities;

[WorkflowActivity("demo.postgres.query-customer", Name = "Query customer", Category = "Database", Icon = "database", Designer = "query-customer")]
public sealed class QueryCustomerActivity(IDemoConnectionFactory factory) : IAsyncActivity
{
    [ActivityInput, UIHint("connection"), Required, DefaultValue("customer-db"), Display(Name = "Connection", Description = "A named connection. Credentials stay outside the workflow.")]
    public string ConnectionRef { get; set; } = string.Empty;

    [ActivityInput, UIHint("code"), Required, DefaultValue("select id, name from customers where id = @customerId"), Display(Name = "SQL query")]
    public string Sql { get; set; } = string.Empty;

    [ActivityInput, UIHint("binding"), Required, Display(Name = "Customer ID parameter")]
    public string CustomerId { get; set; } = string.Empty;

    [ActivityOutput]
    public string CustomerJson { get; private set; } = string.Empty;

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenAsync(ConnectionRef, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = Sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@customerId";
        parameter.Value = CustomerId;
        command.Parameters.Add(parameter);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            CustomerJson = "null";
            return;
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var index = 0; index < reader.FieldCount; index++)
            result[reader.GetName(index)] = await reader.IsDBNullAsync(index, cancellationToken)
                ? null
                : reader.GetValue(index);
        CustomerJson = JsonSerializer.Serialize(result);
    }
}
