namespace IxIFlow.Templates;

/// <summary>A problem an engine found in a template, with its position in the template's text when known.</summary>
public sealed record TemplateDiagnostic(string Message, int? Line = null, int? Column = null)
{
    public override string ToString() => Line is { } line
        ? $"({line}:{Column ?? 1}) {Message}"
        : Message;
}
