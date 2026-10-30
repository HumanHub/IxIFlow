using System.Text.Json;

namespace IxIFlow.Core;

public static class WorkflowDraftKinds
{
    public const string Workflow = "workflow", Activity = "activity", Template = "template";
}

public sealed record WorkflowDraftId(string Kind, string Name, int Version, Guid Generation, long Revision);

public sealed record WorkflowDraftSummary(
    string Kind,
    string Name,
    int Version,
    Guid Generation,
    long Revision,
    int SourceLength,
    DateTime UpdatedAtUtc,
    string? UpdatedBy);

/// <summary>Draft pages sort by kind, Unicode scalar value sequence of name, then version.</summary>
public sealed record WorkflowDraftQuery(
    CatalogScope Scope,
    string? Kind = null,
    string? NamePrefix = null,
    string? After = null,
    int Limit = 100)
{
    public string? Kind
    {
        get;
        init => field = WorkflowCatalogRules.ValidateText(value);
    } = WorkflowCatalogRules.ValidateText(Kind);

    public string? NamePrefix
    {
        get;
        init => field = WorkflowCatalogRules.ValidateText(value);
    } = WorkflowCatalogRules.ValidateText(NamePrefix);
}

public sealed record CatalogMutationToken(long Revision);

public sealed class CatalogPreconditionException(string message) : Exception(message);

public sealed record RetirementBlockers(
    IReadOnlyList<WorkflowVersionReference> Callers,
    WorkflowVersionReference? UnfinishedCaller,
    bool OutsideScope);

public sealed record WorkflowPublicationReceipt(WorkflowDraftId Draft, string ReceiptJson);

public enum WorkflowPublicationState
{
    Running,
    Published,
    Failed,
    Abandoned
}

public enum WorkflowPublishDiagnosticClass
{
    Authoring,
    Conflict,
    Unavailable,
    Limit,
    Internal
}

public sealed record WorkflowPublicationDiagnostic(
    string Stage,
    string Code,
    string Message,
    string Severity,
    string Path,
    int Line,
    int Column,
    WorkflowPublishDiagnosticClass Class);

public sealed record WorkflowPublicationOutcome(
    WorkflowPublicationReceipt? Receipt,
    IReadOnlyList<WorkflowPublicationDiagnostic> Diagnostics,
    WorkflowPublishDiagnosticClass? FailureClass);

public sealed record WorkflowPublication(
    Guid Id,
    WorkflowDraftId Draft,
    int Attempt,
    WorkflowPublicationState State,
    Guid HostId,
    string? StartedBy,
    DateTime StartedAtUtc,
    DateTime HeartbeatAtUtc,
    DateTime? FinishedAtUtc,
    WorkflowPublicationOutcome? Outcome);

public sealed record WorkflowPublicationQuery(
    CatalogScope Scope,
    string Kind,
    string Name,
    int Version,
    Guid? Generation = null,
    string? After = null,
    int Limit = 100)
{
    public string Kind
    {
        get;
        init => field = WorkflowCatalogRules.ValidateText(value)!;
    } = WorkflowCatalogRules.ValidateText(Kind)!;

    public string Name
    {
        get;
        init => field = WorkflowCatalogRules.ValidateText(value)!;
    } = WorkflowCatalogRules.ValidateText(Name)!;
}

internal static class WorkflowPublicationRules
{
    internal static void Query(WorkflowDraftQuery query)
    {
        ArgumentNullException.ThrowIfNull(query.Scope);

        if (query.Kind != null)
            Key(query.Kind, "key", 1);

        if (query.NamePrefix != null)
            WorkflowCatalogRules.ValidateText(query.NamePrefix);
    }

    internal static void Key(string kind, string name, int version)
    {
        if (kind is not (WorkflowDraftKinds.Workflow or WorkflowDraftKinds.Activity or WorkflowDraftKinds.Template))
            throw new ArgumentException("Unknown draft kind", nameof(kind));

        WorkflowCatalogRules.ValidateName(name);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(name.Length, kind == WorkflowDraftKinds.Workflow ? 200 : 100);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);

        if (kind == WorkflowDraftKinds.Activity)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(version, 65534);

