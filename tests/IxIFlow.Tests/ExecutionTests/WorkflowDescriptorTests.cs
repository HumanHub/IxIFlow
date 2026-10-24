using IxIFlow.Builders.Interfaces;
using IxIFlow.ActivitySdk;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using IxIFlow.Authoring;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Dsl.Compilation;
using IxIFlow.Dsl.Documents;
using IxIFlow.Extensions;
using IxIFlow.Generators;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "registration")]
[Trait("Spec", "F-14")]
[Trait("Spec", "F-18")]
public sealed class WorkflowDescriptorTests
{
    [Fact]
    public void DefinitionAttributeAndFluentContinuationProduceTypedPortablePresentation()
    {
        var attribute = ServiceCollectionExtensions.BuildWorkflowDefinition<Attributed, Data>(new());
        var descriptor = WorkflowDescriptor.Of(attribute);
        Assert.Equal("Attribute", descriptor.DisplayName);
        Assert.Equal("Help", descriptor.Description);
        Assert.Equal("folder", descriptor.Icon);
        Assert.Equal("Examples", descriptor.Category);
        Assert.Equal(new[] { "a", "z" }, descriptor.Tags);
        Assert.Equal(WorkflowTypeIdentity.SavedName(typeof(Data)), descriptor.DataType);
        Assert.Equal(WorkflowTypeIdentity.CSharpName(typeof(Data)), descriptor.DataTypeName);
        Assert.Equal(descriptor, WorkflowDescriptor.Parse(descriptor.ToJson()));

        var builder = Workflow.Create<Data>("Presentation", 1);
        builder.DisplayName("Root").Description("Root help").Tags("z", "a");
        var definition = builder.Step<Noop>().Delay(TimeSpan.FromMinutes(1)).DisplayName("Continuation").Icon("bolt").Category("Other").Build();
        var fluent = WorkflowDescriptor.Of(definition);
        Assert.Equal("Continuation", fluent.DisplayName);
        Assert.Equal("Root help", fluent.Description);
        Assert.Equal("bolt", fluent.Icon);
        Assert.Equal("Other", fluent.Category);
        Assert.Equal(new[] { "a", "z" }, fluent.Tags);
        definition.DisplayName = null;
        Assert.Equal(definition.Name, WorkflowDescriptor.Of(definition).DisplayName);
        Assert.False(definition.Metadata.ContainsKey("DisplayName"));
    }

    [Fact]
    public async Task YamlPublishesTypedPresentationAndBuildInspectionReturnsOnlyValueJson()
    {
        var yaml = """
            workflow:
              name: Presentation
              version: 1
              dataType: Data
              displayName: YAML presentation
              description: YAML help
              tags: [z, a]
              icon: file-text
              category: Documents
            definitions:
              - kind: delay
                duration: 00:01:00
            """;
        var options = YamlTestContracts.Create(("Data", typeof(Data)));
        var source = WorkflowSourceTranslator.Translate(yaml, "workflow.yaml", options);
        Assert.Contains("definition.DisplayName =", source);
        Assert.DoesNotContain("Metadata[\"DisplayName\"]", source);
        var compiled = WorkflowCompilation.Compile(source);
        Assert.True(compiled.Success, string.Join("; ", compiled.Diagnostics));
        var result = await WorkflowBuild.RunAsync(compiled, new Dictionary<string, byte[]> { [typeof(Data).Assembly.GetName().Name!] = File.ReadAllBytes(typeof(Data).Assembly.Location) }, TimeSpan.FromSeconds(30),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);
        Assert.Null(result.Error);
        var descriptor = WorkflowDescriptor.Parse(result.DescriptorJson);
        Assert.Equal("YAML presentation", descriptor.DisplayName);
        Assert.Equal("YAML help", descriptor.Description);
        Assert.Equal("file-text", descriptor.Icon);
        Assert.Equal("Documents", descriptor.Category);
        Assert.Equal(new[] { "a", "z" }, descriptor.Tags);
        Assert.Equal(WorkflowContract.OfType(typeof(Data)).ToJson(), result.ContractJson);
    }

