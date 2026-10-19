using IxIFlow.Core;

namespace IxIFlow.Tests.Infrastructure;

internal static class CommitAssert
{
    // These fields are stable receipt content. Scheduling and the store clock are checked separately.
    public static void Equal(WorkflowCommitResult expected, WorkflowCommitResult actual)
    {
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.Revision, actual.Revision);
        Assert.Equal(expected.PendingCancellation, actual.PendingCancellation);
        Assert.Equal(expected.Parked, actual.Parked);
        Assert.Equal(expected.UnreadEvents, actual.UnreadEvents);
        Assert.Equal(expected.MadeRunnable, actual.MadeRunnable);
        Assert.Equal(expected.Admission, actual.Admission);
    }

    public static async Task SchedulingAsync(IWorkflowStateRepository store, string instanceId,
        WorkflowCommitResult receipt)
    {
        var row = await store.GetWorkflowInstanceAsync(instanceId);
        Assert.NotNull(row);
        Assert.Equal(row.RunnableAtUtc, receipt.RunnableAtUtc);
        Assert.NotEqual(default, receipt.StoreTimeUtc);
    }
}
