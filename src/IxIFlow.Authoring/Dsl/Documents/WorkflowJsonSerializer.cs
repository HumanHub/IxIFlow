using System.Collections.Concurrent;
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
            info.Properties.Clear();
            foreach (var key in DocumentModel.GetKeys(info.Type))
            {
                if (key.IsDiscriminator)
                {
                    // A variant always writes its declared kind and accepts no other, including where its type is fixed.
                    var kind = DocumentModel.GetKind(info.Type)!.Kind;
                    var discriminator = info.CreateJsonPropertyInfo(typeof(string), key.Name);
                    discriminator.Get = _ => kind;
                    discriminator.Set = (target, value) =>
                    {
                        if ((string?)value != kind) throw new JsonException($"Expected workflow document kind '{kind}'");
                        key.Member?.SetValue(target, value);
                    };
                    info.Properties.Add(discriminator);
                }
                else if (key.Member is { } member)
                {
                    var property = properties[member.Name];
                    property.Name = key.Name;
                    info.Properties.Add(property);
                }
                else
                {
                    // The root's editor schema binding is accepted and not part of the model.
                    var binding = info.CreateJsonPropertyInfo(typeof(string), key.Name);
                    binding.Get = _ => null;
                    binding.Set = (_, _) => { };
                    info.Properties.Add(binding);
                }
            }
        }),
        Converters =
        {
            new DocumentKindConverter<WorkflowStepDocument>(),
            new DocumentKindConverter<ExpressionDocument>(),
            new DocumentValueConverter()
        }
    };

    private abstract class VariantReader
    {
        public abstract DocumentNode? Read(ref Utf8JsonReader reader, JsonSerializerOptions options);
    }

    // Reads with the variant's own converter on the caller's reader; JsonSerializer.Deserialize would scope
    // a new reader to the value and restart its line numbers.
    private sealed class VariantReader<TVariant> : VariantReader where TVariant : DocumentNode
    {
        public override DocumentNode? Read(ref Utf8JsonReader reader, JsonSerializerOptions options) =>
            ((JsonConverter<TVariant>)options.GetConverter(typeof(TVariant))).Read(ref reader, typeof(TVariant), options);
    }

    private sealed class DocumentKindConverter<T> : JsonConverter<T> where T : DocumentNode
    {
        private static readonly ConcurrentDictionary<Type, VariantReader> Variants = new();

        public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            // Scan a copy for the discriminator, then read the variant with the original reader so that
            // error positions refer to the whole document.
            if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException($"A workflow {typeof(T).Name} must be an object");
            var scan = reader;
            var names = new HashSet<string>(StringComparer.Ordinal);
            string? kind = null;
            while (scan.Read() && scan.TokenType == JsonTokenType.PropertyName)
            {
                var name = scan.GetString()!;
                names.Add(name);
                scan.Read();
                if (name == "kind" && scan.TokenType == JsonTokenType.String) kind = scan.GetString();
                scan.Skip();
            }
            var type = DocumentModel.ResolveVariant(typeof(T), kind, names.Contains)
                ?? throw new JsonException($"Unknown workflow document kind '{kind}'");
            return (T?)Variants.GetOrAdd(type, static type => (VariantReader)Activator.CreateInstance(typeof(VariantReader<>).MakeGenericType(type))!)
                .Read(ref reader, options);
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
