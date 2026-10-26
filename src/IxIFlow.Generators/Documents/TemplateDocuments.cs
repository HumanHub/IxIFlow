using System.ComponentModel;

namespace IxIFlow.Dsl.Documents;

[Description("A versioned template and its culture variants.")]
public sealed record TemplateDocument : DocumentNode
{
    [Description("The document contract version.")]
    [DocumentValues("1.0")]
    public string SchemaVersion { get; init; } = "1.0";
    [Description("The template identity, engine and declared model.")]
    [DocumentRequired]
    public TemplateHeaderDocument Template { get; init; } = new();
    [Description("Package dependencies for the model and its type arguments.")]
    public WorkflowImportsDocument Imports { get; init; } = new();
    [Description("The base variant's Liquid text.")]
    [DocumentRequired]
    public string Content { get; init; } = string.Empty;
    [Description("Liquid text keyed by culture name.")]
    public Dictionary<string, string> Cultures { get; init; } = new();
}

[Description("The identity and declared read surface of a template.")]
public sealed record TemplateHeaderDocument : DocumentNode
{
    [Description("The lowercase package-style template key.")]
    [DocumentRequired]
    public string Key { get; init; } = string.Empty;
    [Description("The positive integer template version.")]
    [DocumentRequired]
    public int Version { get; init; }
    [Description("The template engine name: liquid.")]
    [DocumentRequired]
    public string Engine { get; init; } = string.Empty;
    [Description("The output format: text or html.")]
    [DocumentRequired]
    [DocumentValues("text", "html")]
    public string Format { get; init; } = string.Empty;
    [Description("The full C# model type expression, including generic arguments.")]
    [DocumentRequired]
    public string Model { get; init; } = string.Empty;
    [Description("The template display name.")]
    public string? DisplayName { get; init; }
    [Description("The template description.")]
    public string? Description { get; init; }
}
