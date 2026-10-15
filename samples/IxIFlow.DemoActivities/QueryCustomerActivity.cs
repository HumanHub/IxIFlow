using System.Data.Common;
using System.Text.Json;
using IxIFlow.ActivitySdk;
using IxIFlow.Core;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.DemoActivities;

[WorkflowActivity("demo.postgres.query-customer", Name = "Query customer", Category = "Database", Icon = "database", Designer = "query-customer")]
public sealed class QueryCustomerActivity : IAsyncActivity
{
    [WorkflowInput(Label = "Connection", Control = "connection", Required = true, Help = "A named connection. Credentials stay outside the workflow.", Default = "customer-db")]
    public string ConnectionRef { get; set; } = string.Empty;

    [WorkflowInput(Label = "SQL query", Control = "code", Required = true, Default = "select id, name from customers where id = @customerId")]
    public string Sql { get; set; } = string.Empty;

    [WorkflowInput(Label = "Customer ID parameter", Control = "binding", Required = true)]
    public string CustomerId { get; set; } = string.Empty;

    [WorkflowOutput]
    public string CustomerJson { get; private set; } = string.Empty;

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        var factory = context.Services.GetRequiredService<IDemoConnectionFactory>();
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
