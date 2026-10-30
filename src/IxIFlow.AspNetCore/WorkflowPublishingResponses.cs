using IxIFlow.Core;

namespace IxIFlow.AspNetCore;

public sealed record WorkflowDiagnosticResponse(
    string Stage,
    string Code,
    string Class,
    string Severity,
    string Message,
    string Path,
    int Line,
    int Column);

public sealed record WorkflowValidationResponse(string Kind, bool IsValid, IReadOnlyList<WorkflowDiagnosticResponse> Diagnostics);

public sealed record WorkflowPublicationSummary(Guid Id, string State, int Attempt, DateTime StartedAtUtc);

public sealed record WorkflowDraftResponse(WorkflowDraftSummary Draft, WorkflowPublicationSummary? Publication);

public sealed record WorkflowPublicationResponse(
    Guid Id,
    WorkflowDraftId Draft,
    int Attempt,
    string State,
    WorkflowVersionDetailResponse? Workflow,
    PackageDetailResponse? Package,
    CatalogTemplateDetail? Template,
    IReadOnlyList<WorkflowDiagnosticResponse> Diagnostics);

public sealed record WorkflowPackageResponse(PackageDetailResponse Package, bool Created);

public sealed record PackageWithdrawalResponse(PackageDetailResponse Package, PackageHolders Holders);

public sealed record TemplatePreviewResponse(string? Content, string? Format, IReadOnlyList<WorkflowDiagnosticResponse> Diagnostics);

public sealed record NuGetVersionsResponse(IReadOnlyList<string> Versions);

public sealed record NuGetServiceResourceResponse(
    [property: System.Text.Json.Serialization.JsonPropertyName("@id")]
    string Id,
    [property: System.Text.Json.Serialization.JsonPropertyName("@type")]
    string Type);

public sealed record NuGetServiceIndexResponse(string Version, IReadOnlyList<NuGetServiceResourceResponse> Resources);

// Web projections own every enum-bearing member. They do not depend on host JSON options.
public sealed record WorkflowVersionResponse(
    string Name,
    int Version,
    string State,
    string StructureHash,
    ArtifactDigest? ArtifactDigest,
    string ContractHash,
    WorkflowDescriptor Descriptor,
    long LifecycleRevision);

public sealed record WorkflowDependencyResponse(string Name, int Version, string Kind);

public sealed record WorkflowVersionDetailResponse(
    WorkflowVersionResponse Summary,
    WorkflowContract Contract,
    IReadOnlyList<WorkflowDependencyResponse> Dependencies,
    IReadOnlyList<WorkflowPackageReference> ActivityPackages,
    bool ArtifactPresent,
    WorkflowDraftId? PublishedDraft,
    DateTime? PublishedAtUtc,
    string? PublishedBy);

public sealed record ActivityProviderResponse(string PackageId, string PackageVersion, string Origin, string Build);

public sealed record ActivityScopeResponse(string ValueType, string ValueTypeName, string Concurrency, string Threading);

public sealed record ActivityDescriptorResponse(
    string Key,
    string Version,
    string Name,
    string Category,
    string? Description,
    string? Icon,
    string? Designer,
    IReadOnlyList<ContractMember> Inputs,
    IReadOnlyList<ContractMember> Outputs,
    string PackageId,
    string PackageVersion,
    string MetadataTypeName,
    string CSharpTypeName,
    ActivityScopeResponse? Scope,
    IReadOnlyList<ScopeRequirement> RequiresScopes);

public sealed record ActivityResponse(
    ActivityDescriptorResponse Descriptor,
    ActivityProviderResponse Provider,
    bool Withdrawn,
    bool ArtifactPresent);

public sealed record ActivityDetailResponse(ActivityResponse Activity, IReadOnlyList<CatalogHost> Hosts);

public sealed record PackageResponse(
    string Id,
    string Version,
    ArtifactDigest Digest,
    string Origin,
    string Extraction,
    string? ExtractionDetail,
    int ActivityCount,
    bool Withdrawn,
    bool ArtifactPresent,
    long LifecycleRevision);

public sealed record PackageActivitiesResponse(
    IReadOnlyList<ActivityDescriptorResponse> Activities,
    string Extraction,
    string? Detail,
    IReadOnlyList<WorkflowPackageReference>? Closure,
    IReadOnlyList<string> MissingPackageIds);

public sealed record PackageDetailResponse(
    PackageResponse Summary,
    PackageActivitiesResponse Activities,
    WorkflowDraftId? PublishedDraft,
    DateTime RegisteredAtUtc,
    string? RegisteredBy,
    DateTime? WithdrawnAtUtc,
    string? WithdrawnBy);

internal static class PublishingResponses
{
    internal static WorkflowVersionResponse Project(CatalogWorkflowVersion row) => new(row.Name, row.Version, row.State.ToString(),
        row.StructureHash, row.ArtifactDigest, row.ContractHash, row.Descriptor, row.LifecycleRevision);

    internal static WorkflowVersionDetailResponse Project(CatalogWorkflowVersionDetail row) => new(Project(row.Summary), row.Contract,
        row.Dependencies.Select(item => new WorkflowDependencyResponse(item.Name, item.Version, item.Kind.ToString())).ToArray(),
        row.ActivityPackages, row.ArtifactPresent, row.PublishedDraft, row.PublishedAtUtc, row.PublishedBy);

    internal static ActivityDescriptorResponse Project(ActivityDescriptor row) => new(row.Key, row.Version, row.Name, row.Category,
        row.Description, row.Icon, row.Designer, row.Inputs, row.Outputs, row.PackageId, row.PackageVersion, row.MetadataTypeName,
        row.CSharpTypeName,
        row.Scope == null
            ? null
            : new(row.Scope.ValueType, row.Scope.ValueTypeName, row.Scope.Concurrency.ToString(), row.Scope.Threading.ToString()),
        row.RequiresScopes);

    internal static ActivityResponse Project(CatalogActivity row) => new(Project(row.Descriptor),
        new(row.Provider.PackageId, row.Provider.PackageVersion, row.Provider.Origin.ToString(), row.Provider.Build), row.Withdrawn,
        row.ArtifactPresent);

    internal static PackageResponse Project(CatalogPackage row) => new(row.Id, row.Version, row.Digest, row.Origin.ToString(),
        row.Extraction.ToString(),
        row.ExtractionDetail, row.ActivityCount, row.Withdrawn, row.ArtifactPresent, row.LifecycleRevision);

    internal static PackageDetailResponse Project(CatalogPackageDetail row) => new(Project(row.Summary),
        new(row.Activities.Activities.Select(Project).ToArray(), row.Activities.Extraction.ToString(), row.Activities.Detail,
            row.Activities.Closure, row.Activities.MissingPackageIds),
        row.PublishedDraft, row.RegisteredAtUtc, row.RegisteredBy, row.WithdrawnAtUtc, row.WithdrawnBy);

    internal static WorkflowDiagnosticResponse Project(WorkflowPublicationDiagnostic row) =>
        new(row.Stage, row.Code, row.Class.ToString(), row.Severity, row.Message, row.Path, row.Line, row.Column);

    internal static WorkflowDiagnosticResponse Project(IxIFlow.Authoring.WorkflowPublishDiagnostic row) =>
        new(row.Stage.ToString(), row.Code, row.Class.ToString(), row.Severity.ToString(), row.Message, row.Path, row.Line, row.Column);
}