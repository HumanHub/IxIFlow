using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Samples;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Samples.Tests;

[Trait("Feature", "control-flow")]
[Trait("Spec", "W-04")]
public sealed class AssignSampleTests
{
    internal static WorkflowDefinition GeneratedDefinition() => IxIFlow.Samples.Generated.Workflow_assign_totals_workflow.Build();

    [Fact]
    public async Task FluentAndYamlAssignmentsHaveTheSameHashAndTotal()
    {
        var fluent = AssignTotals.Definition();
        var yaml = GeneratedDefinition();
        Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(yaml));
        foreach (var definition in new[] { fluent, yaml })
        {
            await using var services = new ServiceCollection().AddLogging().AddIxIFlow().Services.BuildServiceProvider();
            var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new AssignmentTotalsData());
            Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
            Assert.Equal(37.5m, Assert.IsType<AssignmentTotalsData>(result.WorkflowData).Total);
        }
    }
}
