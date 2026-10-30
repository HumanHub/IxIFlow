using System.Runtime.CompilerServices;
using IxIFlow.Core;

namespace IxIFlow.Builders;

// Continuation builders share their root step list, just like scope declarations.
internal sealed class WorkflowPresentation
{
    private static readonly ConditionalWeakTable<List<WorkflowStep>, WorkflowPresentation> Values = new();
    internal static WorkflowPresentation For(List<WorkflowStep> steps) => Values.GetValue(steps, _ => new());
    internal string? DisplayName;
    internal string Description = "";
    internal string? Icon;
    internal string? Category;
    internal string[] Tags = [];

    internal void Apply(WorkflowDefinition definition)
    {
        definition.DisplayName = DisplayName;
        definition.Description = Description;
        definition.Icon = Icon;
        definition.Category = Category;
        definition.Tags = Tags.ToArray();
    }
}