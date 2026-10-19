using IxIFlow.Core;

namespace IxIFlow.Tests.Infrastructure;

/// <summary>
/// Children run as instances of their own (D-17): a parent's call returns at its first idle point, while
/// its child runs in a slot of its own, and the parent finishes once the child's completion wakes it.
/// </summary>
public static class ChildRuns
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>Runs <paramref name="definition"/> and, if it went idle waiting for a child, waits for its end.</summary>
    public static async Task<WorkflowExecutionResult> RunToEndAsync<TData>(this IWorkflowClient client,
        WorkflowDefinition definition, TData data, WorkflowOptions? options = null) where TData : class =>
        await client.FinishedAsync(await client.RunAsync(definition, data, options));

    /// <summary>The end of a run a call left idle; any other result as it is.</summary>
    public static async Task<WorkflowExecutionResult> FinishedAsync(this IWorkflowClient client,
        WorkflowExecutionResult result) =>
        result.Status == WorkflowExecutionStatus.Suspended
            ? await client.AwaitAsync(result.InstanceId, WorkflowAwaitCondition.Finished).WaitAsync(Patience)
            : result;

    /// <summary>
    /// The instance of the workflow named <paramref name="name"/>, once it waits for <paramref name="key"/>:
    /// an event addressed by key needs its wait to be open (D-33).
    /// </summary>
    public static async Task<string> WaitingInstanceAsync(this IWorkflowStateRepository store, string name, string key)
    {
        using var patience = new CancellationTokenSource(Patience);
        while (true)
        {
            foreach (var instance in await Task.WhenAll((await store.QueryInstancesAsync(new InstanceQuery(WorkflowName: name, Limit: int.MaxValue))).Items.Select(async row => (await store.GetWorkflowInstanceAsync(row.InstanceId))!)))
                if ((await ((IWorkflowInbox)store).GetOpenWaitsAsync(
                        new WaitQuery(InstanceId: instance.InstanceId, Key: key))).Count > 0)
                    return instance.InstanceId;
            await Task.Delay(10, patience.Token);
        }
    }
}
