using System.Reflection;
using IxIFlow.Core;

namespace IxIFlow.Tests.Infrastructure;

/// <summary>Observes queued capacity acquisition without using an elapsed-time window.</summary>
public static class HostWorkProbe
{
    public static async Task WaitingForSlotAsync(WorkflowHost host, string instanceId)
    {
        var slots = (SemaphoreSlim)typeof(WorkflowHost).GetField("_slots", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(host)!;

        var head = typeof(SemaphoreSlim).GetField("m_asyncHead", BindingFlags.Instance | BindingFlags.NonPublic)!;

        await TestEventually.UntilAsync(
            instanceId, "work queued for an execution slot",
            () => Task.FromResult(new { slots.CurrentCount, Queued = head.GetValue(slots) != null }),
            state => state.CurrentCount == 0 && state.Queued);
    }
}