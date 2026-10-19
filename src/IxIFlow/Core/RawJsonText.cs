using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Core;

/// <summary>Embeds a validated schema-4 checkpoint as a JSON object.</summary>
internal sealed class RawJsonText : JsonConverter<string>
{
    public override bool HandleNull => true;

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("An execution checkpoint must be a JSON object");
        using var document = JsonDocument.ParseValue(ref reader);
        var json = document.RootElement.GetRawText();
        _ = ExecutionCheckpoint.ReadTrunk(json);
        return json;
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        // The checkpoint shares its owner's depth budget. Reject excess depth before writing.
        var remainingDepth = (options.MaxDepth == 0 ? 64 : options.MaxDepth) - writer.CurrentDepth;
        if (remainingDepth <= 0)
            throw new JsonException("The execution checkpoint exceeds the JSON depth limit");
        var bytes = Encoding.UTF8.GetBytes(value);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = remainingDepth });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("An execution checkpoint must be a JSON object");
        try
        {
            reader.Skip();
            if (reader.Read())
                throw new JsonException("Unexpected content after the execution checkpoint");
        }
        catch (JsonException error)
        {
            throw new JsonException("The execution checkpoint is invalid or exceeds the JSON depth limit", error);
        }
        _ = ExecutionCheckpoint.ReadTrunk(value);
        writer.WriteRawValue(bytes, skipInputValidation: true);
    }
}
