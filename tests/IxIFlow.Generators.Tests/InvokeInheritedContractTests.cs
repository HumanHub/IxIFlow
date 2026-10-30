using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Dsl.Compilation;
using IxIFlow.Dsl.Documents;

namespace IxIFlow.Generators.Tests;

[Trait("Spec", "F-18")]
public sealed class InvokeInheritedContractTests
{
    [Fact]
    public void SymbolContractMatchesRuntimeOverrideInheritance()
    {
        var symbol = GeneratorRegressionTests.Compilation().GetTypeByMetadataName(typeof(InheritedInvokeData).FullName!)!;
        var symbolic = WorkflowDataDocumentContract.FromSymbol(symbol).Select(null, null);
        var runtime = WorkflowContract.OfType(typeof(InheritedInvokeData));

        Assert.Equal(runtime.Inputs.Select(member => member.Name),
            symbolic.Members.Where(member => member.Input).Select(member => member.Name));

        Assert.Equal(runtime.Outputs.Select(member => member.Name),
            symbolic.Members.Where(member => member.Output).Select(member => member.Name));

        foreach (var member in runtime.Inputs.Concat(runtime.Outputs))
        {
            var actual = Assert.Single(symbolic.Members, candidate => candidate.Name == member.Name);
            Assert.Equal(member.Required, actual.Required);
            Assert.Equal(member.HasDefault, actual.HasDefault);
        }

        Assert.DoesNotContain(symbolic.Members, member => member.Name == "Hidden");
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("input: []", "MAP006")]
    [InlineData("input: [{target: Result, from: {kind: constant, value: bad}}]", "MAP004")]
    [InlineData("output: [{source: Value, to: {path: workflow.Value}}]", "MAP005")]
    public async Task YamlInvokeUsesInheritedMarkersRequiredAndDefaults(string mapping, string? code)
    {
        var options = new WorkflowSourceOptions();
        var typeName = typeof(InheritedInvokeData).FullName!;
        var symbol = GeneratorRegressionTests.Compilation().GetTypeByMetadataName(typeName)!;
        options.DataContracts[typeName] = WorkflowDataDocumentContract.FromSymbol(symbol);
        options.Workflows["Child@1"] = new(typeName, 1);

        var input = mapping.Length == 0 || mapping.StartsWith("output:", StringComparison.Ordinal)
            ? "input: [{target: Mandatory, from: {kind: constant, value: mapped}}]"
            : mapping;

        var output = mapping.StartsWith("output:", StringComparison.Ordinal)
            ? mapping
            : "output: [{source: Result, to: {path: workflow.Result}}]";

        var yaml =
            $"workflow: {{name: Parent, dataType: {typeName}}}\ndefinitions:\n  - kind: invokeWorkflow\n    workflow: {{name: Child, version: 1}}\n    {input}\n    {output}";

        var result = await new WorkflowDocumentValidator().ValidateAsync(WorkflowYamlSerializer.Read(yaml),
            new WorkflowSourceContracts(options));

        if (code != null)
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code &&
                (code != "MAP006" || diagnostic.Message.Contains("Mandatory", StringComparison.Ordinal)));
        else
        {
            Assert.Empty(result.Diagnostics);
            var compilation = GeneratorRegressionTests.Compilation(WorkflowSourceTranslator.Translate(yaml, "inherited.yaml", options));
            using var bytes = new MemoryStream();
            var emitted = compilation.Emit(bytes);
            Assert.True(emitted.Success, string.Join("; ", emitted.Diagnostics));
            bytes.Position = 0;
            var assembly = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(bytes);

            var generated =
                (WorkflowDefinition)assembly.GetTypes().Single(type => type.GetMethod("Build") != null).GetMethod("Build")!.Invoke(null,
                    null)!;

            var fluent = new WorkflowBuilder<InheritedInvokeData, object>([], "Parent").Invoke<InheritedInvokeData>("Child", 1,
                setup => setup
                    .Input(data => data.Mandatory).From(_ => "mapped")
                    .Output(data => data.Result).To(ctx => ctx.WorkflowData.Result)).Build();

            Assert.Equal(Assert.Single(fluent.Steps).InvokedContract!.ToJson(), Assert.Single(generated.Steps).InvokedContract!.ToJson());

            Assert.Equal("\"fallback\"",
                Assert.Single(WorkflowContract.OfType(typeof(InheritedInvokeData)).Inputs, member => member.Name == "Value").DefaultJson);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratorPreservesInheritedContractForLocalAndTypedInvokes(bool typed)
    {
        const string input = "input: [{target: Mandatory, from: {kind: constant, value: mapped}}]";

        var yaml = $"workflow: {{name: Parent, dataType: {typeof(InheritedInvokeData).FullName}}}\ndefinitions:\n  - kind: " +
            (typed
                ? $"invokeWorkflowType\n    workflowType: {typeof(InheritedInvokeWorkflow).FullName}"
                : "invokeWorkflow\n    workflow: {name: Child, version: 1}") +
            "\n    " + input + "\n    output: [{source: Result, to: {path: workflow.Result}}]";

        var child = new WorkflowText("child.yaml",
            $"workflow: {{name: Child, dataType: {typeof(InheritedInvokeData).FullName}}}\ndefinitions: []");

        var driver = WorkflowGeneratorHarness.Driver(yaml)
            .AddAdditionalTexts(System.Collections.Immutable.ImmutableArray.Create<Microsoft.CodeAnalysis.AdditionalText>(child))
            .WithUpdatedAnalyzerConfigOptions(new WorkflowFileOptionsProvider("invalid.yaml", "child.yaml"));

        driver = driver.RunGeneratorsAndUpdateCompilation(GeneratorRegressionTests.Compilation(), out var output, out var diagnostics);
        Assert.Empty(diagnostics);
        Assert.Empty(output.GetDiagnostics().Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
        Assert.Equal(2, driver.GetRunResult().GeneratedTrees.Length);
    }

    [Fact]
    public async Task YamlDeclarationsExcludeInheritedPrivateProperties()
    {
        const string typeName = "SourceDerived";

        var compilation = GeneratorRegressionTests.Compilation("""
                                                               public class SourceBase
                                                               {
                                                                   [IxIFlow.ActivitySdk.WorkflowInput] private string Hidden { get; set; }
                                                               }
                                                               public sealed class SourceDerived : SourceBase { }
                                                               """);

        var contract = WorkflowDataDocumentContract.FromSymbol(compilation.GetTypeByMetadataName(typeName)!);
        var options = new WorkflowSourceOptions();
        options.DataContracts[typeName] = contract;
        var yaml = $"workflow: {{name: Private, dataType: {typeName}, input: [Hidden]}}\ndefinitions: []";

        var result = await new WorkflowDocumentValidator().ValidateAsync(WorkflowYamlSerializer.Read(yaml),
            new WorkflowSourceContracts(options));

        Assert.Equal("WF005", Assert.Single(result.Diagnostics).Code);
    }
}

public class InheritedInvokeBase
{
    [DefaultValue("fallback")] public virtual string Value { get; set; } = "";
    [WorkflowInput, Required] public virtual string Mandatory { get; set; } = "";
    [WorkflowOutput] public virtual string Result { get; set; } = "";
    [WorkflowInputOutput] public virtual string Both { get; set; } = "";
    [WorkflowInput] private string Hidden { get; set; } = "";
}

public sealed class InheritedInvokeData : InheritedInvokeBase
{
    [WorkflowInput, Required] public override string Value { get; set; } = "";
    public override string Mandatory { get; set; } = "";
    public override string Result { get; set; } = "";
    public override string Both { get; set; } = "";
}

[Workflow("InheritedChild")]
public sealed class InheritedInvokeWorkflow : IWorkflow<InheritedInvokeData>
{
    public void Build(IWorkflowBuilder<InheritedInvokeData> builder)
    {
    }
}