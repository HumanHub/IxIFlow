using System.Text.Json;
using IxIFlow.Core;
using IxIFlow.Dsl.Documents;
using IxIFlow.Extensions;
using IxIFlow.Generators.Tests.Generated;
using IxIFlow.Samples;
using Json.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Generators.Tests;

[Trait("Spec", "F-06")]
public sealed class GraphSampleParityTests
{
    public static IEnumerable<object[]> Samples() =>
    [
        ["stock-flowchart", (Func<WorkflowDefinition>)Flowcharts.Definition, (Func<WorkflowDefinition>)Workflow_stock_flowchart.Build],
        [
            "approval-state-machine", (Func<WorkflowDefinition>)StateMachines.Definition,
            (Func<WorkflowDefinition>)Workflow_approval_state_machine.Build
        ],
        [
            "activity-trigger-state-machine", (Func<WorkflowDefinition>)ActivityTriggers.Definition,
            (Func<WorkflowDefinition>)Workflow_activity_trigger_state_machine.Build
        ]
    ];

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task CompiledSampleDocumentsHaveFluentParityAndDesignerLocations(string name,
        Func<WorkflowDefinition> fluent, Func<WorkflowDefinition> generated)
    {
        var definition = generated();
        Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent()), WorkflowDefinitionValidator.Inspect(definition));
        var path = Path.Combine(WorkflowSchemaFiles.RepositoryRoot(), "samples/IxIFlow.Samples/Definitions", name + ".yaml");

        Assert.True(JsonSchema.FromFile(WorkflowSchemaFiles.SchemaPath, new BuildOptions { SchemaRegistry = new SchemaRegistry() })
            .Evaluate(WorkflowYamlSchemaValueTests.Read(File.ReadAllText(path))).IsValid);

        var table = Assert.Single(definition.GraphTable);
        Assert.All(table.Nodes, n => Assert.Contains(name + ".yaml:", n.SourceLocation));
        Assert.All(table.Transitions.SelectMany(t => t.Cases), c => Assert.Contains(name + ".yaml:", c.SourceLocation));
        Assert.All(definition.StepTable, s => Assert.Contains(name + ".yaml:", s.SourceLocation));

        Assert.Equal(await Execute(fluent()), await Execute(definition));

        async Task<string> Execute(WorkflowDefinition graph)
        {
            var collection = new ServiceCollection().AddLogging();
            var mailbox = new StateMachines.Mailbox();
            collection.AddSingleton(mailbox);
            collection.AddIxIFlow();
            await using var services = collection.BuildServiceProvider();
            var client = services.GetRequiredService<IWorkflowClient>();

            object data = name switch
            {
                "stock-flowchart" => new Flowcharts.Order(),
                "approval-state-machine" => new StateMachines.Approval(),
                _ => new ActivityTriggers.Review()
            };

            var result = await client.RunAsync(graph, data);

            if (name == "approval-state-machine")
            {
                Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
                result = await client.ReplyAsync(mailbox.ReplyToken, new StateMachines.Decision { Approved = true });
                var approval = Assert.IsType<StateMachines.Approval>(result.WorkflowData);
                Assert.True(approval.ExitRecorded && approval.DecisionRecorded && approval.Notified && approval.Approved);
            }
            else if (name == "stock-flowchart")
                Assert.True(Assert.IsType<Flowcharts.Order>(result.WorkflowData).Shipped);
            else
                Assert.True(Assert.IsType<ActivityTriggers.Review>(result.WorkflowData).Approved);

            Assert.Equal(WorkflowExecutionStatus.Success, result.Status);

            return JsonSerializer.Serialize(result.WorkflowData);
        }
    }
}