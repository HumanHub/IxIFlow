using System.Text.Json;
using IxIFlow.Core;
using IxIFlow.Dsl.Compilation;
using IxIFlow.Dsl.Documents;
using IxIFlow.Templates;

namespace IxIFlow.Authoring;

/// <summary>Exactly one result arm belongs to the declared kind.</summary>
public sealed record WorkflowPublishResult(string Kind, WorkflowVersion? Workflow,
    WorkflowPackage? Package, TemplateVersionEntry? Template, IReadOnlyList<WorkflowPublishDiagnostic> Diagnostics)
{
    public bool Success => HasMatchingArm && Diagnostics.All(item => item.Severity != WorkflowDiagnosticSeverity.Error);
    public WorkflowPublishDiagnosticClass? FailureClass => Diagnostics.FirstOrDefault(item => item.Severity == WorkflowDiagnosticSeverity.Error)?.Class;
    private bool HasMatchingArm => Kind switch
    {
        WorkflowDraftKinds.Workflow => Workflow != null && Package == null && Template == null,
        WorkflowDraftKinds.Activity => Package != null && Workflow == null && Template == null,
        WorkflowDraftKinds.Template => Template != null && Workflow == null && Package == null,
        _ => false
    };
    public WorkflowPublicationReceipt? Receipt
    {
        get
        {
            if (!Success) return null;
            var draft = Workflow?.PublishedDraft ?? Package?.PublishedDraft ?? Template?.PublishedDraft;
            if (draft == null) return null;
            WorkflowPublicationRules.Id(draft);
            if (draft.Kind != Kind || Kind switch
                {
                    WorkflowDraftKinds.Workflow => Workflow!.Name != draft.Name || Workflow.Version != draft.Version,
                    WorkflowDraftKinds.Activity => Package!.Id != draft.Name || Package.Version != draft.Version + ".0.0",
                    WorkflowDraftKinds.Template => Template!.Version.Key != draft.Name || Template.Version.Version != draft.Version,
                    _ => true
                }) throw new ArgumentException("The result arm does not match its publication identity");
            return new(draft, Kind switch
            {
                WorkflowDraftKinds.Workflow => JsonSerializer.Serialize(Workflow),
                WorkflowDraftKinds.Activity => JsonSerializer.Serialize(Package),
                WorkflowDraftKinds.Template => JsonSerializer.Serialize(Template),
                _ => throw new InvalidOperationException()
            });
        }
    }

    /// <summary>Decodes the same audited representation used by durable attempts.</summary>
    public static WorkflowPublishResult FromReceipt(WorkflowPublicationReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        WorkflowPublicationRules.Id(receipt.Draft);
        var result = receipt.Draft.Kind switch
        {
            WorkflowDraftKinds.Workflow => new WorkflowPublishResult(receipt.Draft.Kind, JsonSerializer.Deserialize<WorkflowVersion>(receipt.ReceiptJson), null, null, []),
            WorkflowDraftKinds.Activity => new(receipt.Draft.Kind, null, JsonSerializer.Deserialize<WorkflowPackage>(receipt.ReceiptJson), null, []),
            WorkflowDraftKinds.Template => new(receipt.Draft.Kind, null, null, JsonSerializer.Deserialize<TemplateVersionEntry>(receipt.ReceiptJson), []),
            _ => throw new ArgumentException("Unknown publication kind", nameof(receipt))
        };
        var decoded = result.Receipt;
        if (!result.Success || decoded == null || decoded.Draft != receipt.Draft || decoded.ReceiptJson != receipt.ReceiptJson)
            throw new ArgumentException("The receipt does not match its complete publication identity", nameof(receipt));
        return result;
    }
}

public sealed record WorkflowValidationReport(string Kind, IReadOnlyList<WorkflowPublishDiagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.All(item => item.Severity != WorkflowDiagnosticSeverity.Error);
    public WorkflowPublishDiagnosticClass? FailureClass => Diagnostics.FirstOrDefault(item => item.Severity == WorkflowDiagnosticSeverity.Error)?.Class;
}

public sealed record TemplatePreviewRequest(string? Culture, JsonElement Model);
public sealed record TemplatePreviewResult(RenderedTemplate? Rendered, IReadOnlyList<WorkflowPublishDiagnostic> Diagnostics);
