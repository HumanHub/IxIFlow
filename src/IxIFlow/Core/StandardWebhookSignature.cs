using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace IxIFlow.Core;

/// <summary>The shared Standard Webhooks v1 byte-level signing and verification protocol.</summary>
public static class StandardWebhookSignature
{
    public static byte[] DecodeSecret(string secret)
    {
        if (secret == null || !secret.StartsWith("whsec_", StringComparison.Ordinal))
            throw new ArgumentException("A Standard Webhooks secret must start with whsec_");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(secret[6..]); }
        catch (FormatException) { throw new ArgumentException("A Standard Webhooks secret must contain base64"); }
        if (bytes.Length is < 24 or > 64)
            throw new ArgumentException("A Standard Webhooks secret must contain 24 to 64 bytes");
        return bytes;
    }

    public static string Sign(ReadOnlySpan<byte> secret, string id, string timestamp, ReadOnlySpan<byte> body) =>
        "v1," + Convert.ToBase64String(Digest(secret, id, timestamp, body));

    public static bool Verify(IEnumerable<byte[]> secrets, string id, string timestamp, string signatures,
        ReadOnlySpan<byte> body, DateTimeOffset now, TimeSpan tolerance)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200 || signatures.Length > 4096 ||
            !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            tolerance <= TimeSpan.Zero || Math.Abs((double)now.ToUnixTimeSeconds() - seconds) > tolerance.TotalSeconds)
            return false;
        var candidates = new List<byte[]>();
        foreach (var signature in signatures.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (signature.StartsWith("v1,", StringComparison.Ordinal))
            {
                try { candidates.Add(Convert.FromBase64String(signature[3..])); }
                catch (FormatException) { }
            }
        foreach (var key in secrets)
        {
            var expected = Digest(key, id, timestamp, body);
            foreach (var candidate in candidates)
                if (CryptographicOperations.FixedTimeEquals(expected, candidate)) return true;
        }
        return false;
    }

    private static byte[] Digest(ReadOnlySpan<byte> secret, string id, string timestamp, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.UTF8.GetBytes(id + "." + timestamp + ".");
        var signed = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signed, 0);
        body.CopyTo(signed.AsSpan(prefix.Length));
        return HMACSHA256.HashData(secret, signed);
    }
}
