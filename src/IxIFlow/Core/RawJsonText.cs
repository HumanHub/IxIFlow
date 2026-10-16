using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IxIFlow.Core;

/// <summary>
/// Writes a string that holds JSON as that JSON, not as an escaped string inside its owner's JSON, and reads it
/// back as text. A string written before this, as an escaped string, still reads.
/// </summary>
internal sealed class RawJsonText : JsonConverter<string>
{
    // The text sits one level inside its owner, so what is written raw keeps one level of the reader's depth
    // free; deeper text is escaped, as is text that is not JSON, so whatever is written reads back.
    private const int MaxRawDepth = 63;

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
        var text = value.AsSpan().TrimStart();
        if (text.Length > 0 && text[0] is '{' or '[' && IsJson(Encoding.UTF8.GetBytes(value), out var utf8))
            writer.WriteRawValue(utf8, skipInputValidation: true);
        else
            writer.WriteStringValue(value);
    }

    // One pass that both validates the text and bounds its depth.
    private static bool IsJson(byte[] bytes, out byte[] utf8)
    {
        utf8 = bytes;
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = MaxRawDepth });
        try
        {
            if (!reader.Read())
                return false;
            reader.Skip();
            return !reader.Read();
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
