using System.Data.Common;
using System.Reflection;
using Dapper;
using IxIFlow.Core;

namespace IxIFlow.Tests.Infrastructure;

internal static class PostgreSqlPeriodicPlan
{
    // Use the provider's transaction setup, so EXPLAIN sees the shipping SET LOCAL policy.
    internal static async Task<DbTransaction> BeginAsync(IWorkflowStateRepository store, DbConnection connection,
        string operation)
    {
        var method = store.GetType().GetMethod("BeginTransactionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var kind = Enum.Parse(method.GetParameters()[1].ParameterType, operation);
        var transaction = (await (ValueTask<DbTransaction?>)method.Invoke(store, [connection, kind])!)!;
        Assert.Equal("off", await connection.ExecuteScalarAsync<string>("SHOW jit", transaction: transaction));
        return transaction;
    }
}
