using System.Text.Json;
using System.Text.Json.Serialization;

namespace IxIFlow.Core;

/// <summary>
/// Writes a string that holds JSON as that JSON, not as an escaped string inside its owner's JSON, and reads it
/// back as text. A string written before this, as an escaped string, still reads.
/// </summary>
internal sealed class RawJsonText : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return reader.GetString();
        if (reader.TokenType == JsonTokenType.Null)
            return null;
        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.GetRawText();
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        // Only an object or array is written raw; anything else, such as an empty state, stays a string.
        var text = value.AsSpan().TrimStart();
        if (text.Length > 0 && text[0] is '{' or '[')
            writer.WriteRawValue(value);
        else
            writer.WriteStringValue(value);
    }
}
