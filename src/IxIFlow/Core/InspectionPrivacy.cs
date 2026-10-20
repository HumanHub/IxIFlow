using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IxIFlow.Core;

internal static class InspectionPrivacy
{
    // Defense in depth for copied capabilities. Classification-based redaction belongs to the redaction stack.
    private static readonly Regex Canonical = new("WyIzIiwi[A-Za-z0-9_+/=-]*", RegexOptions.CultureInvariant);
    private static readonly Regex Encoded = new("[A-Za-z0-9_+/=-]{20,}", RegexOptions.CultureInvariant);
    public static T Clean<T>(T value, IReadOnlyList<string> waitIds)
    {
        var node = JsonSerializer.SerializeToNode(value)!;
        Visit(node);
        return node.Deserialize<T>()!;
        string Scrub(string text)
        {
            text = Canonical.Replace(text, m =>
            {
                // Tokens in URLs can adjoin base64 characters. Find the JSON token's actual end.
                for (var length = Math.Min(m.Length, 2000); length >= 12; length--)
                    if (ReplyToken.TryRead(m.Value[..length], out _, out _, out _))
                        return "[reply capability]" + m.Value[length..];
                return m.Value;
            });
            text = Encoded.Replace(text, m => ReplyToken.TryRead(m.Value, out _, out _, out _) ? "[reply capability]" : m.Value);
            foreach (var id in waitIds) text = text.Replace(id, "[wait capability]", StringComparison.Ordinal);
            return text;
        }
        void Visit(JsonNode item)
        {
            if (item is JsonObject obj)
                foreach (var pair in obj.ToArray())
                {
                    var key = Scrub(pair.Key);
                    if (key != pair.Key) { obj.Remove(pair.Key); obj[key] = pair.Value; }
                    if (pair.Value != null) Visit(pair.Value);
                }
            else if (item is JsonArray array)
                foreach (var child in array.ToArray()) { if (child != null) Visit(child); }
            else if (item is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                item.ReplaceWith(JsonValue.Create(Scrub(text)));
        }
    }
}
