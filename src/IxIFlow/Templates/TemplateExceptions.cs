namespace IxIFlow.Templates;

/// <summary>A template could not be published, found or rendered.</summary>
public abstract class TemplateException(string message, Exception? innerException = null) : Exception(message, innerException);

public sealed class TemplateNotFoundException(string message) : TemplateException(message);

/// <summary>Published content is immutable.</summary>
public sealed class TemplateVersionConflictException(TemplateReference template)
    : TemplateException($"Template {template} is already published with different content; publish a new version")
{
    public TemplateReference Template { get; } = template;
}
