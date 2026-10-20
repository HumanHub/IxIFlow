using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Core;

/// <summary>An explicit privileged read, separate from inspection. No registered authorizer means denial.</summary>
public interface IWorkflowDiagnostics
{
    Task<IReadOnlyList<WorkflowDiagnostic>> ReadAsync(string instanceId, CancellationToken cancellationToken = default);
}

/// <summary>The application checks the current caller and instance on every raw-diagnostics read.</summary>
public interface IWorkflowDiagnosticsAuthorizer
{
    ValueTask<bool> CanReadAsync(string instanceId, CancellationToken cancellationToken);
}

internal sealed class WorkflowDiagnostics(IServiceProvider services, IWorkflowStateRepository repository) : IWorkflowDiagnostics
{
    public async Task<IReadOnlyList<WorkflowDiagnostic>> ReadAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        InstanceIds.Validate(instanceId);
        cancellationToken.ThrowIfCancellationRequested();
        var instance = await repository.GetWorkflowInstanceSummaryAsync(instanceId);
        cancellationToken.ThrowIfCancellationRequested();
        var authorizer = services.GetService<IWorkflowDiagnosticsAuthorizer>();
        if (authorizer == null || !await authorizer.CanReadAsync(instanceId, cancellationToken))
            throw new UnauthorizedAccessException("Raw workflow diagnostics require application authorization");
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = instance == null ? []
            : await repository.GetDiagnosticsForIncarnationAsync(instanceId, instance.Incarnation);
        cancellationToken.ThrowIfCancellationRequested();
        return diagnostics;
    }
}
