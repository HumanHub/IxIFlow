namespace IxIFlow.Core;

/// <summary>The exact identity of a workflow body composed into a caller.</summary>
public sealed record WorkflowInlinePin(string Name, int Version, string StructureHash, string ContractHash);
