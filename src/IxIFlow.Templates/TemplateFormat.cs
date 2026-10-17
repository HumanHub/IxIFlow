namespace IxIFlow.Templates;

/// <summary>What a template produces, which decides how the values it writes are encoded.</summary>
public enum TemplateFormat
{
    /// <summary>Plain text, such as a subject line or an SMS: values are written as they are.</summary>
    Text,

    /// <summary>HTML: values are HTML-encoded unless the template writes them raw on purpose.</summary>
    Html
}
