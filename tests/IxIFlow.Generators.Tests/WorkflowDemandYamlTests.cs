using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Dsl.Documents;
using IxIFlow.Dsl.Compilation;
using Json.Schema;

namespace IxIFlow.Generators.Tests;

public sealed class WorkflowDemandYamlTests
{
    private const string Yaml = """
        workflow: {name: Parity, dataType: IxIFlow.Generators.Tests.ParityData}
        demands:
          - {key: gpu, value: cuda, inherit: false}
          - {key: region, from: {path: 'workflow.Value.ToString()'}, inherit: true}
          - {key: disk}
        definitions:
          - {kind: activity, id: work, activity: IxIFlow.Generators.Tests.ParityWork}
        """;

    [Fact]
    public void YamlAndFluentResolveToTheSameSnapshotAndHash()
    {
        var generated = GeneratorRegressionTests.Build(Yaml);
        var fluent = Workflow.Create<ParityData>("Parity").Demand("gpu", "cuda")
            .DemandFrom("region", ctx => ctx.WorkflowData.Value.ToString(), inherit: true)
            .Demand("disk").Step<ParityWork>().WithId("work").Build();
        var first = new WorkflowScopeCatalog(fluent).StructureHash;
        var second = new WorkflowScopeCatalog(generated).StructureHash;
        Assert.Equal(first, second);
        var data = new ParityData { Value = 42 };
        Assert.Equal(WorkflowDemands.Resolve(fluent, data, new(), first), WorkflowDemands.Resolve(generated, data, new(), second));
        var noPlacement = GeneratorRegressionTests.Build(Yaml.Remove(Yaml.IndexOf("demands:", StringComparison.Ordinal),
            Yaml.IndexOf("definitions:", StringComparison.Ordinal) - Yaml.IndexOf("demands:", StringComparison.Ordinal)));
        Assert.Equal(second, new WorkflowScopeCatalog(noPlacement).StructureHash);
        var roundTrip = WorkflowYamlSerializer.Read(WorkflowYamlSerializer.Write(WorkflowYamlSerializer.Read(Yaml)));
        Assert.Equal(3, roundTrip.Demands.Count);
        Assert.True(roundTrip.Demands[1].Inherit);
    }

    [Fact]
    public void SchemaAcceptsDemandDeclarations()
    {
        var schema = JsonSchema.FromFile(WorkflowSchemaFiles.SchemaPath, new BuildOptions { SchemaRegistry = new SchemaRegistry() });
        Assert.True(schema.Evaluate(WorkflowYamlSchemaValueTests.Read(Yaml)).IsValid);
    }

    [Theory]
    [InlineData("{key: region, value: eu, from: {path: workflow.Value}}", "DEMAND002")]
    [InlineData("{key: ''}", "MEMBER001")]
    [InlineData("{key: region, from: {path: previousStep.Value}}", "EXPR001")]
    public async Task InvalidDemandsAreRefused(string declaration, string code)
    {
        var document = WorkflowYamlSerializer.Read("workflow: {name: Test, dataType: IxIFlow.Generators.Tests.ParityData}\ndemands: [" + declaration + "]");
        var result = await new WorkflowDocumentValidator().ValidateAsync(document, new Context());
        Assert.Contains(result.Diagnostics, d => d.Code == code);
    }

    private sealed class Context : IWorkflowValidationContext
    {
        public Task<bool> HasDataTypeAsync(string type, CancellationToken token) => Task.FromResult(true);
        public Task<ActivityDocumentContract?> FindActivityAsync(string key, CancellationToken token) => Task.FromResult<ActivityDocumentContract?>(null);
        public Task<bool> HasEventAsync(string key, CancellationToken token) => Task.FromResult(true);
        public Task<bool> HasWorkflowAsync(string name, int? version, CancellationToken token) => Task.FromResult(true);
        public Task<bool> HasStepTemplateAsync(string key, CancellationToken token) => Task.FromResult(true);
    }
}
