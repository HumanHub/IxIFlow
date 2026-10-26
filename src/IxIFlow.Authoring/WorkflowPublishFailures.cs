using IxIFlow.Core;
using IxIFlow.Dsl.Compilation;
using IxIFlow.Dsl.Documents;
using IxIFlow.Templates;

namespace IxIFlow.Authoring;

internal static class WorkflowPublishFailures
{
    internal static WorkflowPublishDiagnostic Diagnostic(string code, string message, WorkflowPublishDiagnosticClass @class,
        WorkflowPublishStage stage = WorkflowPublishStage.Validate, DocumentLocation? location = null) =>
        new(stage, code, message, WorkflowDiagnosticSeverity.Error, "workflow.yaml",
            Math.Max(1, location?.Line ?? 1), Math.Max(1, location?.Column ?? 1)) { Class = @class };

    internal static WorkflowPublishDiagnostic Unsupported(string kind) => Diagnostic("PUB002",
        "This document kind is not supported by this publisher", WorkflowPublishDiagnosticClass.Authoring) with { Path = kind + ".yaml" };

    internal static WorkflowPublishDiagnosticClass ClassFor(string code) => code switch
    {
        "PUB001" or "PUB022" or "PUB025" or "PUB040" or "TPL007" => WorkflowPublishDiagnosticClass.Conflict,
        "PUB006" or "PUB008" or "PUB009" or "PUB017" or "PUB024" or "PUB030" or "PUB041" or "PUB043" => WorkflowPublishDiagnosticClass.Unavailable,
        "PUB042" or "TPL018" => WorkflowPublishDiagnosticClass.Limit,
        "PUB007" or "PUB023" => WorkflowPublishDiagnosticClass.Internal,
        _ => WorkflowPublishDiagnosticClass.Authoring
    };

    internal static WorkflowPublishDiagnostic Classify(Exception error, WorkflowPublishStage stage = WorkflowPublishStage.Publish,
        DocumentLocation? location = null)
    {
        if (error is WorkflowPublishException curated)
            return Diagnostic(curated.Code, curated.Message, ClassFor(curated.Code), stage, curated.Location ?? location);
        if (error is TemplateRenderException { InnerException: WorkflowEngineException { Kind: WorkflowErrorKind.ResourceExhausted } limit })
            return Classify(limit, stage, location);
        if (PublishDependencyNotFoundException.Find(error) is { } refusal)
            return Diagnostic("PUB003", refusal.Message, WorkflowPublishDiagnosticClass.Authoring, stage, location);
        var (code, message, @class) = error switch
        {
            CatalogPreconditionException => ("PUB040", "The catalog changed before publication completed", WorkflowPublishDiagnosticClass.Conflict),
            WorkflowEngineException { Kind: WorkflowErrorKind.InvalidState } => ("PUB040", "The publication identity conflicts with the catalog", WorkflowPublishDiagnosticClass.Conflict),
            WorkflowEngineException { Kind: WorkflowErrorKind.ArtifactUnavailable or WorkflowErrorKind.ArtifactMismatch or WorkflowErrorKind.Infrastructure } =>
                ("PUB041", "A required artifact or publication service is unavailable", WorkflowPublishDiagnosticClass.Unavailable),
            IOException or UnauthorizedAccessException or BadImageFormatException or TypeLoadException or System.Reflection.ReflectionTypeLoadException => ("PUB041", "A required artifact is unavailable or corrupt", WorkflowPublishDiagnosticClass.Unavailable),
            WorkflowEngineException { Kind: WorkflowErrorKind.ResourceExhausted } => ("PUB042", "Publication exceeds a configured resource limit", WorkflowPublishDiagnosticClass.Limit),
            TimeoutException => ("PUB043", "Publication exceeded the configured timeout", WorkflowPublishDiagnosticClass.Unavailable),
            PackageClosureUnavailableException => ("PUB041", "A required package dependency is unavailable", WorkflowPublishDiagnosticClass.Unavailable),
            WorkflowEngineException { Kind: WorkflowErrorKind.InvalidDefinition or WorkflowErrorKind.InvalidRequest } =>
                ("PUB004", error.Message, WorkflowPublishDiagnosticClass.Authoring),
            _ => ("PUB007", "An unexpected publication failure occurred", WorkflowPublishDiagnosticClass.Internal)
        };
        return Diagnostic(code, message, @class, stage, location);
    }
}
