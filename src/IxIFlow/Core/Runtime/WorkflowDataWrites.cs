using IxIFlow.Builders;

namespace IxIFlow.Core.Runtime;

/// <summary>The explicit data writes used by both parallel conflict and item isolation checks.</summary>
internal static class WorkflowDataWrites
{
    internal static IEnumerable<string?> Of(WorkflowStep step)
    {
        foreach (var mapping in step.OutputMappings.Concat(
                     step.StepMetadata.GetValueOrDefault("CompensationActivities") is List<CompensationActivityInfo> compensations
                         ? compensations.SelectMany(item => item.OutputMappings) : []))
            if (mapping.Direction == PropertyMappingDirection.Output) yield return mapping.TargetPath;
        if (step.Assignment != null) yield return step.Assignment.TargetPath;
        if (step.ForEachIntoPath != null) yield return step.ForEachIntoPath;
    }
}
