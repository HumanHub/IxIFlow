using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace IxIFlow.AspNetCore;

/// <summary>Validated CloudEvents metadata, including extension attributes.</summary>
public sealed record CloudEventEnvelope(string Id, string Source, string Type, string? Subject,
    DateTimeOffset? Time, string? DataContentType, IReadOnlyDictionary<string, string> Attributes);

internal sealed record ParsedBody(JsonElement Data, CloudEventEnvelope? Envelope, bool Binary)
{
    public static ParsedBody Parse(HttpRequest request, byte[] bytes, bool required)
    {
        var mediaType = request.ContentType?.Split(';')[0].Trim();
        var structured = string.Equals(mediaType, "application/cloudevents+json", StringComparison.OrdinalIgnoreCase);
        var binary = request.Headers.Keys.Any(k => k.StartsWith("ce-", StringComparison.OrdinalIgnoreCase));
        if (structured && binary) throw new ArgumentException("CloudEvents content modes cannot be mixed");
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (!structured && !binary)
        {
            if (required) throw new ArgumentException("A CloudEvents envelope is required");
            if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("JSON content is required");
            return new(root.Clone(), null, false);
        }
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        JsonElement data;
        if (structured)
        {
            if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("Invalid CloudEvents object");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new ArgumentException("Duplicate CloudEvents attribute");
                if (property.Name is "data" or "data_base64") continue;
                if (property.Name.Length == 0 || property.Name.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9')))
                    throw new ArgumentException("Invalid CloudEvents attribute name");
                if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
                    throw new ArgumentException("Invalid CloudEvents attribute value");
                if (property.Name is "specversion" or "id" or "source" or "type" or "subject" or "time" or "datacontenttype" or "dataschema" &&
                    property.Value.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("CloudEvents core attributes must be strings");
                attributes.Add(property.Name, property.Value.ToString());
            }
            if (root.TryGetProperty("data_base64", out var encoded))
            {
                if (root.TryGetProperty("data", out _)) throw new ArgumentException("Conflicting CloudEvents data fields");
                if (encoded.ValueKind != JsonValueKind.String) throw new ArgumentException("Invalid CloudEvents base64 data");
                using var decoded = JsonDocument.Parse(Convert.FromBase64String(encoded.GetString()!));
                data = decoded.RootElement.Clone();
            }
            else data = root.TryGetProperty("data", out var payload) ? payload.Clone() : throw new ArgumentException("Event data is required");
        }
        else
        {
            foreach (var header in request.Headers.Where(h => h.Key.StartsWith("ce-", StringComparison.OrdinalIgnoreCase)))
            {
                if (header.Value.Count != 1) throw new ArgumentException("CloudEvents attributes must occur once");
                var name = header.Key[3..].ToLowerInvariant();
                if (name.Length == 0 || name.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9')))
                    throw new ArgumentException("Invalid CloudEvents attribute name");
                attributes.Add(name, Uri.UnescapeDataString(header.Value.ToString()));
            }
            if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Binary CloudEvents data must be JSON");
            attributes["datacontenttype"] = request.ContentType!;
            data = root.Clone();
        }
        string Required(string name) => attributes.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException("Missing CloudEvents " + name);
        if (Required("specversion") != "1.0") throw new ArgumentException("Unsupported CloudEvents version");
        var id = Required("id");
        var source = Required("source");
        var type = Required("type");
        if (!Uri.IsWellFormedUriString(source, UriKind.RelativeOrAbsolute) || source.Any(char.IsWhiteSpace))
            throw new ArgumentException("Invalid CloudEvents source URI reference");
        if (attributes.TryGetValue("dataschema", out var schema) && !Uri.TryCreate(schema, UriKind.Absolute, out _))
            throw new ArgumentException("Invalid CloudEvents data schema URI");
        DateTimeOffset? time = null;
        if (attributes.TryGetValue("time", out var text))
        {
            if (!Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$") ||
                !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                throw new ArgumentException("Invalid CloudEvents timestamp");
            time = parsed;
        }
        var content = attributes.GetValueOrDefault("datacontenttype");
        if (content != null && !string.Equals(content.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("CloudEvents data must be JSON");
        return new(data, new(id, source, type, attributes.GetValueOrDefault("subject"), time, content,
            attributes.AsReadOnly()), binary);
    }
}
