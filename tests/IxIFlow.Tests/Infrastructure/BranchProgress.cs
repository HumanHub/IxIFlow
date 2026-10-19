using System.Text.Json;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Tests.Infrastructure;

/// <summary>Reads one saved branch copy, following branch indexes from the root through open joins.</summary>
public static class BranchProgress
{
    public static async Task<T> ReadAsync<T>(IWorkflowStateRepository repository, string instanceId,
        params int[] branchPath)
    {
        var saved = await repository.GetWorkflowInstanceAsync(instanceId)
            ?? throw new InvalidOperationException($"Instance '{instanceId}' was not saved");
        var checkpoint = ExecutionCheckpoint.Read(saved.ExecutionStateJson);
        checkpoint.RootData = JsonSerializer.Deserialize<T>(saved.WorkflowDataJson.InlineText)!;
        var continuation = checkpoint.Continuations.Single(item => item.ParentJoinId == null);
        foreach (var index in branchPath)
        {
            var join = checkpoint.Joins.Single(item => item.ParentContinuationId == continuation.Id);
            continuation = checkpoint.BranchesOf(join)[index];
        }
        return (T)checkpoint.DataOf(continuation);
    }
}
