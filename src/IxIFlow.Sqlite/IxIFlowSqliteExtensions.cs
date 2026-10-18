using IxIFlow.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IxIFlow.Extensions;

/// <summary>Chooses SQLite as the single-host store (D-21).</summary>
public static class IxIFlowSqliteExtensions
{
    /// <summary>
    /// Keeps instances, inbox, waits, trace and leases in a WAL database, created on first use.
    /// The host holds an exclusive .ixiflow-owner file handle until disposed; a crash releases it.
    /// The repository enforces ownership on direct construction too. Coordinators are not supported on SQLite.
    /// </summary>
    public static IIxIFlowBuilder UseSqlite(this IIxIFlowBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        builder.ChooseStore("SQLite");
        connectionString = SqliteWorkflowStateRepository.FileConnectionString(connectionString);
        builder.Services.Replace(ServiceDescriptor.Singleton<IWorkflowStateRepository>(provider =>
            new SqliteWorkflowStateRepository(connectionString, provider.GetRequiredService<TimeProvider>())));
        return builder;
    }
}
