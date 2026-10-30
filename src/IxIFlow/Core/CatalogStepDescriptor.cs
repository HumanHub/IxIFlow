namespace IxIFlow.Core;

public sealed record StepMemberDescriptor(string Name, string? Type, string? TypeName, bool Required, string? TypeFrom);

public sealed record CatalogStepDescriptor(
    string Kind,
    string DisplayName,
    string Category,
    string? Icon,
    IReadOnlyList<StepMemberDescriptor> Inputs,
    IReadOnlyList<StepMemberDescriptor> Outputs);