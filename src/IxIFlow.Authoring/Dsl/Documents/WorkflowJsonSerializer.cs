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
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver().WithAddedModifier(info =>
        {
            if (!typeof(DocumentNode).IsAssignableFrom(info.Type) || info.Kind != JsonTypeInfoKind.Object) return;
            var properties = info.Properties.ToDictionary(property => property.Name);
            var members = DocumentModel.GetMembers(info.Type);
            info.Properties.Clear();
            // A variant always writes its declared kind and accepts no other, including where its type is fixed.
            var kind = DocumentModel.GetKind(info.Type)?.Kind;
            if (kind != null)
            {
                var stored = members.FirstOrDefault(member => DocumentModel.GetName(member) == "kind");
                var discriminator = info.CreateJsonPropertyInfo(typeof(string), "kind");
                discriminator.Get = _ => kind;
                discriminator.Set = (target, value) =>
                {
                    if ((string?)value != kind) throw new JsonException($"Expected workflow document kind '{kind}'");
                    stored?.SetValue(target, value);
                };
                info.Properties.Add(discriminator);
            }
            foreach (var member in members)
            {
                var name = DocumentModel.GetName(member);
                if (name == "kind" && kind != null) continue;
                var property = properties[member.Name];
                property.Name = name;
                info.Properties.Add(property);
            }
        }),
        Converters =
        {
            new DocumentKindConverter<WorkflowStepDocument>(),
            new DocumentKindConverter<ExpressionDocument>(),
            new DocumentValueConverter()
        }
    };

    private sealed class DocumentKindConverter<T> : JsonConverter<T> where T : DocumentNode
    {
        public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            var kind = root.TryGetProperty("kind", out var discriminator) ? discriminator.GetString() : null;
            var type = DocumentModel.ResolveVariant(typeof(T), kind, name => root.TryGetProperty(name, out _))
                ?? throw new JsonException($"Unknown workflow document kind '{kind}'");
            return (T?)root.Deserialize(type, options);
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            if (DocumentModel.GetKind(value.GetType()) == null)
                throw new JsonException($"Unknown workflow document type '{value.GetType().Name}'");
            JsonSerializer.Serialize(writer, value, value.GetType(), options);
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
