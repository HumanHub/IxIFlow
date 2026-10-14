using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Core;

/// <summary>Retries completion publication after a host dies between commit and publish.</summary>
public sealed class WorkflowCompletionOutboxService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IMessageBus _bus;
    private readonly WorkflowHostOptions _options;
    private readonly ILogger<WorkflowCompletionOutboxService> _logger;

    public WorkflowCompletionOutboxService(IServiceScopeFactory scopes,
        IMessageBus bus, WorkflowHostOptions options,
        ILogger<WorkflowCompletionOutboxService> logger)
    {
        _scopes = scopes;
        _bus = bus;
        _options = options;
        _logger = logger;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWorkflowStateRepository>();
        if (repository is not IWorkflowCompletionOutbox)
            throw new InvalidOperationException(
                "Distributed hosting requires a workflow repository with a completion outbox");
        return base.StartAsync(cancellationToken);
    }

    internal async Task PublishOnceAsync()
    {
        using var scope = _scopes.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWorkflowStateRepository>();
        await new WorkflowCompletionPublisher(repository, _bus, _options.HostId)
            .PublishPendingAsync();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try
            {
                await PublishOnceAsync();
            }
            catch (Exception error)
            {
                _logger.LogError(error, "Could not publish pending workflow completions");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
