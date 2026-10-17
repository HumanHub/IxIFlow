using IxIFlow.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IxIFlow.Extensions;

/// <summary>Chooses PostgreSQL as the store (D-21).</summary>
public static class IxIFlowPostgreSqlExtensions
{
    /// <summary>
    /// Keeps instances, their inbox, waits, trace and leases in the PostgreSQL database
    /// <paramref name="connectionString"/> names, and wakes the host through <c>LISTEN</c>/<c>NOTIFY</c> when an
    /// instance becomes runnable (C-13). The schema is created on first use.
    /// </summary>
    public static IIxIFlowBuilder UsePostgreSql(this IIxIFlowBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        builder.ChooseStore("PostgreSQL");
        var store = new PostgreSqlWorkflowStateRepository(connectionString);
        builder.Services.Replace(ServiceDescriptor.Singleton<IWorkflowStateRepository>(store));
        builder.Services.Replace(ServiceDescriptor.Singleton<IRunnableWakeSource>(store));
        return builder;
    }
}
