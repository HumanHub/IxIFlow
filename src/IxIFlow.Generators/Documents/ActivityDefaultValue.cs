using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IxIFlow.Dsl.Documents;

/// <summary>Conversion text for scalar metadata, separate from the authored serialization text.</summary>
internal static class ActivityDefaultValue
{
    internal static string ConversionType(string declaredType, ITypeSymbol? boundType = null)
    {
        if (boundType != null)
            return ConversionSymbol(boundType).ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        var type = ConversionSyntax(SyntaxFactory.ParseTypeName(declaredType));

        return NumericType(type) ?? type.WithoutTrivia().ToString();
    }

    private static ITypeSymbol ConversionSymbol(ITypeSymbol type) =>
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            ? nullable.TypeArguments[0]
            : type;

    private static TypeSyntax ConversionSyntax(TypeSyntax type) => type switch
    {
        NullableTypeSyntax nullable => ConversionSyntax(nullable.ElementType),
        _ when SystemName(type) is GenericNameSyntax generic && generic.Identifier.ValueText == "Nullable" &&
            generic.TypeArgumentList.Arguments.Count == 1 => ConversionSyntax(generic.TypeArgumentList.Arguments[0]),
        _ => type
    };

    // Before binding, only keywords and global::System names identify framework
    // types unambiguously. Ordinary identifiers and System may be shadowed.
    private static SimpleNameSyntax? SystemName(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax qualified when qualified.Left is AliasQualifiedNameSyntax alias &&
            alias.Alias.Identifier.ValueText == "global" && alias.Name.Identifier.ValueText == "System"
            => qualified.Right,
        _ => null
    };

    private static string? NumericType(TypeSyntax type) => type is PredefinedTypeSyntax predefined
        ? predefined.Keyword.Kind() switch
        {
            SyntaxKind.SByteKeyword => "sbyte", SyntaxKind.ByteKeyword => "byte",
            SyntaxKind.ShortKeyword => "short", SyntaxKind.UShortKeyword => "ushort",
            SyntaxKind.IntKeyword => "int", SyntaxKind.UIntKeyword => "uint",
            SyntaxKind.LongKeyword => "long", SyntaxKind.ULongKeyword => "ulong",
            SyntaxKind.FloatKeyword => "float", SyntaxKind.DoubleKeyword => "double", SyntaxKind.DecimalKeyword => "decimal",
            _ => null
        }
        : (SystemName(type) as IdentifierNameSyntax)?.Identifier.ValueText switch
        {
            "SByte" => "sbyte", "Byte" => "byte", "Int16" => "short", "UInt16" => "ushort",
            "Int32" => "int", "UInt32" => "uint", "Int64" => "long", "UInt64" => "ulong",
            "Single" => "float", "Double" => "double", "Decimal" => "decimal",
            _ => null
        };

    private static string? NumericType(ITypeSymbol type) => ConversionSymbol(type).SpecialType switch
    {
        SpecialType.System_SByte => "sbyte", SpecialType.System_Byte => "byte",
        SpecialType.System_Int16 => "short", SpecialType.System_UInt16 => "ushort",
        SpecialType.System_Int32 => "int", SpecialType.System_UInt32 => "uint",
        SpecialType.System_Int64 => "long", SpecialType.System_UInt64 => "ulong",
        SpecialType.System_Single => "float", SpecialType.System_Double => "double", SpecialType.System_Decimal => "decimal",
        _ => null
    };

    private static TypeConverter? IntegerConverter(string? type) => type switch
    {
        "sbyte" => new SByteConverter(), "byte" => new ByteConverter(),
        "short" => new Int16Converter(), "ushort" => new UInt16Converter(),
        "int" => new Int32Converter(), "uint" => new UInt32Converter(),
        "long" => new Int64Converter(), "ulong" => new UInt64Converter(),
        _ => null
    };

