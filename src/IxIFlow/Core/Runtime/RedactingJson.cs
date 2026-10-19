using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Compliance.Classification;
using Microsoft.Extensions.Compliance.Redaction;

namespace IxIFlow.Core.Runtime;

/// <summary>The only JSON projection for workflow values exposed through inspection.</summary>
internal sealed class RedactingJson(IServiceProvider services)
{
    internal string SensitiveText(string value)
    {
        try
        {
            var provider = services.GetService(typeof(IRedactorProvider)) as IRedactorProvider;
            return provider?.GetRedactor(SensitiveAttribute.Classification)?.Redact(value) ?? "";
        }
        catch { return ""; }
    }

    private static readonly ConditionalWeakTable<Type, ClassifiedPath[]> Paths = new();
    internal sealed record ClassifiedPath(string Path, DataClassificationSet Classifications);

    internal static IReadOnlyList<ClassifiedPath> ClassifiedPaths(Type type) =>
        Paths.GetValue(type, BranchData.ClassifiedPaths);

    internal static DataClassificationSet? Classification(ICustomAttributeProvider? provider)
    {
        var attributes = (provider is MemberInfo member
                ? Attribute.GetCustomAttributes(member, typeof(DataClassificationAttribute), inherit: true)
                : provider?.GetCustomAttributes(typeof(DataClassificationAttribute), true))
            ?.Cast<DataClassificationAttribute>().ToArray();
        return attributes is not { Length: > 0 } ? null :
            new DataClassificationSet(attributes.Select(attribute => attribute.Classification).ToArray());
    }

    public string Serialize(object? value)
    {
        try
        {
            return new Projection(services.GetService(typeof(IRedactorProvider)) as IRedactorProvider)
                .Project(value).GetRawText();
        }
        catch
        {
            return "\"\"";
        }
    }

    public string Serialize(string json, string typeName)
    {
        try
        {
            var type = WorkflowTypeIdentity.Resolve(typeName);
            // Never run an opaque converter just to inspect its output.
            if (type == null || type.IsDefined(typeof(JsonConverterAttribute), true))
                return "\"\"";
            return Serialize(JsonSerializer.Deserialize(json, type));
        }
        catch
        {
            return "\"\"";
        }
    }

    private sealed class Projection
    {
        private static readonly JsonElement Erased = JsonSerializer.SerializeToElement("");
        private readonly IRedactorProvider? _provider;
        private readonly bool _raw;
        private readonly JsonSerializerOptions _options;
        private int _depth;

        public Projection(IRedactorProvider? provider, bool raw = false)
        {
            _provider = provider;
            _raw = raw;
            _options = new JsonSerializerOptions
            {
                TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { Modify } }
            };
        }

        public JsonElement Project(object? value)
        {
            try
            {
                if (++_depth > 64)
                    throw new JsonException("Inspection depth exceeded");
                if (value == null)
                    return JsonSerializer.SerializeToElement<object?>(null);
                var type = value.GetType();
                _ = ClassifiedPaths(type);
                var info = _options.GetTypeInfo(type);
                if (info.Kind == JsonTypeInfoKind.Object)
                    return JsonSerializer.SerializeToElement(value, info);
                if (info.Kind == JsonTypeInfoKind.Enumerable)
                    return JsonSerializer.SerializeToElement(((IEnumerable)value).Cast<object?>().Select(Project).ToArray());
                // Dictionary keys, arbitrary JSON and custom converters do not provide member classification.
                if (info.Kind == JsonTypeInfoKind.None && Scalar(type) &&
                    !type.IsDefined(typeof(JsonConverterAttribute), true))
                    return JsonSerializer.SerializeToElement(value, info);
                return Unsupported();
            }
            finally
            {
                _depth--;
            }
        }

        // Replace the property contract, including its type: numbers and objects can become JSON strings.
        private void Modify(JsonTypeInfo info)
        {
            if (info.Kind != JsonTypeInfoKind.Object)
                return;
            for (var index = 0; index < info.Properties.Count; index++)
            {
                var original = info.Properties[index];
                if (original.Get == null)
                    continue;
                var classification = Classification(original.AttributeProvider);
                var replacement = info.CreateJsonPropertyInfo(typeof(JsonElement), original.Name);
                replacement.Order = original.Order;
                replacement.Get = owner =>
                {
                    try
                    {
                        if (original.AttributeProvider == null || original.CustomConverter != null || original.IsExtensionData)
                            return Unsupported();
                        var value = original.Get(owner);
                        return !_raw && classification is { } classified
                            ? Redact(value, classified) : Project(value);
                    }
                    catch when (!_raw)
                    {
                        return Erased;
                    }
                };
                info.Properties[index] = replacement;
            }
        }

        private JsonElement Redact(object? value, DataClassificationSet classification)
        {
            try
            {
                if (++_depth > 64)
                    throw new JsonException("Inspection depth exceeded");
                if (value == null)
                    return Erased;
                var info = _options.GetTypeInfo(value.GetType());
                if (info.Kind == JsonTypeInfoKind.Enumerable)
                    return JsonSerializer.SerializeToElement(((IEnumerable)value).Cast<object?>()
                        .Select(item => Redact(item, classification)).ToArray());
                var redactor = _provider?.GetRedactor(classification);
                if (redactor == null)
                    return Erased;
                // Inspect the complete value before handing it to a redactor. Unsupported shapes erase whole.
                var raw = new Projection(null, raw: true).Project(value);
                var input = value is string text ? text : raw.GetRawText();
                return JsonSerializer.SerializeToElement(redactor.Redact(input));
            }
            catch
            {
                return Erased;
            }
            finally
            {
                _depth--;
            }
        }

        private JsonElement Unsupported() => _raw
            ? throw new JsonException("The inspection contract is opaque") : Erased;

        private static bool Scalar(Type type) => type.IsPrimitive || type.IsEnum ||
            type == typeof(string) || type == typeof(decimal) || type == typeof(Guid) ||
            type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) ||
            type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(Uri);
    }
}
