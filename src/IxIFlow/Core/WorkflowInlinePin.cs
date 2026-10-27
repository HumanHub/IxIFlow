namespace IxIFlow.Core;

/// <summary>The exact identity of a workflow body composed into a caller.</summary>
public sealed record WorkflowInlinePin(string Name, int Version, string StructureHash, string ContractHash)
{
    public ArtifactDigest? BundleDigest { get; init; }
    public string? EntryType { get; init; }
    public string? AssemblyName { get; init; }
}

/// <summary>A flattened inline recipe; sites use the caller's runtime catalog addresses.</summary>
public sealed record WorkflowInlineWorkflow(string Name, int Version, ArtifactDigest? BundleDigest,
    string StructureHash, string ContractHash, string EntryType, string AssemblyName, IReadOnlyList<string> Sites);

/// <summary>Stable executable and source paths, independent of source text and file location.</summary>
public static class WorkflowBundleIdentity
{
    public static string AssemblyName(string name, int version) =>
        "IxIFlow.Workflow." + Uri.EscapeDataString(name) + ".v" + version.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static string ClassName(string name, int version) =>
        "Workflow_" + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(name)) + "_v" + version.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static string InlineSourceDirectory(string name, int version) =>
        "inline/" + Uri.EscapeDataString(name) + "@" + version.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/";
}
