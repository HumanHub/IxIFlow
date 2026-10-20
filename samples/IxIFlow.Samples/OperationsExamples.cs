using IxIFlow.Core;

namespace IxIFlow.Samples;

/// <summary>Compiled examples for the operations guide.</summary>
public static class OperationsExamples
{
    public static async Task SearchAsync(IWorkflowOperations operations)
    {
        // snippet: search
        var page = await operations.SearchAsync(new InstanceQuery(
            Statuses: [WorkflowStatus.Suspended, WorkflowStatus.NeedsResolution],
            WorkflowName: "Order", WorkflowVersion: 2, Limit: 100));
        var waits = await operations.GetWaitsAsync(page.Items.Select(row => row.InstanceId).ToArray());
        var next = await operations.SearchAsync(new InstanceQuery(
            Statuses: [WorkflowStatus.Suspended, WorkflowStatus.NeedsResolution],
            WorkflowName: "Order", WorkflowVersion: 2, Limit: 100, After: page.NextCursor));
        // end-snippet: search
    }
    public static async Task TerminateAsync(IWorkflowOperations operations, string instanceId)
    {
        // snippet: terminate
        var stopped = await operations.TerminateAsync(instanceId, "operator-stop",
            "Stopped after investigation", actor: "alice");
        // end-snippet: terminate
    }
}
