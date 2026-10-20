using IxIFlow.Core;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Infrastructure;

internal sealed class DiagnosticTestAccess : IWorkflowDiagnosticsAuthorizer
{
    public ValueTask<bool> CanReadAsync(string instanceId, CancellationToken cancellationToken) => ValueTask.FromResult(true);

    public static void Allow(IServiceCollection services) => services.AddScoped<IWorkflowDiagnosticsAuthorizer, DiagnosticTestAccess>();
}
