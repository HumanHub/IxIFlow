namespace IxIFlow.Dsl.Documents;

/// <summary>The source coordinates of a document node.</summary>
public readonly record struct DocumentLocation(int Line, int Column);

public abstract record DocumentNode
{
    /// <summary>The node's source coordinates, supplied by a reader or host.</summary>
    public DocumentLocation Location { get; set; } = new(1, 1);
    /// <summary>Present source members and their coordinates. A nonempty root map enables
    /// omitted-member checks throughout the document; code-built documents use their values.</summary>
    public IReadOnlyDictionary<string, DocumentLocation> Members { get; set; } =
        new Dictionary<string, DocumentLocation>();
}
