using JsonSerializer = IxIFlow.Core.Runtime.WorkflowJson;
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

    private readonly bool _enableSensitiveDiagnostics;

    internal InspectionRedactionPolicy(bool enableSensitiveDiagnostics = false) =>
        _enableSensitiveDiagnostics = enableSensitiveDiagnostics;

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
            return note.Type == WorkflowTypeIdentity.SavedName(typeof(string))
                ? JsonSerializer.Serialize(Note(JsonSerializer.Deserialize<string>(note.Json.InlineText) ?? "", services))
                : new RedactingJson(services).Serialize(note.Json.InlineText, note.Type);
        }
        catch { return "\"\""; }
    }

    /// <summary>Projects a stored diagnostic without exposing its message or stack.</summary>
    public InspectionError Error(WorkflowDiagnostic diagnostic) => new(diagnostic.ErrorType, diagnostic.DiagnosticId);

    /// <summary>The committed diagnostic identity, or none for a data-free engine rejection.</summary>
    public InspectionError? ResultError(WorkflowExecutionResult result) => result.Diagnostic;

    /// <summary>Only explicitly classified engine reasons may expose their original text.</summary>
    public string ResultReason(WorkflowExecutionResult result) => result.Diagnostic?.ToString() ??
        (result.ErrorKind != null ? result.ErrorMessage ?? result.ErrorKind.ToString()! : result.Status.ToString());

    /// <summary>Projects a delivery for an inspection listing. Store reads remain execution primitives.</summary>
    public OutboxDelivery Delivery(OutboxDelivery delivery) => delivery with
    {
        Event = delivery.Event with { DataJson = "" },
        LeaseToken = null,
        LeaseUntilUtc = null,
        LastError = delivery.LastError == null ? null : new InspectionError("DeliveryFailure", DeliveryDiagnosticId(delivery)).ToString()
    };

    /// <summary>Administrative listings never expose event bodies or publisher free text.</summary>
    public DeliverySummary Delivery(DeliverySummary delivery) => delivery with
    {
        Event = delivery.Event with { DataJson = "" },
        LastError = delivery.LastError == null ? null : new InspectionError("DeliveryFailure",
            $"{delivery.Event.Id}/{delivery.PublisherName}/{delivery.Attempts}").ToString()
    };

    public OutboxPublisherState Publisher(OutboxPublisherState publisher) => publisher with
    {
        LastError = publisher.LastError == null ? null : new InspectionError("PublisherFailure",
            $"{publisher.Name}/{publisher.DisabledAtUtc?.Ticks}").ToString()
    };

    /// <summary>Schema shape is inspection; annotations can contain values or arbitrary free text.</summary>
    public string? EventSchema(string typeName)
    {
        try
        {
            if (WorkflowTypeIdentity.Resolve(typeName) is not { } type) return null;
            var options = WorkflowJson.Options(type: type);
            options.MakeReadOnly(populateMissingResolver: true);
            var schema = System.Text.Json.Schema.JsonSchemaExporter.GetJsonSchemaAsNode(options, type);
            StripAnnotations(schema);
            return schema.ToJsonString();
        }
        catch { return null; }
    }

    private static void StripAnnotations(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node is System.Text.Json.Nodes.JsonObject obj)
        {
            foreach (var key in new[] { "default", "examples", "example", "description", "title", "enum", "const" }) obj.Remove(key);
            foreach (var property in obj.ToArray())
            {
                // Property names are schema structure, even when named "default" or "description".
                if (property.Key is "properties" or "$defs" && property.Value is System.Text.Json.Nodes.JsonObject members)
                    foreach (var member in members) StripAnnotations(member.Value);
                else StripAnnotations(property.Value);
            }
        }
        else if (node is System.Text.Json.Nodes.JsonArray array)
            foreach (var item in array) StripAnnotations(item);
    }

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
        if (!_enableSensitiveDiagnostics) return;
        var safe = Error(error);
        logger?.LogError(error, "Sensitive diagnostic {ErrorType} ({DiagnosticId})", safe.ErrorType, safe.DiagnosticId);
    }
}

/// <summary>Safe error identity for inspection, spans and ordinary logs.</summary>
public sealed record InspectionError(string ErrorType, string DiagnosticId)
{
    public override string ToString() => $"{ErrorType} ({DiagnosticId})";
}

/// <summary>Logger category for raw exceptions. Emission requires the engine sensitive-diagnostics setting.</summary>
public sealed class SensitiveDiagnostics;

/// <summary>Raw diagnostics from a checkpoint. Return only after application authorization.</summary>
public sealed record WorkflowDiagnostic(string DiagnosticId, string ErrorType, string Message, string? StackTrace);