            if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"\A[a-z][a-z0-9]*([.-][a-z][a-z0-9]*)*\z",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                throw new ArgumentException("Invalid activity draft key", nameof(name));
        }

        if (kind == WorkflowDraftKinds.Template && (!WorkflowPackageRules.ValidId(name) || name != name.ToLowerInvariant()))
            throw new ArgumentException("Invalid template draft key", nameof(name));
    }

    internal static void Id(WorkflowDraftId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        Key(id.Kind, id.Name, id.Version);

        if (id.Generation == Guid.Empty || id.Revision < 1)
            throw new ArgumentException("A complete draft identity is required", nameof(id));
    }

    internal static void Actor(string? actor)
    {
        WorkflowCatalogRules.ValidateText(actor);

        if (actor?.Length > 200)
            throw new ArgumentException("Actor exceeds 200 characters", nameof(actor));
    }

    internal static void Save(WorkflowDraft definition, WorkflowDraftId? expected, string? actor)
    {
        Key(definition.Kind, definition.Name, definition.Version);
        ArgumentNullException.ThrowIfNull(definition.Source);
        WorkflowCatalogRules.ValidateText(definition.Source);
        Actor(actor);

        if (expected == null)
        {
            if (definition.Generation != Guid.Empty || definition.Revision != 0)
                throw new ArgumentException("A new draft must have an empty generation and zero revision", nameof(definition));
        }
        else
        {
            Id(expected);

            if ((expected.Kind, expected.Name, expected.Version) != (definition.Kind, definition.Name, definition.Version))
                throw new ArgumentException("The expected draft identity differs from the proposed identity", nameof(expected));
        }
    }

    internal static void Token(WorkflowVersion row, CatalogMutationToken expected)
    {
        ArgumentNullException.ThrowIfNull(expected);

        if (row.LifecycleRevision != expected.Revision)
            throw new CatalogPreconditionException("The workflow lifecycle revision changed");
    }

    internal static WorkflowPublicationReceipt Receipt(WorkflowVersion version) => new(
        version.PublishedDraft ?? throw new ArgumentException("The workflow has no draft receipt"), JsonSerializer.Serialize(version));

    private static string PublicationContent(WorkflowVersion version) => JsonSerializer.Serialize(version with
    {
        State = default, LifecycleRevision = 0, StateChangedAtUtc = null, StateChangedBy = null
    });

    internal static WorkflowPublicationOutcome Outcome(WorkflowDraftId draft, WorkflowPublicationOutcome outcome, WorkflowVersion? stored,
        IxIFlow.Templates.TemplateVersionEntry? template = null, WorkflowPackage? package = null)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(outcome.Diagnostics);

        foreach (var diagnostic in outcome.Diagnostics)
            if (diagnostic.Stage is not ("Validate" or "Translate" or "Compile" or "Test" or "Publish") ||
                diagnostic.Severity is not ("Info" or "Warning" or "Error") || !Enum.IsDefined(diagnostic.Class))
                throw new ArgumentException("Invalid publication diagnostic", nameof(outcome));

        if (outcome.FailureClass is { } failure && !Enum.IsDefined(failure))
            throw new ArgumentException("Invalid failure class", nameof(outcome));

        if (outcome.Receipt is { } receipt)
        {
            if (draft.Kind == WorkflowDraftKinds.Activity)
            {
                WorkflowPackage? submittedPackage;

                try
                {
                    submittedPackage = JsonSerializer.Deserialize<WorkflowPackage>(receipt.ReceiptJson);
                }
                catch (JsonException error)
                {
                    throw new ArgumentException("Invalid package publication receipt", nameof(outcome), error);
                }

                if (receipt.Draft != draft || package?.PublishedDraft != draft || submittedPackage == null ||
                    submittedPackage.PublishedDraft != draft ||
                    PackagePublicationContent(submittedPackage) != PackagePublicationContent(package) ||
                    outcome.FailureClass != null || outcome.Diagnostics.Any(item => item.Severity == "Error"))
                    throw new ArgumentException("Publication receipt must match the audited immutable package entry", nameof(outcome));

                return outcome with { Receipt = new(draft, JsonSerializer.Serialize(package)) };
            }

            if (draft.Kind == WorkflowDraftKinds.Template)
            {
                IxIFlow.Templates.TemplateVersionEntry? submittedTemplate;

                try
                {
                    submittedTemplate = JsonSerializer.Deserialize<IxIFlow.Templates.TemplateVersionEntry>(receipt.ReceiptJson);
                }
                catch (Exception error) when (error is JsonException or ArgumentException)
                {
                    throw new ArgumentException("Invalid template publication receipt", nameof(outcome), error);
                }

                if (receipt.Draft != draft || template?.PublishedDraft != draft || submittedTemplate == null ||
                    JsonSerializer.Serialize(submittedTemplate) != JsonSerializer.Serialize(template) ||
                    outcome.FailureClass != null || outcome.Diagnostics.Any(item => item.Severity == "Error"))
                    throw new ArgumentException("Publication receipt must match the audited immutable template entry", nameof(outcome));

                return outcome with { Receipt = new(draft, JsonSerializer.Serialize(template)) };
            }

            WorkflowVersion? submitted;

            try
            {
                submitted = JsonSerializer.Deserialize<WorkflowVersion>(receipt.ReceiptJson);
            }
            catch (JsonException error)
            {
                throw new ArgumentException("Invalid publication receipt", nameof(outcome), error);
            }

            if (draft != receipt.Draft || draft.Kind != WorkflowDraftKinds.Workflow || stored?.PublishedDraft != draft ||
                submitted == null || PublicationContent(submitted) != PublicationContent(stored) ||
                outcome.FailureClass != null || outcome.Diagnostics.Any(item => item.Severity == "Error"))
                throw new ArgumentException("Publication receipt must match the audited immutable catalog entry", nameof(outcome));

            // Lifecycle fields in a caller's receipt may predate a transition. Store only trusted
            // catalog fields, read under the same lock/transaction as completion.
            return outcome with { Receipt = Receipt(stored) };
        }
        else if (outcome.FailureClass == null)
            throw new ArgumentException("A failed publication requires a failure class", nameof(outcome));

        return outcome;
    }

    private static string PackagePublicationContent(WorkflowPackage package) => JsonSerializer.Serialize(package with
    {
        Withdrawn = false, WithdrawnAtUtc = null, WithdrawnBy = null, LifecycleRevision = 0
    });

    internal static WorkflowPublication Copy(WorkflowPublication value) =>
        JsonSerializer.Deserialize<WorkflowPublication>(JsonSerializer.Serialize(value))!;

    internal static RetirementBlockers Mask(IEnumerable<WorkflowVersion> callers, WorkflowVersionReference? unfinished, CatalogScope scope)
    {
        var pairs = callers.Select(row => new WorkflowVersionReference(row.Name, row.Version)).ToArray();

        return new(pairs.Where(row => scope.Allows(WorkflowDraftKinds.Workflow, row.Name)).ToArray(),
            unfinished != null && scope.Allows(WorkflowDraftKinds.Workflow, unfinished.Name) ? unfinished : null,
            pairs.Any(row => !scope.Allows(WorkflowDraftKinds.Workflow, row.Name)) ||
            unfinished != null && !scope.Allows(WorkflowDraftKinds.Workflow, unfinished.Name));
    }
}