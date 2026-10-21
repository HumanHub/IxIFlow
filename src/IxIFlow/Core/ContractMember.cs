namespace IxIFlow.Core;

/// <summary>A portable input or output, including its converted default and designer metadata.</summary>
public sealed record ContractMember(string Name, string Type, bool Required, bool HasDefault,
    string? DefaultJson, string? DisplayName, string? Description, string? UIHint);
