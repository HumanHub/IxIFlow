using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace IxIFlow.Dsl.Documents;

/// <summary>
/// Reads and writes the shared workflow document. Validation and compilation are separate steps.
/// </summary>
public static class WorkflowYamlSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static WorkflowDocument Read(string yaml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yaml);
        var stream = new YamlStream();
        using var reader = new StringReader(yaml);
        stream.Load(reader);
        if (stream.Documents.Count != 1)
        {
            throw new InvalidOperationException("A workflow YAML file must contain one document");
        }

        var value = FromYamlNode(stream.Documents[0].RootNode);
        var json = JsonSerializer.Serialize(value, JsonOptions);
        try
        {
            return JsonSerializer.Deserialize<WorkflowDocument>(json, JsonOptions)
                ?? throw new InvalidOperationException("Workflow YAML did not contain a document");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Workflow YAML does not match the workflow document: {exception.Message}", exception);
        }
    }

    public static string Write(WorkflowDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var json = JsonSerializer.SerializeToNode(document, JsonOptions)
            ?? throw new InvalidOperationException("Workflow document could not be serialized");
        var stream = new YamlStream(new YamlDocument(ToYamlNode(json)));
        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        return writer.ToString();
    }

    private static object? FromYamlNode(YamlNode node) => node switch
    {
        YamlMappingNode mapping => Mapping(mapping),
        YamlSequenceNode sequence => sequence.Children.Select(FromYamlNode).ToList(),
        YamlScalarNode scalar => Scalar(scalar),
        _ => throw new InvalidOperationException($"Unsupported YAML node {node.GetType().Name}")
    };

    private static Dictionary<string, object?> Mapping(YamlMappingNode mapping)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in mapping.Children)
        {
            if (pair.Key is not YamlScalarNode key || string.IsNullOrWhiteSpace(key.Value) ||
                !values.TryAdd(key.Value, FromYamlNode(pair.Value)))
            {
                throw new InvalidOperationException("Workflow YAML contains an invalid or duplicate mapping key");
            }
        }
        return values;
    }

    private static object? Scalar(YamlScalarNode scalar)
    {
        var value = scalar.Value;
        if (scalar.Style != ScalarStyle.Plain) return value;
        if (value is null or "null" or "~") return null;
        if (value == "true") return true;
        if (value == "false") return false;
        if (long.TryParse(value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var integer)) return integer;
        if (double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var number)) return number;
        return value;
    }

    private static YamlNode ToYamlNode(JsonNode? node) => node switch
    {
        null => new YamlScalarNode("null"),
        JsonObject mapping => ToYamlMapping(mapping),
        JsonArray sequence => new YamlSequenceNode(sequence.Select(ToYamlNode)),
        JsonValue scalar => ToYamlScalar(scalar),
        _ => throw new InvalidOperationException($"Unsupported JSON node {node.GetType().Name}")
    };

    private static YamlMappingNode ToYamlMapping(JsonObject mapping)
    {
        var yaml = new YamlMappingNode();
        foreach (var pair in mapping)
        {
            yaml.Add(new YamlScalarNode(pair.Key), ToYamlNode(pair.Value));
        }
        return yaml;
    }

    private static YamlScalarNode ToYamlScalar(JsonValue value)
    {
        var element = JsonSerializer.SerializeToElement(value, JsonOptions);
        return element.ValueKind switch
        {
            JsonValueKind.String => new YamlScalarNode(element.GetString()) { Style = ScalarStyle.DoubleQuoted },
            JsonValueKind.True => new YamlScalarNode("true"),
            JsonValueKind.False => new YamlScalarNode("false"),
            JsonValueKind.Number => new YamlScalarNode(element.GetRawText()),
            JsonValueKind.Null => new YamlScalarNode("null"),
            _ => throw new InvalidOperationException($"Unsupported JSON value {element.ValueKind}")
        };
    }
}
