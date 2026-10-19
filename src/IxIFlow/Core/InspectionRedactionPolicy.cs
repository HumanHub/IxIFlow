using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using IxIFlow.Core.Runtime;
using Microsoft.Extensions.Logging;

namespace IxIFlow.Core;

/// <summary>The policy for free text in inspection and engine diagnostics. Execution results are not projections.</summary>
public sealed class InspectionRedactionPolicy
{
    public static InspectionRedactionPolicy Default { get; } = new();
    public const string SensitiveDiagnosticsCategory = "IxIFlow.Core.SensitiveDiagnostics";
    private static readonly ConditionalWeakTable<Exception, InspectionError> Errors = new();

    private InspectionRedactionPolicy() { }

    /// <summary>A random correlation ID, never derived from the message or stack.</summary>
    public InspectionError Error(Exception error) => Errors.GetValue(error, value =>
        new(SerializedException.CatchType(value).FullName ?? "Exception", Guid.NewGuid().ToString("N")));

    /// <summary>Projects wholly sensitive free text. No provider means erasure.</summary>
    public string Note(string note, IServiceProvider services) => new RedactingJson(services).SensitiveText(note);

    /// <summary>Projects typed values for inspection, including debugger variables and edit responses.</summary>
    public JsonElement Value(object? value, IServiceProvider services) =>
        JsonSerializer.Deserialize<JsonElement>(new RedactingJson(services).Serialize(value));

    /// <summary>Projects a saved typed value without modifying the checkpoint.</summary>
    public JsonElement StoredValue(string json, string typeName, IServiceProvider services) =>
        JsonSerializer.Deserialize<JsonElement>(new RedactingJson(services).Serialize(json, typeName));

    /// <summary>Projects a recovery reason from its checkpoint identity.</summary>
    public string? Reason(InspectionError? diagnostic) => diagnostic?.ToString();

    internal string AttemptNote(SerializedValue note, IServiceProvider services)
    {
        try
        {
            return note.Type == typeof(string).AssemblyQualifiedName
                ? JsonSerializer.Serialize(Note(JsonSerializer.Deserialize<string>(note.Json) ?? "", services))
                : new RedactingJson(services).Serialize(note.Json, note.Type);
        }
        catch { return "\"\""; }
    }

    /// <summary>Projects a stored diagnostic without exposing its message or stack.</summary>
    public InspectionError Error(WorkflowDiagnostic diagnostic) => new(diagnostic.ErrorType, diagnostic.DiagnosticId);

    /// <summary>The safe identity of a result, including rejections that did not produce a checkpoint.</summary>
    public InspectionError ResultError(WorkflowExecutionResult result) => result.Diagnostic ??=
        new(result.ErrorType ?? result.Status.ToString(), Guid.NewGuid().ToString("N"));

    /// <summary>Projects a delivery for an inspection listing. Store reads remain execution primitives.</summary>
    public OutboxDelivery Delivery(OutboxDelivery delivery) => delivery with
    {
        LastError = delivery.LastError == null ? null : new InspectionError("DeliveryFailure", DeliveryDiagnosticId(delivery)).ToString()
    };

    /// <summary>A delivery attempt has an identity even when the publisher returned only free text.</summary>
    public string DeliveryDiagnosticId(OutboxDelivery delivery) => $"{delivery.Event.Id}/{delivery.PublisherName}/{delivery.Attempts}";

    internal void Fail(Activity? span, Exception error)
    {
        var safe = Error(error);
        span?.SetStatus(ActivityStatusCode.Error);
        span?.SetTag("error.type", safe.ErrorType);
        span?.SetTag("ixiflow.diagnostic.id", safe.DiagnosticId);
    }

    internal void Log(ILogger logger, ILogger<SensitiveDiagnostics>? sensitive, LogLevel level,
        Exception error, string message, params object?[] args)
    {
        var safe = Error(error);
        logger.Log(level, message + ": {ErrorType} ({DiagnosticId})", args.Concat([safe.ErrorType, safe.DiagnosticId]).ToArray());
        LogSensitive(sensitive, error);
    }

    internal void LogSensitive(ILogger<SensitiveDiagnostics>? logger, Exception error)
    {
        var safe = Error(error);
        logger?.LogError(error, "Sensitive diagnostic {ErrorType} ({DiagnosticId})", safe.ErrorType, safe.DiagnosticId);
    }
}

/// <summary>Safe error identity for inspection, spans and ordinary logs.</summary>
public sealed record InspectionError(string ErrorType, string DiagnosticId)
{
    public override string ToString() => $"{ErrorType} ({DiagnosticId})";
}

/// <summary>Logger category for raw exceptions. Disabled by AddIxIFlow; explicitly enable its category to opt in.</summary>
public sealed class SensitiveDiagnostics;

/// <summary>Raw diagnostics from a checkpoint. Return only after application authorization.</summary>
public sealed record WorkflowDiagnostic(string DiagnosticId, string ErrorType, string Message, string? StackTrace);
