using IxIFlow.Core;

namespace IxIFlow.Samples;

public static class CatalogExamples
{
    // snippet: template-reads
    public static async Task<(CatalogPage<CatalogTemplate> Templates, IReadOnlyList<CatalogStepDescriptor> Steps)>
        ReadAsync(IWorkflowCatalogStore catalog, CatalogScope scope)
    {
        var templates = await catalog.ListTemplatesAsync(new CatalogTemplateQuery(scope, Engine: "liquid"));
        var steps = await catalog.ListStepDescriptorsAsync(scope);
        return (templates, steps);
    }
    // end-snippet: template-reads

    // snippet: render-yaml
    public const string RenderYaml = """
        workflow: {name: CatalogRenderSample, dataType: IxIFlow.Samples.MessageData}
        definitions:
          - kind: renderTemplate
            id: render
            template: quote@1
            modelType: IxIFlow.Samples.QuoteData
            model: {path: workflow.Quote}
            culture: {kind: constant, value: fr-CA}
            output: [{source: Rendered, to: {path: workflow.Body}}]
        """;
    // end-snippet: render-yaml
}
