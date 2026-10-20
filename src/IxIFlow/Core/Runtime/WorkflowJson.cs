using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace IxIFlow.Core.Runtime;

// Serializer metadata is strongly cached by options. A process-wide options instance must never
// own collectible types; the ephemeron lets the whole profile die with its load context.
internal static class WorkflowJson
{
    private static readonly ConditionalWeakTable<AssemblyLoadContext, Profiles> Contexts = new();
    private sealed class Profiles
    {
        internal readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> Options = new();
    }

    // JsonSerializer pools metadata across equivalent options. A unique resolver identity prevents
    // a bundle's options from reusing the process-wide default's cache (or another bundle's cache).
    private sealed class OwnedResolver(IJsonTypeInfoResolver source) : IJsonTypeInfoResolver
    {
        public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options) => source.GetTypeInfo(type, options);
    }

    private static AssemblyLoadContext? Of(Type? type)
    {
        if (type == null) return null;
        if (AssemblyLoadContext.GetLoadContext(type.Assembly) is { IsCollectible: true } context) return context;
        if (type.HasElementType) return Of(type.GetElementType());
        return type.IsConstructedGenericType ? type.GetGenericArguments().Select(Of).FirstOrDefault(item => item != null) : null;
    }

    internal static JsonSerializerOptions Options(JsonSerializerOptions? options = null, Type? type = null)
    {
        options ??= JsonSerializerOptions.Default;
        var context = Of(type) ?? WorkflowTypeIdentity.CurrentContext;
        if (context?.IsCollectible != true) return options;
        var profiles = Contexts.GetValue(context, _ => new());
        return profiles.Options.GetValue(options, source => new(source)
        { TypeInfoResolver = new OwnedResolver(source.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver()) });
    }

    internal static void Release(AssemblyLoadContext context) => Contexts.Remove(context);

    internal static string Serialize<T>(T value, JsonSerializerOptions? options = null) =>
        JsonSerializer.Serialize(value, Options(options, value?.GetType() ?? typeof(T)));
    internal static string Serialize(object? value, Type type, JsonSerializerOptions? options = null) =>
        JsonSerializer.Serialize(value, type, Options(options, type));
    internal static T? Deserialize<T>(string json, JsonSerializerOptions? options = null) =>
        JsonSerializer.Deserialize<T>(json, Options(options, typeof(T)));
    internal static object? Deserialize(string json, Type type, JsonSerializerOptions? options = null) =>
        JsonSerializer.Deserialize(json, type, Options(options, type));
    internal static JsonElement SerializeToElement<T>(T value, JsonSerializerOptions? options = null) =>
        JsonSerializer.SerializeToElement(value, Options(options, value?.GetType() ?? typeof(T)));
    internal static JsonElement SerializeToElement(object? value, JsonTypeInfo info) => JsonSerializer.SerializeToElement(value, info);
    internal static byte[] SerializeToUtf8Bytes<T>(T value, JsonSerializerOptions? options = null) =>
        JsonSerializer.SerializeToUtf8Bytes(value, Options(options, value?.GetType() ?? typeof(T)));
    internal static System.Text.Json.Nodes.JsonNode? SerializeToNode(object? value, Type type, JsonSerializerOptions? options = null) =>
        JsonSerializer.SerializeToNode(value, type, Options(options, type));
    internal static System.Text.Json.Nodes.JsonNode? SerializeToNode(object? value, JsonTypeInfo info) =>
        JsonSerializer.SerializeToNode(value, info);
}
