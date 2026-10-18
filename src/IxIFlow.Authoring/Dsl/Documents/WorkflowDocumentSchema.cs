using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace IxIFlow.Dsl.Documents;

/// <summary>Generates the editor schema from the shared workflow document contract.</summary>
public static class WorkflowDocumentSchema
{
    /// <summary>The schema's URL in the tagged IxIFlow release that ships it.</summary>
    public static string Id { get; } = "https://raw.githubusercontent.com/HumanHub/IxIFlow/v" +
        typeof(IxIFlow.Core.WorkflowDefinition).Assembly.GetName().Version!.ToString(3) + "/src/IxIFlow/schemas/workflow.schema.json";

    public static string Write()
    {
        var definitions = new JsonObject();
        var nullability = new NullabilityInfoContext();
        var root = new JsonObject
        {
            ["$schema"] = "http://json-schema.org/draft-07/schema#",
            ["$id"] = Id,
            ["title"] = "IxIFlow workflow"
        };
        foreach (var property in ObjectSchema(typeof(WorkflowDocument))) root.Add(property.Key, property.Value?.DeepClone());
        root["definitions"] = definitions;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n";

        JsonObject Reference(Type type)
        {
            var name = type == typeof(WorkflowStepDocument) ? "step" :
                DocumentModel.CamelCase(type.Name.Substring(0, type.Name.Length - "Document".Length));
            if (!definitions.ContainsKey(name))
            {
                definitions[name] = null;
                definitions[name] = type.IsAbstract ? AbstractSchema(type) : ObjectSchema(type);
            }
            return new JsonObject { ["$ref"] = "#/definitions/" + name };
        }

        JsonObject ValueSchema(Type type, NullabilityInfo? nullable = null)
        {
            var underlying = Nullable.GetUnderlyingType(type);
            var allowsNull = underlying != null || nullable?.ReadState == NullabilityState.Nullable;
            type = underlying ?? type;
            if (type == typeof(object)) return new JsonObject();
            var scalar = type == typeof(string) ? "string" : type == typeof(int) ? "integer" : type == typeof(bool) ? "boolean" : type == typeof(double) ? "number" : null;
            if (scalar != null)
                return new JsonObject { ["type"] = allowsNull ? new JsonArray(scalar, "null") : JsonValue.Create(scalar) };
            if (type.IsArray)
                return new JsonObject { ["type"] = "array", ["items"] = ValueSchema(type.GetElementType()!, nullable?.ElementType) };
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
                return new JsonObject { ["type"] = "array", ["items"] = ValueSchema(type.GetGenericArguments()[0], nullable?.GenericTypeArguments[0]) };
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
                return new JsonObject { ["type"] = "object", ["additionalProperties"] = ValueSchema(type.GetGenericArguments()[1], nullable?.GenericTypeArguments[1]) };
            return Reference(type);
        }

        JsonObject ObjectSchema(Type type)
        {
            var properties = new JsonObject();
            var required = new JsonArray();
            var instance = Activator.CreateInstance(type)!;
            foreach (var key in DocumentModel.GetKeys(type))
            {
                if (key.IsDiscriminator)
                {
                    var kind = DocumentModel.GetKind(type)!;
                    properties[key.Name] = new JsonObject
                    {
                        ["type"] = "string", ["const"] = kind.Kind,
                        ["description"] = key.Member is { } stored ? Describe(stored) : "The discriminator selecting this document variant."
                    };
                    if (kind.ImpliedBy == null) required.Add(key.Name);
                    continue;
                }
                if (key.Member is not { } member)
                {
                    properties[key.Name] = new JsonObject
                    {
                        ["type"] = "string", ["description"] = "The editor schema this document binds to; readers ignore it."
                    };
                    continue;
                }
                var schema = ValueSchema(member.PropertyType, nullability.Create(member));
                schema["description"] = Describe(member);
                if (member.GetCustomAttribute<DocumentValuesAttribute>() is { } values)
                    schema["enum"] = new JsonArray(values.Values.Select(value => (JsonNode?)JsonValue.Create(value))
                        .Concat(nullability.Create(member).ReadState == NullabilityState.Nullable ? new JsonNode?[] { null } : []).ToArray());
                if (member.GetValue(instance) is { } value && (value is string text && text.Length > 0 || value is int or bool or double))
                    schema["default"] = JsonSerializer.SerializeToNode(value);
                properties[key.Name] = schema;
                if (member.IsDefined(typeof(DocumentRequiredAttribute))) required.Add(key.Name);
            }
            var result = new JsonObject
            {
                ["type"] = "object",
                ["description"] = Describe(type),
                ["additionalProperties"] = false,
                ["properties"] = properties
            };
            if (required.Count > 0) result["required"] = required;
            return result;
        }

        JsonObject AbstractSchema(Type type)
        {
            var variants = DocumentModel.GetVariants(type).ToArray();
            var implied = variants.Select(DocumentModel.GetKind).Where(kind => kind!.ImpliedBy != null).ToArray();
            var conditions = new JsonArray();
            foreach (var variant in variants)
            {
                var kind = DocumentModel.GetKind(variant)!;
                conditions.Add(new JsonObject
                {
                    ["if"] = new JsonObject
                    {
                        ["required"] = Strings(["kind"]),
                        ["properties"] = new JsonObject { ["kind"] = new JsonObject { ["const"] = kind.Kind } }
                    },
                    ["then"] = Reference(variant)
                });
                if (kind.ImpliedBy is { } member)
                    conditions.Add(new JsonObject
                    {
                        ["if"] = new JsonObject
                        {
                            ["required"] = Strings([member]),
                            ["not"] = new JsonObject { ["required"] = Strings(["kind"]) }
                        },
                        ["then"] = Reference(variant)
                    });
            }
            var result = new JsonObject
            {
                ["type"] = "object",
                ["description"] = Describe(type),
                ["properties"] = new JsonObject
                {
                    ["kind"] = new JsonObject
                    {
                        ["type"] = "string", ["enum"] = Strings(variants.Select(variant => DocumentModel.GetKind(variant)!.Kind)),
                        ["description"] = "The discriminator selecting the concrete document variant."
                    }
                },
                ["allOf"] = conditions
            };
            if (implied.Length == 0) result["required"] = Strings(["kind"]);
            else result["anyOf"] = new JsonArray(new[] { "kind" }.Concat(implied.Select(kind => kind!.ImpliedBy!))
                .Select(member => (JsonNode)new JsonObject { ["required"] = Strings([member]) }).ToArray());
            return result;
        }
    }

    private static string Describe(MemberInfo member) => member.GetCustomAttribute<DescriptionAttribute>()?.Description ??
        throw new InvalidOperationException($"{(member is Type ? member.Name : member.DeclaringType!.Name + "." + member.Name)} needs a [Description] for the workflow schema");

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
}
