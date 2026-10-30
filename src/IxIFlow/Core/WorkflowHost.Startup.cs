using Microsoft.Extensions.Hosting;

namespace IxIFlow.Core;

public sealed partial class WorkflowHost : IHostedLifecycleService
{
    private readonly object _startupLock = new();
    private readonly List<Func<CancellationToken, Task>> _startupValidations = [];

    internal void RegisterStartupValidation(Action validate)
    {
        RegisterStartupValidation(_ =>
        {
            validate();

            return Task.CompletedTask;
        });
    }

    internal void RegisterStartupValidation(Func<CancellationToken, Task> validate)
    {
        lock (_startupLock)
            _startupValidations.Add(validate);
    }

    Task IHostedLifecycleService.StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // GenericWebHostService has now built the pipeline, including conventional Startup.Configure routes.
    // Throwing here propagates through Host.StartAsync, before ApplicationStarted is signalled.
    async Task IHostedLifecycleService.StartedAsync(CancellationToken cancellationToken)
    {
        Func<CancellationToken, Task>[] validations;

        lock (_startupLock)
            validations = _startupValidations.ToArray();

        foreach (var validate in validations)
            await validate(cancellationToken).ConfigureAwait(false);
    }

    Task IHostedLifecycleService.StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    Task IHostedLifecycleService.StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}