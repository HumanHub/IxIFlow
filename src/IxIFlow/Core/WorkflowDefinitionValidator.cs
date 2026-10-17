using IxIFlow.Builders;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

/// <summary>
/// Runs every build rule on a workflow definition: the rules the fluent <c>Build()</c> applies, and those checked when
/// a definition is registered or run (D-26). A definition built without the fluent builder, such as one compiled from a
/// document, gets the same checks here.
/// </summary>
public static class WorkflowDefinitionValidator
{
    /// <summary>
    /// Checks <paramref name="definition"/> and pairs its requests with their waits, as building it would. Throws a
    /// <see cref="WorkflowEngineException"/> of kind <see cref="WorkflowErrorKind.InvalidDefinition"/> naming the first
    /// rule it breaks.
    /// </summary>
    public static void Validate(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Apply(definition.Steps);
        _ = new WorkflowScopeCatalog(definition);
    }

    /// <summary>The build rules, in order, over the steps of a definition being built; returns the steps.</summary>
    internal static List<WorkflowStep> Apply(List<WorkflowStep> steps) =>
        MappingRules.Checked(ParallelRules.Checked(ForEachRules.Paired(SagaRules.Checked(RequestPairing.Paired(steps)))));
}
