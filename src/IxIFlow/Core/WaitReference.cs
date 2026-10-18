using System.Security.Cryptography;
using System.Text;

namespace IxIFlow.Core;

/// <summary>Diagnostic correlation for a wait, without exposing its reply capability (D-33).</summary>
public static class WaitReference
{
    /// <summary>The first 16 lowercase hex characters of SHA-256 over UTF-8 instanceId + ":" + waitId.</summary>
    public static string Of(string instanceId, string waitId)
    {
        ArgumentNullException.ThrowIfNull(instanceId);
        ArgumentNullException.ThrowIfNull(waitId);
        // Refused events can target invalid IDs; preserve their diagnostic text without normalizing it.
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(instanceId + ":" + waitId)))[..16];
    }
}
