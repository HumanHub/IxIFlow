using System.Collections.Immutable;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Runtime.Loader;
using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Dsl.Compilation;
using Microsoft.CodeAnalysis;

namespace IxIFlow.Generators.Tests;

[Trait("Spec", "F-18")]
public sealed class InvokeDerivedAttributeContractTests
{
    [Fact]
    public void SymbolContractMatchesRuntimeDerivedAttributesAndOverrideRules()
    {
        var symbol = GeneratorRegressionTests.Compilation().GetTypeByMetadataName(typeof(DerivedAttributeInvokeData).FullName!)!;
        var symbolic = WorkflowDataDocumentContract.FromSymbol(symbol).Select(null, null);
        var runtime = WorkflowContract.OfType(typeof(DerivedAttributeInvokeData));
        foreach (var member in runtime.Inputs)
        {
            var actual = Assert.Single(symbolic.Members, candidate => candidate.Name == member.Name);
            Assert.True(actual.Input);
            Assert.Equal(member.Required, actual.Required);
            Assert.Equal(member.HasDefault, actual.HasDefault);
        }
        var fallback = Assert.Single(runtime.Inputs, member => member.Name == "Value");
        Assert.True(fallback.Required);
        Assert.Equal("\"fallback\"", fallback.DefaultJson);
        Assert.False(Assert.Single(runtime.Inputs, member => member.Name == "NotInherited").HasDefault);
        Assert.False(Assert.Single(runtime.Inputs, member => member.Name == "Hidden").HasDefault);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocalAndTypedYamlInvokeAcceptDerivedDefaultLikeFluent(bool typed)
    {
        var driver = Driver(typed, mapRequired: true).RunGeneratorsAndUpdateCompilation(
            GeneratorRegressionTests.Compilation(), out var output, out var diagnostics);
        Assert.Empty(diagnostics);
        Assert.Empty(output.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        using var bytes = new MemoryStream();
        var emitted = output.Emit(bytes);
        Assert.True(emitted.Success, string.Join("; ", emitted.Diagnostics));
        bytes.Position = 0;
        var assembly = AssemblyLoadContext.Default.LoadFromStream(bytes);
        var generated = assembly.GetTypes().Select(type => type.GetMethod("Build", Type.EmptyTypes))
            .Where(method => method?.ReturnType == typeof(WorkflowDefinition))
            .Select(method => (WorkflowDefinition)method!.Invoke(null, null)!).Single(definition => definition.Name == "Parent");
        var builder = Parent();
        var fluent = typed
            ? builder.Invoke<DerivedAttributeInvokeWorkflow, DerivedAttributeInvokeData>(setup => setup.Input(data => data.Mandatory).From(_ => "mapped")).Build()
            : builder.Invoke<DerivedAttributeInvokeData>("Child", 1, setup => setup.Input(data => data.Mandatory).From(_ => "mapped")).Build();
        Assert.Equal(Assert.Single(fluent.Steps).InvokedContract!.ToJson(), Assert.Single(generated.Steps).InvokedContract!.ToJson());
        Assert.Equal("\"fallback\"", Assert.Single(Assert.Single(generated.Steps).InvokedContract!.Inputs,
            member => member.Name == "Value").DefaultJson);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocalAndTypedYamlInvokeRequireDerivedRequiredInput(bool typed)
    {
        var result = Driver(typed, mapRequired: false).RunGenerators(GeneratorRegressionTests.Compilation()).GetRunResult();
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal("IXIYAML001", error.Id);
        Assert.Contains("Mandatory", error.GetMessage());
        Assert.Equal(2, error.Location.GetLineSpan().StartLinePosition.Line);
        var fluent = Assert.Throws<WorkflowEngineException>(() => typed
            ? Parent().Invoke<DerivedAttributeInvokeWorkflow, DerivedAttributeInvokeData>().Build()
            : Parent().Invoke<DerivedAttributeInvokeData>("Child", 1).Build());
        Assert.Contains("Mandatory", fluent.Message);
    }

    private static WorkflowBuilder<DerivedAttributeInvokeData, object> Parent() => new([], "Parent");

    private static GeneratorDriver Driver(bool typed, bool mapRequired)
    {
        var type = typeof(DerivedAttributeInvokeData).FullName!;
        var yaml = $"workflow: {{name: Parent, dataType: {type}}}\ndefinitions:\n  - kind: " +
            (typed ? $"invokeWorkflowType\n    workflowType: {typeof(DerivedAttributeInvokeWorkflow).FullName}" : "invokeWorkflow\n    workflow: {name: Child, version: 1}") +
            (mapRequired ? "\n    input: [{target: Mandatory, from: {kind: constant, value: mapped}}]" : "");
        return WorkflowGeneratorHarness.Driver(yaml).AddAdditionalTexts(ImmutableArray.Create<AdditionalText>(
                new WorkflowText("child.yaml", $"workflow: {{name: Child, dataType: {type}}}\ndefinitions: []")))
            .WithUpdatedAnalyzerConfigOptions(new WorkflowFileOptionsProvider("invalid.yaml", "child.yaml"));
    }
}

public class InvokeFallbackAttribute() : DefaultValueAttribute("fallback");
public sealed class InvokeIndirectFallbackAttribute : InvokeFallbackAttribute;
public sealed class InvokeRequiredAttribute : RequiredAttribute;
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class InvokeLocalFallbackAttribute() : DefaultValueAttribute("local");

public class DerivedAttributeInvokeBase
{
    [WorkflowInput, Required, InvokeIndirectFallback] public virtual string Value { get; set; } = "";
    [WorkflowInput, InvokeRequired] public virtual string Mandatory { get; set; } = "";
    [WorkflowInput, InvokeLocalFallback] public virtual string NotInherited { get; set; } = "";
    [InvokeIndirectFallback] public string Hidden { get; set; } = "";
}

public sealed class DerivedAttributeInvokeData : DerivedAttributeInvokeBase
{
    [WorkflowInput, Required, InvokeFallback] public string Direct { get; set; } = "";
    public override string Value { get; set; } = "";
    public override string Mandatory { get; set; } = "";
    public override string NotInherited { get; set; } = "";
    [WorkflowInput] public new string Hidden { get; set; } = "";
}

[Workflow("Child")]
public sealed class DerivedAttributeInvokeWorkflow : IWorkflow<DerivedAttributeInvokeData>
{
    public void Build(IWorkflowBuilder<DerivedAttributeInvokeData> builder) { }
}
