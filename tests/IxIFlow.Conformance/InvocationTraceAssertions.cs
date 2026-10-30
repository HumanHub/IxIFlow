using IxIFlow.Core;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Tests.Conformance;

internal static class InvocationTraceAssertions
{
    internal static async Task<InvocationTrace[]> AttemptsAsync(IWorkflowStateRepository store, string id, WorkflowDefinition definition)
    {
        var replay = TraceReplay.Load(definition, await store.GetTraceAsync(id, 0, 1000));

        while (replay.StepForward())
        {
        }

        return replay.Current.Invocations.OrderBy(attempt => attempt.StepId, StringComparer.Ordinal)
            .ThenBy(attempt => attempt.AttemptNumber).ToArray();
    }

    internal static async Task<RetryTrace[]> RetriesAsync(IWorkflowStateRepository store, string id) =>
        (await store.GetTraceAsync(id, 0, 1000)).Select(entry => entry.Entry.Payload).OfType<RetryTrace>()
        .Where(retry => retry.Invocation != null).ToArray();

    internal static async Task PrunedAsync(IWorkflowStateRepository store, string id)
    {
        var saved = (await store.GetWorkflowInstanceAsync(id))!;
        Assert.Empty(ExecutionCheckpoint.Read(saved.ExecutionStateJson).InvocationRecords);
        Assert.DoesNotContain(saved.SavedParts!.Keys, key => key.StartsWith("v/", StringComparison.Ordinal));
    }
}