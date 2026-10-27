using System.Text.Json.Nodes;
using IxIFlow.ActivitySdk;
using IxIFlow.Authoring;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Dsl.Compilation;
using IxIFlow.Dsl.Documents;
using IxIFlow.Extensions;
using Json.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Generators.Tests;

[Trait("Feature", "registration")]
[Trait("Spec", "F-17")]
[Trait("Spec", "F-18")]
[Trait("Spec", "F-25")]
public sealed class InlineYamlTests
{
    [Fact]
    public async Task TypedFixtureAndFluentTwinHaveEqualHashesContractsAndResults()
    {
        var yaml = Generated.Workflow_inline_typed.Build();
        var fluent = Workflow.Create<InlineYamlData>("InlineYamlCaller")
            .Use<InlineYamlCallee, InlineYamlData>(setup => setup
                .Input(data => data.Value).From(ctx => ctx.WorkflowData.Value)
                .Output(data => data.Value).To(ctx => ctx.WorkflowData.Value))
            .WithId("inline").Build();
        Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(yaml));
        Assert.Equal(WorkflowDefinitionValidator.GetContract(fluent).Hash, WorkflowDefinitionValidator.GetContract(yaml).Hash);
        Assert.Equal(await Execute(fluent), await Execute(yaml));
    }

    [Fact]
    public void NamedUseLowersToAFreshFactoryWithTheVerifiedPin()
    {
        var callee = InlineYamlEntry.Build();
        var contract = WorkflowDefinitionValidator.GetContract(callee);
        var structure = WorkflowDefinitionValidator.Inspect(callee);
        var options = new WorkflowSourceOptions();
        options.Workflows["InlineYamlCallee@1"] = new(typeof(InlineYamlData).FullName!, 1)
        {
            ContractJson = contract.ToJson(), EntryType = typeof(InlineYamlEntry).FullName!,
            AssemblyName = typeof(InlineYamlEntry).Assembly.GetName().Name,
            BundleDigest = ArtifactDigest.Of([1, 2, 3]).Value, StructureHash = structure, ContractHash = contract.Hash
        };
        var source = WorkflowSourceTranslator.Translate("""
            workflow: {name: InlineNamed, dataType: IxIFlow.Generators.Tests.InlineYamlData}
            definitions:
              - kind: use
                id: site
                workflow: {name: InlineYamlCallee, version: 1}
                input: [{target: Value, from: {path: workflow.Value}}]
                output: [{source: Value, to: {path: workflow.Value}}]
            """, "named.yaml", options);
        var built = WorkflowCompilation.Build(source);
        var fluent = Workflow.Create<InlineYamlData>("InlineNamed").Use<InlineYamlData>(InlineYamlEntry.Build,
            new("InlineYamlCallee", 1, structure, contract.Hash), setup => setup
                .Input(data => data.Value).From(ctx => ctx.WorkflowData.Value)
                .Output(data => data.Value).To(ctx => ctx.WorkflowData.Value)).WithId("site").Build();
        Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(built));
        Assert.Equal("InlineYamlCallee", Assert.Single(WorkflowDefinitionValidator.GetInlineWorkflows(built)).Name);
    }

    [Theory]
    [InlineData("use", "workflow: {name: Child, version: 1}")]
    [InlineData("useType", "workflowType: Child\n    dataType: Data")]
    public void SchemaAndReadersRejectCancellationAndPolicies(string kind, string target)
    {
        foreach (var unsupported in new[] { "cancelWithParent: false", "onError: []" })
        {
            var yaml = "workflow: {name: Parent, dataType: Data}\ndefinitions:\n  - kind: " + kind + "\n    " + target + "\n    " + unsupported;
            Assert.Throws<YamlDotNet.Core.YamlException>(() => WorkflowYamlSerializer.Read(yaml));
            var json = JsonNode.Parse("{\"workflow\":{\"name\":\"Parent\",\"dataType\":\"Data\"},\"definitions\":[{\"kind\":\"" + kind + "\"}]}")!;
            var step = json["definitions"]![0]!.AsObject();
            if (kind == "use") step["workflow"] = new JsonObject { ["name"] = "Child", ["version"] = 1 };
            else step["workflowType"] = "Child";
            step[unsupported.StartsWith("onError", StringComparison.Ordinal) ? "onError" : "cancelWithParent"] = false;
            Assert.False(JsonSchema.FromText(DocumentSchema.Write(typeof(WorkflowDocument)), new BuildOptions { SchemaRegistry = new SchemaRegistry() })
                .Evaluate(System.Text.Json.JsonSerializer.SerializeToElement(json)).IsValid);
        }
    }

    [Theory]
    [InlineData("use", "workflow: {name: Child, version: 1}")]
    [InlineData("useType", "workflowType: Child")]
    public async Task SagaScopesReportStep012IncludingTemplateExpansion(string kind, string target)
    {
        foreach (var template in new[] { false, true })
        {
            var inline = "kind: " + kind + ", " + target;
            var yaml = "workflow: {name: Saga, dataType: Data}\n" +
                (template ? "steps: {body: {steps: [{" + inline + "}]}}\n" : "") +
                "definitions: [{kind: saga, steps: [{" + (template ? "kind: stepRef, ref: body" : inline) + "}]}]";
            var document = WorkflowYamlSerializer.Read(yaml);
            var options = new WorkflowSourceOptions();
            options.Workflows["Child@1"] = new("Data", 1);
            var result = await new WorkflowDocumentValidator().ValidateAsync(document, new WorkflowSourceContracts(options));
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "STEP012" && diagnostic.Message.Contains("saga"));
        }
    }

    private static async Task<string> Execute(WorkflowDefinition definition)
    {
        var services = new ServiceCollection();
        services.AddIxIFlow();
        await using var provider = services.BuildServiceProvider();
        var result = await provider.GetRequiredService<IWorkflowClient>().RunAsync(definition, new InlineYamlData { Value = "parity" });
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        return Assert.IsType<InlineYamlData>(result.WorkflowData).Value;
    }
}

public sealed class InlineYamlData
{
    [WorkflowInput, WorkflowOutput]
    public string Value { get; set; } = "";
}

[Workflow("InlineYamlCallee", Version = 1)]
public sealed class InlineYamlCallee : IWorkflow<InlineYamlData>
{
    public void Build(IWorkflowBuilder<InlineYamlData> builder) => builder.Assign(data => data.WorkflowData.Value, data => data.WorkflowData.Value + "!");
}

public static class InlineYamlEntry
{
    public static WorkflowDefinition Build() => Workflow.Create<InlineYamlData>("InlineYamlCallee")
        .Assign(data => data.WorkflowData.Value, data => data.WorkflowData.Value + "!").Build();
}
