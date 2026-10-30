using System.Data;
using System.Data.Common;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.DemoActivities;
using IxIFlow.Extensions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "scopes"), Trait("Spec", "F-26")]
public sealed class ScopeDemoActivityTests
{
    [Fact]
    public async Task ConnectionScopeAndRequiredQueryShareOneConnectionAndCloseIt()
    {
        var factory = new Factory();
        var collection = new ServiceCollection();
        collection.AddSingleton<IDemoConnectionFactory>(factory);
        collection.AddIxIFlow().AddActivities(typeof(ConnectionScope).Assembly);
        await using var host = collection.BuildServiceProvider();
        var registry = host.GetRequiredService<IActivityRegistry>();
        var scope = Assert.Single(registry.Activities, a => a.ActivityType == typeof(ConnectionScope));
        Assert.Equal("global::System.Data.Common.DbConnection", scope.Scope!.ValueTypeName);
        var query = Assert.Single(registry.Activities, a => a.ActivityType == typeof(QueryCustomerInScope));
        Assert.Single(query.RequiresScopes);

        var definition = new WorkflowBuilder<Data, object>([], "ConnectionScopeDemo")
            .Scope<ConnectionScope>(s => s.Input(a => a.ConnectionRef).From(c => "customer-db"), body => body
                .Step<QueryCustomerInScope>(s => s.Input(a => a.CustomerId).From(c => "customer-1")
                    .Output(a => a.CustomerJson).To(c => c.WorkflowData.Json)))
            .Build();

        var done = await host.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.True(done.Status == WorkflowExecutionStatus.Success, done.ErrorMessage);
        Assert.Equal("Ada", JsonDocument.Parse(Assert.IsType<Data>(done.WorkflowData).Json).RootElement.GetProperty("name").GetString());
        Assert.Equal(1, factory.Opens);
        Assert.Equal(ConnectionState.Closed, factory.Connection!.State);
    }

    public sealed class Data
    {
        public string Json { get; set; } = "";
    }

    private sealed class Factory : IDemoConnectionFactory
    {
        public int Opens;
        public SqliteConnection? Connection;

        public async Task<DbConnection> OpenAsync(string connectionRef, CancellationToken cancellationToken)
        {
            Assert.Equal("customer-db", connectionRef);
            Opens++;
            Connection = new("Data Source=:memory:");
            await Connection.OpenAsync(cancellationToken);
            await using var command = Connection.CreateCommand();
            command.CommandText = "create table customers(id text, name text); insert into customers values('customer-1', 'Ada')";
            await command.ExecuteNonQueryAsync(cancellationToken);

            return Connection;
        }
    }
}