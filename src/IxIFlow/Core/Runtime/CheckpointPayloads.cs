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
    { foreach (var reference in references) _published.Add(reference.Digest); }
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
    public StoredJson Serialize<T>(T value, JsonSerializerOptions? options = null)
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

    internal static IReadOnlyList<PayloadReference> References(string trunk, IReadOnlyDictionary<string, StoredJson> parts)
    {
        var found = new HashSet<PayloadReference>();
        Scan(trunk, found);
        foreach (var (key, value) in parts)
        {
            if (value.Reference is { } reference)
            {
                // The catalog already holds the immutable closure. A commit names the outer part once.
                found.Add(reference);
                continue;
            }
            else if ((key.StartsWith("c/", StringComparison.Ordinal) || key.StartsWith("f/", StringComparison.Ordinal)) &&
                     !value.HasDependencyMetadata)
            {
                var dependencies = new HashSet<PayloadReference>();
                Scan(value.InlineText, dependencies);
                value.WithDependencies(dependencies.ToArray());
            }
            foreach (var dependency in value.Dependencies) found.Add(dependency);
        }
        return found.OrderBy(r => r.Digest, StringComparer.Ordinal).ToArray();
    }
    private static void Scan(string json, HashSet<PayloadReference> found)
    {
        using var document = JsonDocument.Parse(json);
        Visit(document.RootElement);
        void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.EnumerateObject().Count() == 1 && element.TryGetProperty("reference", out var reference) &&
                    reference.ValueKind == JsonValueKind.Object && reference.TryGetProperty("digest", out var digest) &&
                    reference.TryGetProperty("bytes", out var bytes))
                { found.Add(new(digest.GetString()!, bytes.GetInt64())); return; }
                foreach (var property in element.EnumerateObject()) Visit(property.Value);
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var item in element.EnumerateArray()) Visit(item);
        }
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
