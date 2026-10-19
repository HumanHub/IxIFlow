using System.Text;

namespace IxIFlow.Core;

/// <summary>A plainly encoded diagnostic wait reference. A cursor grants no reply permission.</summary>
public static class WaitPageCursor
{
    public static string Create(string waitRef)
    {
        if (!IsReference(waitRef)) throw new ArgumentException("Invalid wait reference", nameof(waitRef));
        return Encode(Encoding.UTF8.GetBytes(waitRef));
    }

    public static bool TryRead(string? cursor, out string waitRef)
    {
        waitRef = "";
        if (string.IsNullOrEmpty(cursor) || cursor.Length != 22 ||
            cursor.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) return false;
        try
        {
            var bytes = Convert.FromBase64String(cursor.Replace('-', '+').Replace('_', '/') + "==");
            var reference = Encoding.UTF8.GetString(bytes);
            if (Encode(bytes) != cursor || !IsReference(reference)) return false;
            waitRef = reference;
            return true;
        }
        catch (FormatException) { return false; }
    }

    private static bool IsReference(string? value) => value is { Length: 16 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
