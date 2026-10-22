using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IxIFlow.Core.Runtime;

internal sealed class PayloadBudget(long maximum)
{
    private long _bytes;
    public void Reserve(long bytes)
    {
        if (bytes > maximum - _bytes)
            throw new PayloadHydrationLimitException(maximum);
        _bytes += bytes;
    }
    public void Release(long bytes) => _bytes -= bytes;
}

internal sealed class PayloadHydrationLimitException(long maximum) : ResourceLimitExceededException(
    $"The run's hydrated and in-flight payloads exceed {maximum} bytes");

// One writer per run. StoredJson caches encoding/identity; saved parts cache their dependency list.
internal sealed class CheckpointPayloads(ExecutionLimits limits, PayloadBudget budget)
{
    private readonly Dictionary<string, StoredJson> _new = new(StringComparer.Ordinal);
    private readonly HashSet<string> _published = new(StringComparer.Ordinal);
    private HashSet<PayloadReference>? _observed;
    private readonly Dictionary<JsonSerializerOptions, JsonSerializerOptions> _options = new(ReferenceEqualityComparer.Instance);
    public IEnumerable<StoredJson> Pending => _new.Values;
    public void Known(IEnumerable<PayloadReference> references)
    {
        _published.Clear();
        foreach (var reference in references) _published.Add(reference.Digest);
    }
    public void Acknowledged()
    {
        foreach (var value in _new.Values) { budget.Release(value.Bytes); _published.Add(value.Identity.Digest); }
        _new.Clear();
    }
    public StoredJson Store(StoredJson value)
    {
        if (!value.IsInline || limits.PayloadThresholdBytes == 0 || value.Bytes <= limits.PayloadThresholdBytes)
            return value;
        if (value.Bytes > limits.MaxPayloadBytes)
            throw new WorkflowEngineException(WorkflowErrorKind.ResourceExhausted, "MaxPayloadBytes exceeded");
        var reference = value.Identity;
        if (!_published.Contains(reference.Digest) && !_new.ContainsKey(reference.Digest))
        {
            budget.Reserve(value.Bytes);
            _new.Add(reference.Digest, value);
        }
        var stored = StoredJson.FromReference(reference).WithDependencies(value.Dependencies);
        stored.Hydrated(value.InlineText);
        return stored;
    }
    // Only the StoredJson converter observes dependencies. Application JSON remains opaque,
    // including when claim checking is disabled on a resumed execution.
    public StoredJson Serialize<T>(T value, JsonSerializerOptions? options = null) =>
        SerializeWithPayloads(value, WorkflowJson.Options(options, value?.GetType() ?? typeof(T)));

    private StoredJson SerializeWithPayloads<T>(T value, JsonSerializerOptions? options)
    {
        var previous = _observed;
        _observed = [];
        try
        {
            var source = options ?? JsonSerializerOptions.Default;
            if (!_options.TryGetValue(source, out var configured))
            {
                configured = new JsonSerializerOptions(source);
                configured.Converters.Insert(0, new ValueConverter(this));
                _options.Add(source, configured);
            }
            var text = JsonSerializer.Serialize(value, configured);
            return StoredJson.Inline(text).WithDependencies(_observed.OrderBy(r => r.Digest, StringComparer.Ordinal).ToArray());
        }
        finally { _observed = previous; }
    }
    private sealed class ValueConverter(CheckpointPayloads owner) : JsonConverter<StoredJson>
    {
        private readonly StoredJsonConverter _wire = new();
        public override StoredJson Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            _wire.Read(ref reader, type, options);
        public override void Write(Utf8JsonWriter writer, StoredJson value, JsonSerializerOptions options)
        {
            var stored = owner.Store(value);
            if (stored.Reference is { } reference) owner._observed?.Add(reference);
            foreach (var dependency in stored.Dependencies) owner._observed?.Add(dependency);
            _wire.Write(writer, stored, options);
        }
    }

    internal static IReadOnlyList<PayloadReference> References(string trunk, IReadOnlyDictionary<string, StoredJson> parts) =>
        References(StoredJson.Inline(trunk), parts);

