namespace IxIFlow.Templates;

/// <summary>A template could not be published, found or rendered.</summary>
public abstract class TemplateException : Exception
{
    protected TemplateException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>A template version failed its engine's validation when it was published (H-03).</summary>
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

/// <summary>
/// A template version, or an engine it needs, does not exist here, or the version was written against another model
/// than the step renders it with.
/// </summary>
public sealed class TemplateNotFoundException(string message) : TemplateException(message);

/// <summary>A version was published again with different content: published versions are immutable (H-01).</summary>
public sealed class TemplateVersionConflictException(TemplateReference template)
    : TemplateException($"Template {template} is already published with different content; publish a new version")
{
    public TemplateReference Template { get; } = template;
}

/// <summary>An engine could not render a template against the model it was given.</summary>
public sealed class TemplateRenderException(TemplateSource template, string reason, Exception? innerException = null)
    : TemplateException($"Template {template.Name} could not be rendered: {reason}", innerException)
{
    public TemplateSource Template { get; } = template;
}
