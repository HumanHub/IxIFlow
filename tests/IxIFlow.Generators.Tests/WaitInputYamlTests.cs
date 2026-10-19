using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Dsl.Documents;
using IxIFlow.Extensions;
using Json.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Generators.Tests;

public sealed class WaitInputYamlTests
{
    [Theory]
    [InlineData("waitFor", false)]
    [InlineData("waitFor", true)]
    [InlineData("suspend", false)]
    [InlineData("suspend", true)]
    public async Task YamlAndFluentMappingsHaveTheSameSuggestionAndFingerprint(string kind, bool hasDefault)
    {
        var yaml = """
            workflow:
              name: WaitInputParity
              dataType: IxIFlow.Generators.Tests.FlowData
            definitions:
              - kind: activity
                id: previous
                activity: IxIFlow.Generators.Tests.Multiply
                input:
                  - target: Value
                    from: {kind: constant, value: 3}
                  - target: Factor
                    from: {kind: constant, value: 2}
              - kind: WAIT_KIND
                id: review
                WAIT_KEY: review
                event: IxIFlow.Generators.Tests.WaitDefaultAnswer
                input:
                  - target: Value
                    from: {path: workflow.Value + previousStep.Result}
            """.Replace("WAIT_KIND", kind).Replace("WAIT_KEY", kind == "suspend" ? "reason" : "key");
        if (hasDefault) yaml += "\n    defaultPayload: {Value: 1, Detail: {Note: keep}, Choices: [1, 2]}\n";
        // Exercise both readers before compiling the shared document.
        var document = WorkflowJsonSerializer.Read(WorkflowJsonSerializer.Write(WorkflowYamlSerializer.Read(yaml)));
        var generated = GeneratorRegressionTests.Build(WorkflowYamlSerializer.Write(document));
        var builder = Workflow.Create<FlowData>("WaitInputParity")
            .Step<Multiply>(setup => setup.Input(activity => activity.Value).From(ctx => 3).Input(activity => activity.Factor).From(ctx => 2))
            .WithId("previous");
        void Configure(ISuspendSetupBuilder<FlowData, WaitDefaultAnswer, Multiply> setup)
        {
            if (hasDefault) setup.DefaultPayload(new WaitDefaultAnswer { Value = 1, Detail = new() { Note = "keep" }, Choices = [1, 2] });
            setup.Input(answer => answer.Value).From(ctx => ctx.WorkflowData.Value + ctx.PreviousStep.Result);
        }
        var fluent = (kind == "suspend" ? builder.Suspend<WaitDefaultAnswer>("review", configure: Configure)
            : builder.WaitFor<WaitDefaultAnswer>("review", configure: Configure)).WithId("review").Build();
        var fingerprint = new WorkflowScopeCatalog(generated).Fingerprint;
        Assert.Equal(new WorkflowScopeCatalog(fluent).Fingerprint, fingerprint);
        Assert.NotEqual(fingerprint, new WorkflowScopeCatalog(GeneratorRegressionTests.Build(
            yaml.Replace("workflow.Value + previousStep.Result", "workflow.Value + previousStep.Result + 1"))).Fingerprint);
        Assert.Equal(await Suggestion(fluent), await Suggestion(generated));

        async Task<string> Suggestion(WorkflowDefinition definition)
        {
            await using var services = new ServiceCollection().AddLogging().AddIxIFlow().Services.BuildServiceProvider();
            var client = services.GetRequiredService<IWorkflowClient>();
            var started = await client.RunAsync(definition, new FlowData { Value = 5 });
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
            var json = Assert.Single(await client.GetOpenWaitsAsync(new(InstanceId: started.InstanceId))).DefaultPayloadJson!;
            var answer = JsonSerializer.Deserialize<WaitDefaultAnswer>(json)!;
            Assert.Equal(11, answer.Value);
            Assert.Equal(hasDefault ? "keep" : null, answer.Detail?.Note);
            return json;
        }
    }

    [Theory]
    [InlineData("arguments: []", "from", "arguments")]
    [InlineData("returns: int", "asset", "returns")]
    [InlineData("entryPoint: Evaluate", "asset", "entryPoint")]
    [InlineData("sourceContext: event", "output", "sourceContext")]
    public void RemovedMembersAreRejectedByBothReadersTheGeneratorAndTheSchema(string member, string position, string name)
    {
        var yaml = """
            workflow: {name: RemovedMembers, dataType: FlowData}
            code:
              expression:
                language: csharp-expression
                source: System.Math.Abs(-1)
            definitions:
              - kind: activity
                activity: Multiply
                input:
                  - target: Value
                    from: {kind: function, function: expression}
                output:
                  - source: Result
                    to: {path: workflow.Result}
            """;
        yaml = position switch
        {
            "from" => yaml.Replace("function: expression}", "function: expression, " + member + "}"),
            "asset" => yaml.Replace("    source:", "    " + member + "\n    source:"),
            _ => yaml.Replace("        to:", "        " + member + "\n        to:")
        };
        var readError = Assert.Throws<YamlDotNet.Core.YamlException>(() => WorkflowYamlSerializer.Read(yaml));
        Assert.Contains("Unknown member '" + name + "'", readError.Message);
        var sourceError = Assert.Throws<WorkflowSourceException>(() => WorkflowSourceTranslator.Translate(yaml, "removed.yaml"));
        var diagnostic = Assert.Single(WorkflowGeneratorHarness.Generate(yaml).Diagnostics);
        Assert.Equal("IXIYAML001", diagnostic.Id);
        Assert.Equal(readError.Start.Line - 1, diagnostic.Location.GetLineSpan().StartLinePosition.Line);
        Assert.Equal(readError.Start.Column - 1, diagnostic.Location.GetLineSpan().StartLinePosition.Character);
        Assert.Equal(readError.Start.Line, sourceError.Line);
        Assert.Equal(readError.Start.Column, sourceError.Column);
        var json = WorkflowYamlSchemaValueTests.Read(yaml);
        Assert.Throws<JsonException>(() => WorkflowJsonSerializer.Read(json.GetRawText()));
        Assert.False(JsonSchema.FromText(WorkflowDocumentSchema.Write(),
            new BuildOptions { SchemaRegistry = new SchemaRegistry() }).Evaluate(json).IsValid);
    }
}
