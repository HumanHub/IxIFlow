namespace IxIFlow.Templates;

/// <summary>A template version failed its engine's validation when it was published.</summary>
public sealed class TemplateValidationException : TemplateException
{
    public TemplateValidationException(TemplateSource template, IReadOnlyList<TemplateDiagnostic> diagnostics)
        : base($"Template {template.Name} is not valid: " + string.Join("; ", diagnostics))
    {
        Template = template;
        Diagnostics = diagnostics;
    }

    public TemplateSource Template { get; }

    public IReadOnlyList<TemplateDiagnostic> Diagnostics { get; }
}

/// <summary>An engine could not render a template against the model it was given.</summary>
public sealed class TemplateRenderException(TemplateSource template, string reason, Exception? innerException = null)
    : TemplateException($"Template {template.Name} could not be rendered: {reason}", innerException)
{
    public TemplateSource Template { get; } = template;
}
