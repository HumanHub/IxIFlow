using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
namespace IxIFlow.Samples;

public static class DebuggingExamples
{
    public static void Configure(IServiceCollection services)
    {
        // snippet: configure
        services.AddIxIFlow(options => options.Debugging = true);
        // end-snippet: configure
    }
    public static async Task OperateAsync(IServiceProvider provider, string instanceId)
    {
        // snippet: operate
        var operations = provider.GetRequiredService<IWorkflowOperations>();
        await operations.PauseAsync(instanceId, actor: "operator@example.com");
        await operations.ContinueAsync(instanceId, actor: "operator@example.com");
        // end-snippet: operate
    }
}
