using IxIFlow.Core;

namespace IxIFlow.Tests.ExecutionTests;

internal static class InvocationTraceAssertions
{
    internal static async Task<InvocationTrace[]> AttemptsAsync(IWorkflowStateRepository store,
        string instanceId, WorkflowDefinition definition)
    {
        var replay = TraceReplay.Load(definition, await store.GetTraceAsync(instanceId, 0, 1000));

        while (replay.StepForward())
        {
        }

        return replay.Current.Invocations.OrderBy(attempt => attempt.StepId, StringComparer.Ordinal)
            .ThenBy(attempt => attempt.AttemptNumber).ToArray();
    }
}