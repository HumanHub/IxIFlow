using System.Globalization;

namespace IxIFlow.Core.Runtime;

internal static class InspectionProjection
{
    // Use the same naming, ordering and frame semantics as the live snapshot.
    public static IReadOnlyList<InspectionThread> Threads(WorkflowInstance instance,
        ExecutionCheckpoint checkpoint, WorkflowScopeCatalog scopes, DateTime now)
    {
        var snapshot = ExecutionSnapshotFactory.Create(instance, checkpoint, scopes, now);
        return snapshot.Pointers.Select(p => new InspectionThread(p.PointerId, p.Name,
            p.ParentPointerId, p.ParentJoinId, p.Sequence, p.ForkActivationId, p.StepId, p.AuthorStepId,
            p.StepIndex, p.Status, p.FrameId!, snapshot.Frames.Where(f => f.State["ContinuationId"] == p.PointerId)
                .Select(f => new InspectionFrame(f.FrameId, f.Name, f.Kind, f.StepId, f.ParentFrameId,
                    int.Parse(f.State["NextStepIndex"], CultureInfo.InvariantCulture), f.LoopIteration,
                    f.ForEachItemIndex)).ToArray())).ToArray();
    }
}
