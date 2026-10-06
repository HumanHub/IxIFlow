using IxIFlow.Core;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Extensions;

public static class WorkflowHostExtensions
{
    /// <summary>
    /// Adds optional coordinator and host services. Register IHostRegistry and
    /// IMessageBus separately before resolving the host.
    /// </summary>
    public static IServiceCollection AddIxIFlowHost(
        this IServiceCollection services,
        Action<WorkflowHostOptions> configure)
    {
        services.AddIxIFlow();

        var options = new WorkflowHostOptions();
        configure(options);
        services.AddSingleton(options);

        services.AddSingleton<IWorkflowHost, WorkflowHost>();
        services.AddSingleton<IWorkflowCoordinator, WorkflowCoordinator>();
        services.AddSingleton<IWorkflowHostClient, HttpWorkflowHostClient>();
        services.AddHostedService<WorkflowQueueService>();
        services.AddHostedService<WorkflowCompletionOutboxService>();
        services.AddHostedService<WorkflowMessageCleanupService>();
        services.AddHostedService<WorkflowRecoveryService>();
        services.AddHostedService<HostHealthService>();

        return services;
    }
}
