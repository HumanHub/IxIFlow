namespace IxIFlow.Core;

/// <summary>A contract member, with its saved type identity and converted JSON default.</summary>
public sealed record ContractMember(string Name, string Type, bool Required, bool HasDefault, string? DefaultJson,
    string? DisplayName, string? Description, string? UIHint);
