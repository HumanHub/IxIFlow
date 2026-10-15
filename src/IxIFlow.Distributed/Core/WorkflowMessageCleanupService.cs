using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Core;

/// <summary>Maintains transport messages and historical host records.</summary>
public sealed class WorkflowMessageCleanupService : BackgroundService
{
    private readonly IMessageBus _bus;
    private readonly IHostRegistry _registry;
    private readonly WorkflowHostOptions _options;
    private readonly ILogger<WorkflowMessageCleanupService> _logger;

    public WorkflowMessageCleanupService(IMessageBus bus, IHostRegistry registry,
        WorkflowHostOptions options,
        ILogger<WorkflowMessageCleanupService> logger)
    {
        _bus = bus;
        _registry = registry;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.ProcessedMessageRetention <= TimeSpan.Zero)
            throw new InvalidOperationException("Processed message retention must be positive");
        if (_options.HostMetricsRetention <= TimeSpan.Zero)
            throw new InvalidOperationException("Host metrics retention must be positive");
        if (_options.HealthCheckInterval <= TimeSpan.Zero)
            throw new InvalidOperationException("Host health interval must be positive");

        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                if (_bus is IMessageMaintenance messageMaintenance)
                {
                    while (await messageMaintenance.PruneExpiredAsync(
                               _options.ProcessedMessageRetention, 1000, stoppingToken) == 1000)
                        await Task.Delay(TimeSpan.FromMilliseconds(50), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                _logger.LogError(error, "Could not prune processed workflow messages");
            }

            try
            {
                await _registry.CleanupStaleHostsAsync(_options.HealthCheckInterval * 3);
                if (_registry is IHostRegistryMaintenance hostMaintenance)
                {
                    while (await hostMaintenance.PruneMetricsAsync(
                               _options.HostMetricsRetention, 1000, stoppingToken) == 1000)
                        await Task.Delay(TimeSpan.FromMilliseconds(50), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                _logger.LogError(error, "Could not maintain workflow host records");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
