using IxIFlow.AspNetCore;
using Microsoft.AspNetCore.Builder;

namespace IxIFlow.Samples;

public static class OperationsHttpExamples
{
    public static void Map(WebApplication app)
    {
        // snippet: operations-http-map
        app.MapWorkflowOperations("/ixiflow/ops", options =>
        {
            options.ReadPolicy = "workflow-read";
            options.ReplyPolicy = "workflow-reply";
            options.InspectPolicy = "workflow-inspect";
            options.WritePolicy = "workflow-write";
            options.OutboxPolicy = "workflow-outbox";
            options.ActForPolicy = "workflow-act-for"; // Optional delegation.
            options.MutationWait = TimeSpan.FromSeconds(10);
        });
        // end-snippet: operations-http-map
    }
}
