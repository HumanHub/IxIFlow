using System.Text.Json;
using IxIFlow.Core;
namespace IxIFlow.ContractTests;

internal static class InspectionMessageExamples
{
    public static object[] Create(DateTime now)
    {
        var frame = new InspectionFrame("activation", "scope", "Sequence", "root/0/sequence", null, 1, 2, 3);
        var thread = new InspectionThread("root", "Root", null, null, 0, null, "root/0", "author", 0, "Active", "activation", [frame]);
        var summary = new InspectionSummary("instance", "workflow", 1, WorkflowStatus.Paused, "parent", now, null, "error");
        var debug = new InspectionDebugState(1, 2, null, null, now);
        var attempt = new InspectionAttempt("root", "invocation", "root/0", "attempt", "Execute", "host", now, null);
        var wait = new InspectionWait("ref", "root", "root/0", WaitKind.Event, "key", now, "child");
        var data = new InspectionData("root", JsonSerializer.SerializeToElement(new { Value = 42 }));
        return [new InstanceInspection(summary, 3, "hash", debug, [], [thread], [attempt], [wait], [data]),
            summary, debug, thread, frame, attempt, wait, data,
            new ReplayOriginTrace("instance", "hash", 1, TraceDetail.Full),
            new ThreadPositionTrace("root", thread),
            new ReplayPosition(4, now, TraceClock.Store, 3, WorkflowStatus.Paused, true, [], [thread], true)];
    }
}
