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
    public static async Task BreakpointsAsync(IWorkflowOperations operations, string instanceId)
    {
        // snippet: breakpoints
        var verified = await operations.SetBreakpointsAsync(instanceId,
        [
            new Breakpoint("charge", "charge-card"),
            new Breakpoint("third-item", "root/2/foreach/0", HitCondition: "== 3"),
            new Breakpoint("source", Source: new BreakpointSource("order.yaml", 20))
        ], actor: "operator@example.com");
        var current = await operations.GetBreakpointsAsync(instanceId);
        // end-snippet: breakpoints
    }
}
