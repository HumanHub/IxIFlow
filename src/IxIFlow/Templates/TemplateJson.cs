using System.Text.Json;
using System.Text.Json.Serialization;
using IxIFlow.Core;

namespace IxIFlow.Templates;

public sealed record TemplateVersionEntry(
    TemplateVersion Version,
    WorkflowDraftId? PublishedDraft,
    DateTime PublishedAtUtc,
    string? PublishedBy);

/// <summary>Canonical content bytes. Publication receipts and audit never enter these bytes.</summary>
public static class TemplateJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<TemplateFormat>() }
    };

    public static byte[] Serialize(TemplateVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);

        return JsonSerializer.SerializeToUtf8Bytes(version, Options);
    }

    public static TemplateVersion Deserialize(ReadOnlySpan<byte> bytes) =>
        JsonSerializer.Deserialize<TemplateVersion>(bytes, Options) ?? throw new JsonException("Template content is null");
}