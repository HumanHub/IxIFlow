using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace IxIFlow.Dsl.Documents;

/// <summary>JSON authoring support around the shared, serializer-independent workflow model.</summary>
public static class WorkflowJsonSerializer
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static WorkflowDocument Read(string json) => JsonSerializer.Deserialize<WorkflowDocument>(json, Options)
        ?? throw new JsonException("Workflow JSON did not contain a document");

    public static string Write(WorkflowDocument document) => JsonSerializer.Serialize(document, Options);

    private static JsonSerializerOptions CreateOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver().WithAddedModifier(info =>
        {
            if (typeof(DocumentNode).IsAssignableFrom(info.Type))
                foreach (var property in info.Properties.Where(property => property.Name is "location" or "members" ||
                             typeof(WorkflowStepDocument).IsAssignableFrom(info.Type) && property.Name == "kind").ToArray())
                    info.Properties.Remove(property);
        }),
        Converters =
        {
            new DocumentKindConverter<WorkflowStepDocument>(
                ("activity", typeof(ActivityStepDocument)), ("activityRef", typeof(ActivityReferenceStepDocument)),
                ("stepRef", typeof(StepReferenceStepDocument)), ("if", typeof(ConditionalStepDocument)),
                ("sequence", typeof(SequenceStepDocument)), ("forEach", typeof(ForEachStepDocument)),
                ("suspend", typeof(SuspendStepDocument)), ("invokeWorkflow", typeof(InvokeWorkflowStepDocument))),
            new DocumentKindConverter<ExpressionDocument>(
                ("constant", typeof(ConstantExpressionDocument)), ("path", typeof(PathExpressionDocument)),
                ("binary", typeof(BinaryExpressionDocument)), ("function", typeof(FunctionExpressionDocument))),
            new DocumentValueConverter()
        }
    };

    private sealed class DocumentKindConverter<T>(params (string Kind, Type Type)[] types) : JsonConverter<T> where T : DocumentNode
    {
        public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            var kind = root.TryGetProperty("kind", out var discriminator) ? discriminator.GetString() : null;
            var type = types.FirstOrDefault(entry => entry.Kind == kind).Type
                ?? throw new JsonException($"Unknown workflow document kind '{kind}'");
            return (T?)root.Deserialize(type, options);
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            var kind = types.FirstOrDefault(entry => entry.Type == value.GetType()).Kind
                ?? throw new JsonException($"Unknown workflow document type '{value.GetType().Name}'");
            writer.WriteStartObject();
            writer.WriteString("kind", kind);
            foreach (var property in JsonSerializer.SerializeToElement(value, value.GetType(), options).EnumerateObject())
                property.WriteTo(writer);
            writer.WriteEndObject();
        }
    }

    private sealed class DocumentValueConverter : JsonConverter<object>
    {
        public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return Value(document.RootElement);
        }

        private static object? Value(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.Array => element.EnumerateArray().Select(Value).ToList(),
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(property => property.Name, property => Value(property.Value)),
            _ => throw new JsonException("Unsupported document value")
        };

        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value, value.GetType());
    }
}