    internal static IReadOnlyList<PayloadReference> References(StoredJson trunk, IReadOnlyDictionary<string, StoredJson> parts)
    {
        if (trunk.HasDependencyMetadata && trunk.Dependencies.Count == 0 && parts.All(part =>
                part.Value.IsInline && part.Value.Dependencies.Count == 0 &&
                (!(part.Key.StartsWith("c/", StringComparison.Ordinal) || part.Key.StartsWith("f/", StringComparison.Ordinal) || part.Key.StartsWith("v/", StringComparison.Ordinal)) ||
                 part.Value.HasDependencyMetadata))) return [];
        var found = new HashSet<PayloadReference>();
        if (trunk.HasDependencyMetadata) found.UnionWith(trunk.Dependencies);
        else ReadReferences(trunk.InlineText, typeof(ExecutionCheckpoint), found);
        foreach (var (key, value) in parts)
        {
            if (value.Reference is { } reference)
            {
                // The catalog already holds the immutable closure. A commit names the outer part once.
                found.Add(reference);
                continue;
            }
            else if ((key.StartsWith("c/", StringComparison.Ordinal) || key.StartsWith("f/", StringComparison.Ordinal) || key.StartsWith("v/", StringComparison.Ordinal)) &&
                     !value.HasDependencyMetadata)
            {
                var dependencies = new HashSet<PayloadReference>();
                ReadReferences(value.InlineText, key.StartsWith("c/", StringComparison.Ordinal)
                    ? typeof(StoredCheckpoint.BranchPart) : key.StartsWith("v/", StringComparison.Ordinal)
                        ? typeof(InvocationRecordState) : typeof(List<StoredJson>), dependencies);
                value.WithDependencies(dependencies.ToArray());
            }
            foreach (var dependency in value.Dependencies) found.Add(dependency);
        }
        return found.OrderBy(r => r.Digest, StringComparer.Ordinal).ToArray();
    }
    internal static IReadOnlyList<PayloadReference> References<T>(string json)
    {
        var found = new HashSet<PayloadReference>();
        ReadReferences(json, typeof(T), found);
        return found.OrderBy(r => r.Digest, StringComparer.Ordinal).ToArray();
    }

    private static void ReadReferences(string json, Type type, HashSet<PayloadReference> found)
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new ReferenceReader(found));
        _ = JsonSerializer.Deserialize(json, type, options);
    }

    // Deserialization follows the engine's declared types. In particular an inline StoredJson
    // string is never parsed as another engine object, regardless of its application content.
    private sealed class ReferenceReader(HashSet<PayloadReference> found) : JsonConverter<StoredJson>
    {
        private readonly StoredJsonConverter _wire = new();
        public override StoredJson Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            var value = _wire.Read(ref reader, type, options);
            if (value.Reference is { } reference) found.Add(reference);
            return value;
        }
        public override void Write(Utf8JsonWriter writer, StoredJson value, JsonSerializerOptions options) =>
            _wire.Write(writer, value, options);
    }
}

internal sealed class PayloadReader(Func<PayloadReference, Task<ReadOnlyMemory<byte>>> read, PayloadBudget budget)
{
    private readonly Dictionary<PayloadReference, Task<string>> _cache = [];
    public async Task<string> ReadAsync(StoredJson value)
    {
        if (value.IsInline) return value.InlineText;
        if (!_cache.TryGetValue(value.Reference!, out var task))
        {
            budget.Reserve(value.Bytes);
            _cache.Add(value.Reference!, task = Fetch(value.Reference!));
        }
        var text = await task;
        value.Hydrated(text);
        return text;
    }
    private async Task<string> Fetch(PayloadReference reference)
    {
        try
        {
            var bytes = await read(reference);
            reference.Verify(bytes.Span);
            return Encoding.UTF8.GetString(bytes.Span);
        }
        catch (InvalidDataException error)
        {
            throw new WorkflowEngineException(WorkflowErrorKind.PayloadUnavailable,
                $"Payload {reference.Digest} ({reference.Bytes} bytes) is missing or corrupt", error);
        }
    }

    // Prefetch only the engine-owned saved values an operation is about to consume. Application objects,
    // ignored runtime state and queued ForEach items are never traversed.
    public Task HydrateAsync(object? value) => Visit(value, new HashSet<object>(ReferenceEqualityComparer.Instance));
    private async Task Visit(object? value, HashSet<object> seen)
    {
        if (value is null || value is string || value.GetType().IsValueType || !seen.Add(value)) return;
        if (value is StoredJson json) { await ReadAsync(json); return; }
        if (value is IDictionary dictionary)
        { foreach (var item in dictionary.Values) await Visit(item, seen); return; }
        if (value is IEnumerable sequence)
        { foreach (var item in sequence) await Visit(item, seen); return; }
        if (value.GetType().Assembly != typeof(ExecutionCheckpoint).Assembly || value is ForEachState) return;
        foreach (var property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (property.GetMethod != null && property.GetIndexParameters().Length == 0 &&
                property.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition != JsonIgnoreCondition.Always)
                await Visit(property.GetValue(value), seen);
    }
}
