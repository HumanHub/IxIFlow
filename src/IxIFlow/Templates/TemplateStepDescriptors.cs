using IxIFlow.Core;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Templates;

/// <summary>The render step's catalog contract; Model is bound before concrete step emission.</summary>
public static class TemplateStepDescriptors
{
    // This output lives in the rendering assembly. Describe its portable identity without loading that assembly in a store.
    private const string RenderedAssembly = "IxIFlow.Templates";
    private const string RenderedType = "IxIFlow.Templates.RenderedTemplate";
    public static CatalogStepDescriptor RenderTemplate() => new("renderTemplate", "Render template", "Templates", "file-text",
        [new("Template", WorkflowTypeIdentity.SavedName(typeof(TemplateReference)), TemplateVersion.ModelTypeName(typeof(TemplateReference)), true, null),
         new("Model", null, null, true, "template.ModelType"),
         new("Culture", WorkflowTypeIdentity.SavedName(typeof(string)), "string?", false, null)],
        [new("Rendered", $"ixi-type:1:N{RenderedAssembly.Length}:{RenderedAssembly}{RenderedType.Length}:{RenderedType}0:", "IxIFlow.Templates.RenderedTemplate?", false, null)]);

    public static CatalogStepDescriptor Bind(TemplateVersion template) => RenderTemplate() with
    {
        Inputs = RenderTemplate().Inputs.Select(member => member.Name == "Model"
            ? member with { Type = template.ModelTypeId, TypeName = template.ModelType, TypeFrom = null } : member).ToArray()
    };
}
