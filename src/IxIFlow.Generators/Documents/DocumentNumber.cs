using System.Text.RegularExpressions;

namespace IxIFlow.Dsl.Documents;

/// <summary>A numeric default retained as authored text until its declared type converts it.</summary>
public sealed class DocumentNumber
{
    public DocumentNumber(string literal)
    {
        if (!IsLiteral(literal)) throw new ArgumentException("Expected a numeric literal", nameof(literal));
        Literal = literal;
    }

    public string Literal { get; }
    public override string ToString() => Literal;

    internal static bool IsLiteral(string? value) => value != null && Regex.IsMatch(value,
        @"\A[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?\z", RegexOptions.CultureInvariant);

    /// <summary>The equivalent JSON number, preserving every digit without floating point conversion.</summary>
    public string JsonLiteral
    {
        get
        {
            var value = Literal.TrimStart('+', '-');
            var exponent = value.IndexOfAny(new[] { 'e', 'E' });
            var suffix = exponent < 0 ? "" : value.Substring(exponent);
            var mantissa = exponent < 0 ? value : value.Substring(0, exponent);
            var point = mantissa.IndexOf('.');
            var integer = (point < 0 ? mantissa : mantissa.Substring(0, point)).TrimStart('0');
            var fraction = point < 0 ? "" : mantissa.Substring(point + 1);
            return (Literal[0] == '-' ? "-" : "") + (integer.Length == 0 ? "0" : integer) +
                (point < 0 ? "" : "." + (fraction.Length == 0 ? "0" : fraction)) + suffix;
        }
    }
}
