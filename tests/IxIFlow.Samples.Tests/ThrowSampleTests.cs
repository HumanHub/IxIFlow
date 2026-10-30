using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Samples;
using IxIFlow.Samples.Generated;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Samples.Tests;

[Trait("Feature", "try")]
[Trait("Spec", "W-05")]
public sealed class ThrowSampleTests
{
    internal static WorkflowDefinition GeneratedDefinition() => Workflow_throw_and_rethrow_workflow.Build();

    [Fact]
    public async Task ThrowSampleFluentAndYamlHaveTheSameContractAndResult()
    {
        var fluent = ThrowAndRethrow.Definition();
        var yaml = GeneratedDefinition();
        Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(yaml));

        foreach (var definition in new[] { fluent, yaml })
        {
            await using var services = new ServiceCollection().AddLogging().AddIxIFlow().Services.BuildServiceProvider();
            var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new DeclineData());
            Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
            Assert.Equal(402, Assert.IsType<DeclineData>(result.WorkflowData).RecordedCode);
        }
    }
}