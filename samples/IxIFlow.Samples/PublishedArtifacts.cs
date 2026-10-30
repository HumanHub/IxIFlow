using IxIFlow.Authoring;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Templates;
using IxIFlow.Templates.Liquid;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Samples;

public static class PublishedArtifacts
{
    // snippet: template-yaml
    public const string TemplateYaml = """
                                       template:
                                         key: samples.greeting
                                         version: 1
                                         engine: liquid
                                         format: text
                                         model: IxIFlow.Samples.GreetingModel
                                       content: "Hello {{ Name }}"
                                       cultures:
                                         fr: "Bonjour {{ Name }}"
                                       """;

    // end-snippet: template-yaml

    // snippet: bundled-render-yaml
    public const string RenderYaml = """
                                     workflow: {name: BundledGreeting, dataType: IxIFlow.Samples.GreetingData}
                                     definitions:
                                       - kind: renderTemplate
                                         id: greeting
                                         template: samples.greeting
                                         model: {path: workflow.Model}
                                         culture: {kind: constant, value: fr-CA}
                                         output: [{source: Rendered, to: {path: workflow.Body}}]
                                     """;

    // end-snippet: bundled-render-yaml

    // snippet: inline-yaml
    public const string InlineYaml = """
                                     workflow: {name: InlineDouble, dataType: IxIFlow.Samples.PublishedNumber}
                                     imports: {workflows: [NumberBody@1]}
                                     definitions:
                                       - kind: use
                                         id: published
                                         workflow: {name: NumberBody, version: 1}
                                         input: [{target: Value, from: {path: workflow.Value}}]
                                         output: [{source: Value, to: {path: workflow.Value}}]
                                       - kind: useType
                                         id: typed
                                         workflowType: IxIFlow.Samples.DoubleNumber
                                         input: [{target: Value, from: {path: workflow.Value}}]
                                         output: [{source: Value, to: {path: workflow.Value}}]
                                     """;

    // end-snippet: inline-yaml

    public static WorkflowPublisher Publisher(IWorkflowStateRepository store, ILogger<WorkflowPublisher>? logger = null) =>
        new(store, store, logger: logger, templateEngines: new TemplateEngines([new LiquidTemplateEngine()]));

    public static async Task<WorkflowVersion> PublishGreetingAsync(IWorkflowStateRepository store)
    {
        var publisher = Publisher(store);
        var template = await store.SaveDefinitionAsync(new("samples.greeting", 1, "template", TemplateYaml), null);
        PublishedActivities.Require(await publisher.PublishAsync(template.Id, "sample-publisher"));

        return await PublishWorkflowAsync(store, publisher, "BundledGreeting", RenderYaml);
    }

    public static async Task<WorkflowVersion> PublishInlineAsync(IWorkflowStateRepository store, ILogger<WorkflowPublisher>? logger = null)
    {
        var publisher = Publisher(store, logger);

        // Typed callees are code-defined catalog versions, registered before publication.
        using var catalog = new WorkflowCatalog(store);
        var typed = Workflow.Create<PublishedNumber>("DoubleNumber");
        new DoubleNumber().Build(typed);
        await catalog.RegisterWorkflowAsync(typed.Build());

        await PublishWorkflowAsync(store, publisher, "NumberBody", """
                                                                   workflow: {name: NumberBody, dataType: IxIFlow.Samples.PublishedNumber, input: [Value], output: [Value]}
                                                                   definitions:
                                                                     - kind: assign
                                                                       to: {path: workflow.Value}
                                                                       value:
                                                                         kind: binary
                                                                         operator: add
                                                                         left: {path: workflow.Value}
                                                                         right: {kind: constant, value: 1}
                                                                   """);

        return await PublishWorkflowAsync(store, publisher, "InlineDouble", InlineYaml);
    }

    private static async Task<WorkflowVersion> PublishWorkflowAsync(IWorkflowStateRepository store,
        WorkflowPublisher publisher, string name, string yaml)
    {
        var draft = await store.SaveDefinitionAsync(new(name, 1, "workflow", yaml), null);

        return PublishedActivities.Require(await publisher.PublishAsync(draft.Id, "sample-publisher")).Workflow!;
    }
}

public sealed class GreetingModel
{
    public string Name { get; set; } = "";
}

public sealed class GreetingData
{
    public GreetingModel Model { get; set; } = new();
    public RenderedTemplate? Body { get; set; }
}

[Workflow("DoubleNumber")]
public sealed class DoubleNumber : IWorkflow<PublishedNumber>
{
    public void Build(IWorkflowBuilder<PublishedNumber> builder) =>
        builder.Input(data => data.Value).Output(data => data.Value)
            .Assign(ctx => ctx.WorkflowData.Value, ctx => ctx.WorkflowData.Value * 2);
}