using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Dsl.Compilation;
using IxIFlow.Dsl.Documents;

namespace IxIFlow.Generators.Tests;

[Trait("Spec", "F-18")]
[Trait("Spec", "F-25")]
public sealed class PublishedInvokeContractTests
{
    private const string Header = "workflow: {name: Contract, dataType: IxIFlow.Generators.Tests.InvokeContractData";

    [Theory]
    [InlineData("input: [Missing]", "WF005", "workflow.input[0]")]
    [InlineData("input: [Result]", "WF006", "workflow.input[0]")]
    [InlineData("output: [Value]", "WF006", "workflow.output[0]")]
    [InlineData("input: [ReadOnly]", "WF006", "workflow.input[0]")]
    public async Task DeclarationDiagnosticsUseTheListLocation(string declaration, string code, string path)
    {
        var document = WorkflowYamlSerializer.Read(Header + ", " + declaration + "}\ndefinitions: []");
        var result = await new WorkflowDocumentValidator().ValidateAsync(document, new WorkflowSourceContracts(Options()));
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal(code, error.Code);
        Assert.Equal(path, error.Path);
        Assert.Equal(1, DocumentModel.GetLocation(document, error.Path).Line);
    }

    [Theory]
    [InlineData("input: [Extra], output: [Result]", false)]
    [InlineData("input: [], output: []", true)]
    public void ListsHaveFluentContractAndStructureParity(string lists, bool empty)
    {
        var yaml = Header + ", " + lists + "}\ndefinitions: []";
        var generated = GeneratorRegressionTests.Build(yaml);
        var fluent = empty ? Workflow.Create<InvokeContractData>("Contract").Input().Output().Build() :
            Workflow.Create<InvokeContractData>("Contract").Input(data => data.Extra).Output(data => data.Result).Build();
        Assert.Equal(WorkflowDefinitionValidator.GetContract(fluent).ToJson(), WorkflowDefinitionValidator.GetContract(generated).ToJson());
        Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(generated));
        var run = WorkflowGeneratorHarness.Generate(yaml);
        Assert.Empty(run.Diagnostics);
        Assert.Single(run.GeneratedTrees);
    }

    [Theory]
    [InlineData("input: [{target: Result, from: {kind: constant, value: bad}}]", "MAP004", "definitions[0].input[0].target")]
    [InlineData("output: [{source: Extra, to: {path: workflow.Extra}}]", "MAP005", "definitions[0].output[0].source")]
    [InlineData("", "MAP006", "definitions[0].input")]
    public async Task InvokeMappingDiagnosticsUseThePinnedContract(string mapping, string code, string path)
    {
        var options = Options();
        options.Workflows.Add("Child@3", new(typeof(InvokeContractData).FullName!, 3));
        var yaml = Header + "}\ndefinitions:\n  - kind: invokeWorkflow\n    workflow: {name: Child, version: 3}\n    " + mapping;
        var result = await new WorkflowDocumentValidator().ValidateAsync(WorkflowYamlSerializer.Read(yaml), new WorkflowSourceContracts(options));
        Assert.Contains(result.Diagnostics, error => error.Code == code && error.Path == path);
    }

    [Fact]
    public void InvokeEmitsTheExactCatalogContractLiteral()
    {
        var options = Options();
        var contract = WorkflowContract.OfType(typeof(InvokeContractData));
        options.Workflows.Add("Child@3", new(typeof(InvokeContractData).FullName!, 3) { ContractJson = contract.ToJson() });
        var source = WorkflowSourceTranslator.Translate(Header + "}\ndefinitions:\n  - kind: invokeWorkflow\n    workflow: {name: Child, version: 3}\n    input: [{target: Value, from: {kind: constant, value: ok}}]", "literal.yaml", options);
        Assert.Contains("WorkflowContract.Parse(", source);
        Assert.Contains("DefaultJson", source);
        Assert.DoesNotContain("WorkflowDefinitionValidator.GetContract(new", source);
        Assert.Empty(GeneratorRegressionTests.Compilation(source).GetDiagnostics().Where(error => error.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
    }

    private static WorkflowSourceOptions Options()
    {
        var options = new WorkflowSourceOptions();
        var type = GeneratorRegressionTests.Compilation().GetTypeByMetadataName(typeof(InvokeContractData).FullName!)!;
        options.DataContracts.Add(typeof(InvokeContractData).FullName!, WorkflowDataDocumentContract.FromSymbol(type));
        return options;
    }
}

public sealed class InvokeContractData
{
    [WorkflowInput, Required] public string Value { get; set; } = "";
    [WorkflowInput, Required, DefaultValue(7)] public int Count { get; set; }
    [WorkflowOutput] public string Result { get; set; } = "";
    public string Extra { get; set; } = "";
    public string ReadOnly => "read";
}
