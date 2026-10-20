using IxIFlow.Debugging;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Samples;

public static class DebugAdapterExamples
{
    public interface IApplicationDebugPolicy
    {
        ValueTask<bool> CanDebugAsync(string actor, string instanceId, DebugOperation operation, CancellationToken cancellationToken);
    }

    public static void Register(IServiceCollection services, IApplicationDebugPolicy policy)
    {
        // snippet: register
        services.AddIxIFlow(o =>
        {
            o.Debugging = true;
        }).AddDebugging(o =>
        {
            o.NamedPipe("ixiflow-debug");
            o.Authorize = (request, cancellationToken) =>
                policy.CanDebugAsync(request.Actor, request.InstanceId,
                    request.Operation, cancellationToken);
        });
        // end-snippet: register
    }
}
