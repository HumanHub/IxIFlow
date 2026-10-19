using Microsoft.Extensions.Hosting;

namespace IxIFlow.Core;

public sealed partial class WorkflowHost : IHostedLifecycleService
{
    private readonly object _startupLock = new();
    private readonly List<Action> _startupValidations = [];

    internal void RegisterStartupValidation(Action validate)
    {
        lock (_startupLock) _startupValidations.Add(validate);
    }

    Task IHostedLifecycleService.StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // GenericWebHostService has now built the pipeline, including conventional Startup.Configure routes.
    // Throwing here propagates through Host.StartAsync, before ApplicationStarted is signalled.
    Task IHostedLifecycleService.StartedAsync(CancellationToken cancellationToken)
    {
        Action[] validations;
        lock (_startupLock) validations = _startupValidations.ToArray();
        foreach (var validate in validations) validate();
        return Task.CompletedTask;
    }

    Task IHostedLifecycleService.StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    Task IHostedLifecycleService.StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
