using IxIFlow.Core;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Extensions;

public static class SqlWorkflowHostExtensions
{
    /// <summary>
    /// Adds distributed host services with a SQL Server message bus and host registry.
    /// Workflow instance state still uses the configured IWorkflowStateRepository.
    /// </summary>
    public static IServiceCollection AddIxIFlowHost(
        this IServiceCollection services,
        Action<WorkflowHostOptions> configure,
        string connectionString)
    {
        services.AddIxIFlowHost(options =>
        {
            configure(options);
            options.MessageBusConnectionString = connectionString;
        });

        services.AddSingleton<IHostRegistry>(_ => new SqlHostRegistry(connectionString));
        services.AddSingleton<IMessageBus>(_ => new SqlMessageBus(connectionString));
        return services;
    }
}
