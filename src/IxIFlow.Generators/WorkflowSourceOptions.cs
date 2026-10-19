using IxIFlow.Dsl.Compilation;

namespace IxIFlow.Generators;

/// <summary>Names resolved by the authoring host before translation.</summary>
public sealed class WorkflowSourceOptions
{
    public string Namespace { get; set; } = "IxIFlow.Generated";
    public string? ClassName { get; set; }
    /// <summary>Require every referenced type to have a host-resolved contract.
    /// Without this option, type names are not resolved, so STEP003, STEP009 and WF003 cannot be reported.
    /// </summary>
    public bool RequireResolvedTypes { get; set; }
    /// <summary>Resolved activity versions and their declaring package identities.</summary>
    public IDictionary<string, ActivityDocumentContract> Activities { get; } =
        new Dictionary<string, ActivityDocumentContract>(StringComparer.Ordinal);
    /// <summary>Package or workflow imports resolved and pinned by the publishing host.</summary>
    public ISet<string> ResolvedImports { get; } = new HashSet<string>(StringComparer.Ordinal);
    public IDictionary<string, string> Types { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    /// <summary>The data type inferred from a workflow type's IWorkflow&lt;T&gt; contract.</summary>
    public IDictionary<string, string> WorkflowDataTypes { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    /// <summary>Class identities read from WorkflowAttribute by the compiling host.</summary>
    public IDictionary<string, WorkflowClassIdentity> WorkflowIdentities { get; } =
        new Dictionary<string, WorkflowClassIdentity>(StringComparer.Ordinal);
    public IDictionary<string, string> Members { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    public IDictionary<string, WorkflowSourceReference> Workflows { get; } =
        new Dictionary<string, WorkflowSourceReference>(StringComparer.Ordinal);
}

/// <summary>The identity declared on a workflow CLR class.</summary>
public sealed class WorkflowClassIdentity(string name, int version)
{
    public string Name { get; } = name;
    public int Version { get; } = version;
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
