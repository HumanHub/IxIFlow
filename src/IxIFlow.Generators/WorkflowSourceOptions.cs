namespace IxIFlow.Generators;

/// <summary>Names resolved by the authoring host before translation.</summary>
public sealed class WorkflowSourceOptions
{
    public string Namespace { get; set; } = "IxIFlow.Generated";
    public string? ClassName { get; set; }
    public IDictionary<string, string> ActivityVersions { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    /// <summary>Package or workflow imports resolved and pinned by the publishing host.</summary>
    public ISet<string> ResolvedImports { get; } = new HashSet<string>(StringComparer.Ordinal);
    public IDictionary<string, string> Types { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public IDictionary<string, string> Members { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public IDictionary<string, WorkflowSourceReference> Workflows { get; } =
        new Dictionary<string, WorkflowSourceReference>(StringComparer.Ordinal);
}

/// <summary>A pinned child workflow and its data contract.</summary>
public sealed class WorkflowSourceReference(string dataType, int version)
{
    public string DataType { get; } = dataType;
    public int Version { get; } = version;
}

/// <summary>A translation error at a YAML location.</summary>
public sealed class WorkflowSourceException(string message, string path, int line, int column)
    : Exception($"{path}({line},{column}): {message}")
{
    public string Detail { get; } = message;
    public string Path { get; } = path;
    public int Line { get; } = line;
    public int Column { get; } = column;
}
