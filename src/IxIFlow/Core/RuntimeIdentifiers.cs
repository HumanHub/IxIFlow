using System.Runtime.CompilerServices;

namespace IxIFlow.Core;

internal static class RuntimeIdentifiers
{
    internal static void ValidateText(string value, [CallerArgumentExpression(nameof(value))] string? parameter = null)
    {
        ArgumentNullException.ThrowIfNull(value, parameter);

        if (value.Contains('\0') || value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])))
            throw new ArgumentException("Runtime identifiers cannot contain U+0000 or leading or trailing whitespace", parameter);

        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                index++;
            else if (char.IsSurrogate(value[index]))
                throw new ArgumentException("Runtime identifiers cannot contain unpaired UTF-16 surrogates", parameter);
        }
    }
}