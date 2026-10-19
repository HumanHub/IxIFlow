using System.Text;

namespace IxIFlow.Core;

/// <summary>An authenticated, encrypted wait-page boundary. A cursor grants no reply permission.</summary>
public static class WaitPageCursor
{
    public static string Create(ReplyTokenKeyRing keys, string waitId)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentException.ThrowIfNullOrWhiteSpace(waitId);
        return Encode(keys.ProtectCursor(Encoding.UTF8.GetBytes(waitId)));
    }

    public static bool TryRead(ReplyTokenKeyRing keys, string? cursor, out string waitId)
    {
        ArgumentNullException.ThrowIfNull(keys);
        waitId = "";
        if (string.IsNullOrEmpty(cursor) || cursor.Length > 2000 ||
            cursor.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) return false;
        try
        {
            var bytes = Convert.FromBase64String(cursor.Replace('-', '+').Replace('_', '/').PadRight((cursor.Length + 3) / 4 * 4, '='));
            if (Encode(bytes) != cursor || !keys.TryUnprotectCursor(bytes, out var payload)) return false;
            waitId = Encoding.UTF8.GetString(payload);
            return waitId.Length > 0;
        }
        catch (FormatException) { return false; }
    }

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