    private static bool TryIntegerString(string literal, TypeConverter converter, out string text)
    {
        text = literal;

        try
        {
            // These known framework converters retain their whitespace, radix and
            // signed-hex semantics, and enforce the target type's range exactly.
            text = ((IFormattable)converter.ConvertFromInvariantString(literal)!).ToString(null, CultureInfo.InvariantCulture);

            return true;
        }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException or NotSupportedException)
        {
            return false;
        }
    }

    internal static bool TryGetText(ActivityMemberDocument member, out string text, ITypeSymbol? boundType = null)
    {
        text = Convert.ToString(member.Default, CultureInfo.InvariantCulture) ?? "";

        if (member.Default == null)
            return true;

        var type = boundType == null
            ? NumericType(ConversionSyntax(SyntaxFactory.ParseTypeName(member.Type)))
            : NumericType(boundType);

        if (member.Default is string literal && IntegerConverter(type) is { } converter)
            return TryIntegerString(literal, converter, out text);

        var bounds = type switch
        {
            "sbyte" => ((BigInteger)sbyte.MinValue, (BigInteger)sbyte.MaxValue),
            "byte" => ((BigInteger)byte.MinValue, (BigInteger)byte.MaxValue),
            "short" => ((BigInteger)short.MinValue, (BigInteger)short.MaxValue),
            "ushort" => ((BigInteger)ushort.MinValue, (BigInteger)ushort.MaxValue),
            "int" => ((BigInteger)int.MinValue, (BigInteger)int.MaxValue),
            "uint" => ((BigInteger)uint.MinValue, (BigInteger)uint.MaxValue),
            "long" => ((BigInteger)long.MinValue, (BigInteger)long.MaxValue),
            "ulong" => ((BigInteger)ulong.MinValue, (BigInteger)ulong.MaxValue),
            _ => ((BigInteger)0, (BigInteger)0)
        };

        if (bounds.Item2 != 0)
            return TryInteger(text, bounds.Item1, bounds.Item2, out text);

        if (type == "double")
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
                double.IsNaN(number) || double.IsInfinity(number))
                return false;

            text = number.ToString("R", CultureInfo.InvariantCulture);
        }
        else if (type == "float")
        {
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
                float.IsNaN(number) || float.IsInfinity(number))
                return false;

            text = number.ToString("R", CultureInfo.InvariantCulture);
        }
        else if (type == "decimal")
            return TryDecimal(text, out text);

        return true;
    }

    private static bool TryInteger(string literal, BigInteger minimum, BigInteger maximum, out string text)
    {
        text = literal;

        if (!TryParts(literal, out var negative, out var significant, out var scale))
            return false;

        // Bound the digit count before allocating zeros, including arbitrarily large exponents.
        if (scale < 0 || significant.Length + scale > 20)
            return false;

        var integer = BigInteger.Parse(significant + new string('0', (int)scale), CultureInfo.InvariantCulture);

        if (negative)
            integer = -integer;

        if (integer < minimum || integer > maximum)
            return false;

        text = integer.ToString(CultureInfo.InvariantCulture);

        return true;
    }

    private static bool TryDecimal(string literal, out string text)
    {
        text = literal;

        if (!TryParts(literal.Trim(), out var negative, out var significant, out var scale) ||
            scale < -28 || significant.Length + BigInteger.Max(scale, 0) > 29)
            return false;

        var coefficient = BigInteger.Parse(significant, CultureInfo.InvariantCulture);

        if (scale > 0)
            coefficient *= BigInteger.Pow(10, (int)scale);

        if (coefficient > (BigInteger)decimal.MaxValue)
            return false;

        // Construct the exact 96-bit coefficient and scale; no parser rounding or underflow.
        var mask = (BigInteger)uint.MaxValue;

        var number = new decimal(unchecked((int)(uint)(coefficient & mask)),
            unchecked((int)(uint)((coefficient >> 32) & mask)), unchecked((int)(uint)(coefficient >> 64)),
            negative, scale < 0 ? (byte)-scale : (byte)0);

        text = number.ToString(CultureInfo.InvariantCulture);

        return true;
    }

    private static bool TryParts(string literal, out bool negative, out string significant, out BigInteger scale)
    {
        negative = false;
        significant = "0";
        scale = BigInteger.Zero;

        if (!DocumentNumber.IsLiteral(literal))
            return false;

        negative = literal[0] == '-';
        var unsigned = literal.TrimStart('+', '-');
        var exponentIndex = unsigned.IndexOfAny(new[] { 'e', 'E' });
        var mantissa = exponentIndex < 0 ? unsigned : unsigned.Substring(0, exponentIndex);

        var exponent = exponentIndex < 0
            ? BigInteger.Zero
            : BigInteger.Parse(unsigned.Substring(exponentIndex + 1), CultureInfo.InvariantCulture);

        var point = mantissa.IndexOf('.');
        var digits = mantissa.Replace(".", "").TrimStart('0');

        if (digits.Length == 0)
            return true;

        scale = exponent - (point < 0 ? 0 : mantissa.Length - point - 1);
        significant = digits.TrimEnd('0');
        scale += digits.Length - significant.Length;

        return true;
    }
}