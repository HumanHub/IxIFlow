using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IxIFlow.Core;

/// <summary>The SHA-256 identity and exact UTF-8 length of an immutable payload.</summary>
public sealed record PayloadReference
{
    public string Digest { get; }
    public long Bytes { get; }

    [JsonConstructor]
    public PayloadReference(string digest, long bytes)
    {
        if (digest is null || digest.Length != 71 || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
            digest.AsSpan(7).ContainsAnyExcept("0123456789abcdef"))
            throw new ArgumentException("Expected sha256: followed by 64 lowercase hex digits", nameof(digest));
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        Digest = digest;
        Bytes = bytes;
    }

    public static PayloadReference Of(ReadOnlySpan<byte> utf8Json) =>
        new("sha256:" + Convert.ToHexStringLower(SHA256.HashData(utf8Json)), utf8Json.Length);

    public void Verify(ReadOnlySpan<byte> body)
    {
        if (body.Length != Bytes || Of(body).Digest != Digest)
            throw new InvalidDataException($"Payload {Digest} failed length or digest verification");
    }
}

/// <summary>
/// Immutable JSON at a persistence seam. Inline values cache their UTF-8 size and content identity.
/// Reading a reference requires asynchronous host hydration; it must never be interpreted as application JSON.
/// </summary>
[JsonConverter(typeof(StoredJsonConverter))]
public sealed record StoredJson
{
    private readonly string? _inline;
    private readonly Lazy<PayloadReference>? _identity;
    private readonly Lazy<byte[]>? _encoded;
    private IReadOnlyList<PayloadReference>? _dependencies;
    private string? _hydrated;
    private readonly int _referenceBytes;
    public PayloadReference? Reference { get; }
    public bool IsInline => Reference is null;
    public long Bytes { get; }
    public PayloadReference Identity => Reference ?? _identity!.Value;
    internal ReadOnlyMemory<byte> Encoded => _encoded!.Value;
    internal long CheckpointBytes => IsInline ? Bytes : _referenceBytes;
    internal IReadOnlyList<PayloadReference> Dependencies => _dependencies ?? [];
    internal bool HasDependencyMetadata => _dependencies != null;

    private StoredJson(string text)
    {
        _inline = text;
        Bytes = Encoding.UTF8.GetByteCount(text);
        _encoded = new(() => Encoding.UTF8.GetBytes(text));
        _identity = new(() => PayloadReference.Of(_encoded.Value));
    }

    private StoredJson(PayloadReference reference)
    {
        Reference = reference;
        Bytes = reference.Bytes;
        _referenceBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(this));
    }
    public static StoredJson Inline(string text) => new(text ?? throw new ArgumentNullException(nameof(text)));
    public static StoredJson FromReference(PayloadReference reference) =>
        new(reference ?? throw new ArgumentNullException(nameof(reference)));
    public string InlineText => _inline ?? _hydrated ?? throw new InvalidOperationException("Payload reference requires host hydration");
    internal void Hydrated(string text) => _hydrated = text;
    internal StoredJson WithDependencies(IReadOnlyList<PayloadReference> dependencies)
    { _dependencies = dependencies; return this; }

    public bool Equals(StoredJson? other) => other is not null && _inline == other._inline && Reference == other.Reference;
    public override int GetHashCode() => HashCode.Combine(_inline, Reference);
}

/// <summary>One unambiguous wire form: an inline JSON string, or an object containing a payload reference.</summary>
public sealed class StoredJsonConverter : JsonConverter<StoredJson>
{
    public override StoredJson Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return StoredJson.Inline(reader.GetString()!);
        using var json = JsonDocument.ParseValue(ref reader);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
            !root.TryGetProperty("reference", out var reference))
            throw new JsonException("Expected inline JSON text or a payload reference");
        if (reference.ValueKind != JsonValueKind.Object || reference.EnumerateObject().Count() != 2 ||
            !reference.TryGetProperty("digest", out var digest) || digest.ValueKind != JsonValueKind.String ||
            !reference.TryGetProperty("bytes", out var bytes) || bytes.ValueKind != JsonValueKind.Number || !bytes.TryGetInt64(out var length))
            throw new JsonException("Invalid payload reference");
        try { return StoredJson.FromReference(new PayloadReference(digest.GetString()!, length)); }
        catch (ArgumentException error) { throw new JsonException("Invalid payload reference", error); }
    }

    public override void Write(Utf8JsonWriter writer, StoredJson value, JsonSerializerOptions options)
    {
        if (value.IsInline) { writer.WriteStringValue(value.InlineText); return; }
        writer.WriteStartObject();
        writer.WriteStartObject("reference");
        writer.WriteString("digest", value.Reference!.Digest);
        writer.WriteNumber("bytes", value.Reference.Bytes);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}
