using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Debugging;
using Microsoft.Extensions.DependencyInjection;
namespace IxIFlow.Samples;

public static class DebuggingExamples
{
    public static void ConfigureEdits(IServiceCollection services, byte[] auditKey)
    {
        // snippet: edit-host
        services.AddIxIFlow(options =>
        {
            options.Debugging = true;
            options.DataEditAuditKey = auditKey;
        });
        // end-snippet: edit-host
    }

    public static async Task<DataEditResult> EditAsync(IWorkflowOperations operations, string instanceId,
        string editId, long pausedRevision, long pauseGeneration)
    {
        // snippet: edit-data
        var receipt = await operations.EditDataAsync(instanceId, editId, pausedRevision,
            new DataEditTarget(pauseGeneration),
            [new DataPatch(["Total"], System.Text.Json.JsonSerializer.SerializeToElement(125.50m))],
            actor: "operator@example.com");
        // end-snippet: edit-data
        return receipt;
    }

    public static async Task StepAsync(IWorkflowOperations operations, string instanceId, string continuationId)
    {
        // snippet: step
        await operations.StepAsync(instanceId, continuationId, DebugStepKind.Over,
            actor: "operator@example.com");
        // end-snippet: step
    }

    public static void ConfigureConditions(IServiceCollection services)
    {
        // snippet: configure-conditions
        services.AddIxIFlow(options => options.Debugging = true);
        services.AddBreakpointConditions();
        // end-snippet: configure-conditions
    }

    public static async Task ConditionsAsync(IWorkflowOperations operations, string instanceId)
    {
        // snippet: conditions
        await operations.SetBreakpointsAsync(instanceId,
        [
            new Breakpoint("large-order", "charge-card",
                Condition: "context.WorkflowData.Total > 1000"),
            new Breakpoint("failed-output", "after-charge",
                Condition: "context.PreviousStep.Succeeded == false"),
            new Breakpoint("second-item", "root/2/foreach/0", HitCondition: ">= 2",
                Condition: "context.Index == 1 && context.Item.Amount > 100")
        ], actor: "operator@example.com");
        // end-snippet: conditions
    }

    public static Breakpoint[] PureConditions()
    {
        // snippet: pure-conditions
        Breakpoint[] examples =
        [
            new("total", "charge-card", Condition: "context.WorkflowData.Items.Where(x => x.Amount > 0).Sum(x => x.Amount) > 1000"),
            new("name", "charge-card", Condition: "context.WorkflowData.Name.StartsWith(\"test\", StringComparison.OrdinalIgnoreCase)"),
            new("time", "charge-card", Condition: "context.UtcNow.Hour >= 18")
        ];
        // end-snippet: pure-conditions
        return examples;
    }

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