    [Theory]
    [InlineData("<svg>")]
    [InlineData("")]
    [InlineData("Folder")]
    [InlineData("folder_name")]
    [InlineData("folder\n")]
    public async Task InvalidIconHasSameDiagnosticForDefinitionAttributeBuilderAndYaml(string icon)
    {
        var definition = Workflow.Create<Data>("Invalid").Icon(icon).Build();
        Assert.Contains("WF007", Assert.Throws<WorkflowEngineException>(() => WorkflowDescriptor.Of(definition)).Message);
        var attribute = ServiceCollectionExtensions.BuildWorkflowDefinition<InvalidAttribute, Data>(new());
        Assert.Contains("WF007", Assert.Throws<WorkflowEngineException>(() => WorkflowDescriptor.Of(attribute)).Message);
        var document = new WorkflowDocument { Workflow = new() { Name = "Invalid", DataType = "Data", Icon = icon } };
        var validation = await new WorkflowDocumentValidator().ValidateAsync(document, new WorkflowSourceContracts(YamlTestContracts.Create(("Data", typeof(Data)))));
        Assert.Contains(validation.Diagnostics, diagnostic => diagnostic.Code == "WF007" && diagnostic.Path == "workflow.icon");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t")]
    public async Task BlankCategoryIsRejectedAcrossSources(string category)
    {
        var definition = Workflow.Create<Data>("Invalid").Category(category).Build();
        Assert.Contains("WF008", Assert.Throws<WorkflowEngineException>(() => WorkflowDescriptor.Of(definition)).Message);
        var attribute = ServiceCollectionExtensions.BuildWorkflowDefinition<InvalidCategoryAttribute, Data>(new());
        Assert.Contains("WF008", Assert.Throws<WorkflowEngineException>(() => WorkflowDescriptor.Of(attribute)).Message);
        var document = new WorkflowDocument { Workflow = new() { Name = "Invalid", DataType = "Data", Category = category } };
        var validation = await new WorkflowDocumentValidator().ValidateAsync(document, new WorkflowSourceContracts(YamlTestContracts.Create(("Data", typeof(Data)))));
        Assert.Contains(validation.Diagnostics, diagnostic => diagnostic.Code == "WF008");
    }

    [Fact]
    public void BoundsPortableNamesAndRequiredVersionJsonAreValidated()
    {
        var descriptor = WorkflowDescriptor.Of(Workflow.Create<Data>("Bounds").Build());
        foreach (var bad in new[]
        {
            descriptor with { DisplayName = " " }, descriptor with { DisplayName = new('x', 201) },
            descriptor with { Description = new('x', 4001) }, descriptor with { Category = new('x', 101) },
            descriptor with { Icon = new('x', 101) }, descriptor with { Tags = ["bad tag"] },
            descriptor with { Tags = [new('x', 51)] }, descriptor with { Tags = Enumerable.Range(0, 9).Select(i => new string('x', 49) + i).ToArray() },
            descriptor with { DataType = "System.Int32" }, descriptor with { DataTypeName = "System.Int32" },
            descriptor with { Tags = ["z", "a"] }, descriptor with { Tags = ["a", "a"] }
        }) Assert.Throws<WorkflowEngineException>(() => WorkflowDescriptor.Validate(bad));
        var contract = WorkflowContract.OfType(typeof(Data));
        var version = new WorkflowVersion("Bounds", 1, "structure", contract.ToJson(), contract.Hash, descriptor, []);
        Assert.Equal(version, JsonSerializer.Deserialize<WorkflowVersion>(JsonSerializer.Serialize(version)));
        Assert.False(typeof(WorkflowVersion).GetConstructors().SelectMany(constructor => constructor.GetParameters())
            .Single(parameter => parameter.Name == "Descriptor").HasDefaultValue);
        var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(version))!.AsObject();
        json.Remove("Descriptor");
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<WorkflowVersion>(json.ToJsonString()));
        Assert.Throws<ArgumentNullException>(() => WorkflowDescriptor.Validate(null!));
        Assert.Equal(descriptor, version.Descriptor);
    }

    [Fact]
    public void WorkflowPresentationIsOutsideStructureAndMemberLabelsRemainInContractHash()
    {
        var definition = Workflow.Create<Data>("Hash").Delay(TimeSpan.FromMinutes(1)).Build();
        var structure = WorkflowDefinitionValidator.Inspect(definition);
        var contract = WorkflowDefinitionValidator.GetContract(definition);
        definition.DisplayName = "Other"; definition.Description = "Other help"; definition.Tags = ["tag"];
        definition.Icon = "bolt"; definition.Category = "Other category";
        Assert.Equal(structure, WorkflowDefinitionValidator.Inspect(definition));
        Assert.Equal(contract.Hash, WorkflowDefinitionValidator.GetContract(definition).Hash);
        var fresh = Workflow.Create<Data>("Hash").DisplayName("Other").Description("Other help").Tags("tag")
            .Icon("bolt").Category("Other category").Delay(TimeSpan.FromMinutes(1)).Build();
        Assert.Equal(structure, WorkflowDefinitionValidator.Inspect(fresh));
        Assert.Equal(contract.Hash, WorkflowDefinitionValidator.GetContract(fresh).Hash);
        var member = Assert.Single(contract.Inputs);
        Assert.Equal("Value label", member.DisplayName);
        Assert.NotEqual(contract.Hash, new WorkflowContract([member with { DisplayName = "Changed label" }], contract.Outputs, contract.Declared).Hash);
        Assert.Equal(contract.ToJson(), WorkflowContract.Parse(contract.ToJson()).ToJson());
    }

    [Workflow("Attributed", DisplayName = "Attribute", Description = "Help", Tags = new[] { "z", "a" }, Icon = "folder", Category = "Examples")]
    public sealed class Attributed : IWorkflow<Data>
    { public void Build(IxIFlow.Builders.Interfaces.IWorkflowBuilder<Data> builder) => builder.Delay(TimeSpan.FromMinutes(1)); }
    [Workflow("InvalidAttribute", Icon = "<svg>")]
    public sealed class InvalidAttribute : IWorkflow<Data>
    { public void Build(IxIFlow.Builders.Interfaces.IWorkflowBuilder<Data> builder) { } }
    [Workflow("InvalidCategoryAttribute", Category = " ")]
    public sealed class InvalidCategoryAttribute : IWorkflow<Data>
    { public void Build(IWorkflowBuilder<Data> builder) { } }
    public sealed class Noop : IAsyncActivity
    { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask; }
    public sealed class Data
    {
        [WorkflowInput, Display(Name = "Value label", Description = "Member help")]
        public int Value { get; set; }
    }
}
