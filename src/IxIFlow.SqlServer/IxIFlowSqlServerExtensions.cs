using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IxIFlow.Extensions;

/// <summary>Chooses SQL Server as the store (D-21).</summary>
public static class IxIFlowSqlServerExtensions
{
    /// <summary>
    /// Keeps instances, their inbox, waits, trace and leases in the SQL Server database
    /// <paramref name="connectionString"/> names. The schema is created on first use.
    /// </summary>
    public static IIxIFlowBuilder UseSqlServer(this IIxIFlowBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        builder.Services.Replace(ServiceDescriptor.Singleton<IWorkflowStateRepository>(
            new SqlWorkflowStateRepository(connectionString)));
        return builder;
    }
}
