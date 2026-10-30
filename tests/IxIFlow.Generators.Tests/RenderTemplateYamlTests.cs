using IxIFlow.Authoring;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Dsl.Compilation;
using IxIFlow.Dsl.Documents;
using IxIFlow.Templates;

namespace IxIFlow.Generators.Tests;

[Trait("Feature", "templates"), Trait("Spec", "F-16"), Trait("Spec", "F-25")]
public sealed class RenderTemplateYamlTests
{
    [Fact]
    public void BuildTimeFallbackHasTheFluentStructureAndContract()
    {
        var yaml = Generated.Workflow_render_template.Build();

        var fluent = Workflow.Create<RenderYamlData>("RenderYaml")
            .Step<RenderTemplate<RenderYamlModel>>(setup => setup
                .Input(activity => activity.Template).From(_ => new TemplateReference("notice", 1))
                .Input(activity => activity.Model).From(ctx => ctx.WorkflowData.Model)
                .Input(activity => activity.Culture).From(ctx => ctx.WorkflowData.Culture)
                .Output(activity => activity.Rendered).To(ctx => ctx.WorkflowData.Body))
            .WithId("render").Build();

        Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(yaml));
        Assert.Equal(WorkflowDefinitionValidator.GetContract(fluent).Hash, WorkflowDefinitionValidator.GetContract(yaml).Hash);
    }

    [Theory]
    [InlineData("notice", null, "TPL010")]
    [InlineData("notice@1", null, "TPL010")]
    [InlineData("notice@x", "Model", "TPL010")]
    [InlineData("notice@1", "Wrong", "TPL011")]
    public async Task MissingPinsAndExplicitModelMismatchAreCurated(string reference, string? model, string code)
    {
        var options = new WorkflowSourceOptions();

        if (code == "TPL011")
            options.Templates["notice@1"] = new("notice", 1, "Model");

        var document = WorkflowYamlSerializer.Read(
            "workflow: {name: Test, dataType: Data}\ndefinitions: [{kind: renderTemplate, template: '" + reference +
            "', model: {path: workflow.Model}" + (model == null ? "" : ", modelType: " + model) + "}]");

        var result = await new WorkflowDocumentValidator().ValidateAsync(document, new WorkflowSourceContracts(options));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code);
    }

    [Fact]
    public async Task CompensationIsRefusedAndDescriptorBindsTheDeclaredModel()
    {
        var document =
            WorkflowYamlSerializer.Read(
                "workflow: {name: Test, dataType: Data}\ndefinitions: [{kind: renderTemplate, template: notice@1, modelType: Model, model: {path: workflow.Model}, compensation: [{activity: Undo}]}]");

        var result = await new WorkflowDocumentValidator().ValidateAsync(document, new WorkflowSourceContracts(new()));
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "TPL009");
        var descriptor = TemplateStepDescriptors.RenderTemplate();

        Assert.Equal(("renderTemplate", "Render template", "Templates", "file-text"),
            (descriptor.Kind, descriptor.DisplayName, descriptor.Category, descriptor.Icon));

        var model = Assert.Single(descriptor.Inputs, member => member.Name == "Model");
        Assert.True(model.Required);
        Assert.Null(model.Type);
        Assert.Null(model.TypeName);
        Assert.Equal("template.ModelType", model.TypeFrom);
        Assert.Equal("string?", descriptor.Inputs.Single(member => member.Name == "Culture").TypeName);

        var version = TemplateVersion.Create<RenderYamlModel>("notice", 1, "liquid", TemplateFormat.Text,
            new TemplateVariant("", "{{ Value }}"));

        var bound = TemplateStepDescriptors.Bind(version).Inputs.Single(member => member.Name == "Model");
        Assert.Equal(version.ModelTypeId, bound.Type);
        Assert.Equal(version.ModelType, bound.TypeName);
        Assert.Null(bound.TypeFrom);
    }
}

public sealed class RenderYamlData
{
    public RenderYamlModel Model { get; set; } = new();
    public string? Culture { get; set; }
    public RenderedTemplate? Body { get; set; }
}

public sealed class RenderYamlModel
{
    public string Value { get; set; } = "";
}