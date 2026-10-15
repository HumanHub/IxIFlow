using System.Text;
using System.Text.Json;

namespace IxIFlow.Core;

/// <summary>
/// The opaque address of one wait (D-33): its instance, its engine-issued ID and, for a request that
/// declared one, the digest of what it asks about. Holding a token is the right to answer that wait,
/// so tokens travel only to whoever should answer, such as in an approval link.
/// </summary>
public static class ReplyToken
{
    private const string Version = "1";

    public static string Create(string instanceId, string waitId, string? subject = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(waitId);
        var json = JsonSerializer.SerializeToUtf8Bytes(new[] { Version, instanceId, waitId, subject });
        return Convert.ToBase64String(json).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>The digest a token carries for what a request is about.</summary>
    public static string Digest(string subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(subject)));
    }

    /// <summary>Reads a token, or returns false when the text is not one.</summary>
    public static bool TryRead(string? token, out string instanceId, out string waitId, out string? subject)
    {
        instanceId = waitId = "";
        subject = null;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 2000)
            return false;
        try
        {
            var base64 = token.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
            var parts = JsonSerializer.Deserialize<string?[]>(Encoding.UTF8.GetString(Convert.FromBase64String(base64)));
            if (parts is not [Version, { Length: > 0 } instance, { Length: > 0 } wait, var digest])
                return false;
            (instanceId, waitId, subject) = (instance, wait, digest);
            return true;
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>An open wait as discovery lists it (D-14): what it waits for, until when, and how to answer it.</summary>
public sealed record OpenWait(
    string InstanceId,
    string WaitId,
    WaitKind Kind,
    string Key,
    string EventType,
    string? EventSchema,
    DateTime? DeadlineUtc,
    DateTime OpenedAtUtc,
    string Token);
