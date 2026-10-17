using System.Text.Json;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Tests.Infrastructure;

/// <summary>
/// What a parallel step's branches have committed so far, as one view of the workflow data. Under
/// D-14 the saved data and run results show the instance's data only until the join; branches keep
/// their own copies, saved as changes. Tests that prove a branch committed while a sibling still waits
/// read this view of the saved state instead.
/// </summary>
public static class BranchProgress
{
    public static async Task<T> ViewAsync<T>(IWorkflowStateRepository repository, string instanceId)
    {
        var saved = await repository.GetWorkflowInstanceAsync(instanceId)
            ?? throw new InvalidOperationException($"Instance '{instanceId}' was not saved");
        var data = JsonSerializer.Deserialize<T>(saved.WorkflowDataJson)!;
        return (T)BranchData.Overlay(data, ExecutionCheckpoint.Read(saved.ExecutionStateJson));
    }
}
