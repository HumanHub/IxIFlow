using System.Text.Json;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Samples;
using IxIFlow.Samples.Generated;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Samples.Tests;

[Trait("Feature", "control-flow"), Trait("Spec", "W-03")]
public sealed class SwitchSampleTests
{
    internal static WorkflowDefinition GeneratedDefinition() => Workflow_switch_routing_workflow.Build();

    [Theory]
    [InlineData("ship", "shipping")]
    [InlineData("hold", "on hold")]
    [InlineData("other", "manual review")]
    public async Task FluentAndYamlSaveTheSameStructureAndSelectTheSameBody(string route, string expected)
    {
        var fluent = SwitchRouting.Definition();
        var yaml = GeneratedDefinition();
        Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(yaml));
        string? savedHash = null;

        foreach (var definition in new[] { fluent, yaml })
        {
            await using var services = SampleTests.Services();
            var result = await SampleTests.Client(services).RunAsync(definition, new RoutingData { Route = route });
            Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
            Assert.Equal(expected, Assert.IsType<RoutingData>(result.WorkflowData).Result);
            var repository = services.GetRequiredService<IWorkflowStateRepository>();
            var saved = await repository.GetWorkflowInstanceAsync(result.InstanceId);
            using var checkpoint = JsonDocument.Parse(saved!.ExecutionStateJson);
            var hash = checkpoint.RootElement.GetProperty("StructureHash").GetString();

            if (savedHash != null)
                Assert.Equal(savedHash, hash);

            savedHash = hash;
        }
    }
}