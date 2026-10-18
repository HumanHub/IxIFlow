namespace IxIFlow.Dsl.Documents;

/// <summary>The source coordinates of a document node.</summary>
public readonly record struct DocumentLocation(int Line, int Column);

public abstract record DocumentNode
{
    public DocumentLocation Location { get; internal set; } = new(1, 1);
    public IReadOnlyDictionary<string, DocumentLocation> Members { get; internal set; } =
        new Dictionary<string, DocumentLocation>();
}
