using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using System.Text.Json;
using IxIFlow.ActivitySdk;
using IxIFlow.Core;

namespace IxIFlow.DemoActivities;

[WorkflowActivity("demo.connection", Name = "Connection", Category = "Database", Icon = "database")]
public sealed class ConnectionScope(IDemoConnectionFactory factory) : IScopeActivity<DbConnection>
{
    [ActivityInput, Required, UIHint("connection"), DefaultValue("customer-db")]
    public string ConnectionRef { get; set; } = "";

    public Task<DbConnection> OpenAsync(ScopeOpening opening, IScopeContext context, CancellationToken cancellationToken = default) =>
        factory.OpenAsync(ConnectionRef, cancellationToken);

    public async Task CloseAsync(DbConnection value, ScopeClosing closing, IScopeContext context,
        CancellationToken cancellationToken = default) =>
        await value.DisposeAsync();
}

[WorkflowActivity("demo.query-customer-in-scope", Name = "Query customer in scope", Category = "Database", Icon = "database")]
[RequiresScope<DbConnection>]
public sealed class QueryCustomerInScope : IAsyncActivity
{
    [ActivityInput, Required] public string CustomerId { get; set; } = "";
    [ActivityOutput] public string CustomerJson { get; private set; } = "";

    public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        await using var command = context.Scope<DbConnection>().CreateCommand();
        command.CommandText = "select id, name from customers where id = @customerId";
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
            result[reader.GetName(index)] = await reader.IsDBNullAsync(index, cancellationToken) ? null : reader.GetValue(index);

        CustomerJson = JsonSerializer.Serialize(result);
    }
}