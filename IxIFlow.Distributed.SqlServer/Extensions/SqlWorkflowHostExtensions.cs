using IxIFlow.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Extensions;

public static class SqlWorkflowHostExtensions
{
    /// <summary>
    /// Adds distributed host services with a SQL Server message bus and host registry.
    /// Workflow instance state is shared through SQL Server.
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
        services.AddSingleton<IMessageBus>(provider => new SqlMessageBus(connectionString,
            logger: provider.GetService<ILogger<SqlMessageBus>>()));
        services.Replace(ServiceDescriptor.Singleton<IWorkflowStateRepository>(
            _ => new SqlWorkflowStateRepository(connectionString)));
        return services;
    }
}
