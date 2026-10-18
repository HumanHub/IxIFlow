using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace IxIFlow.Analyzers;

/// <summary>Checks attribute constants without loading or running the consumer's code.</summary>
internal static class ActivityDefaults
{
    public static bool CanConvert(Compilation compilation, AttributeData attribute, ITypeSymbol target)
    {
        if (attribute.ConstructorArguments.Length == 0)
            return false;
        var argument = attribute.ConstructorArguments[0];
        object? value = argument.Value;
        ITypeSymbol? source = argument.Type;
        if (attribute.ConstructorArguments.Length == 2 && value is ITypeSymbol declared)
        {
            source = declared;
            value = attribute.ConstructorArguments[1].Value;
            if (RuntimeType(declared) is { } declaredType)
            {
                try { value = ConvertTo(value, declaredType); }
                catch (Exception) { return false; }
            }
            else if (declared.TypeKind == TypeKind.Enum && !EnumValue(declared, value))
                return false;
        }
        if (value == null)
            return target.IsReferenceType || target.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
        if (target is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            target = nullable.TypeArguments[0];
        if (source != null && compilation.ClassifyCommonConversion(source, target).IsImplicit)
            return true;
        if (target.TypeKind == TypeKind.Enum)
            return EnumValue(target, value);
        if (RuntimeType(target) is { } type)
        {
            try { _ = ConvertTo(value, type); return true; }
            catch (Exception) { return false; }
        }
        // A custom converter is checked by MappingRules at build, where the consumer's type can be loaded.
        return target.GetAttributes().Any(each =>
            each.AttributeClass?.ToDisplayString() == "System.ComponentModel.TypeConverterAttribute");
    }

    private static object? ConvertTo(object? value, Type target)
    {
        if (value == null || target.IsInstanceOfType(value))
            return value;
        var converter = TypeDescriptor.GetConverter(target);
        return converter.CanConvertFrom(value.GetType())
            ? converter.ConvertFrom(null, CultureInfo.InvariantCulture, value)
            : Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
    }

    private static bool EnumValue(ITypeSymbol target, object? value) => value is string name
        ? long.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ||
          name.Split(',').All(part => target.GetMembers(part.Trim()).OfType<IFieldSymbol>().Any(field => field.HasConstantValue))
        : value != null && Type.GetTypeCode(value.GetType()) is >= TypeCode.SByte and <= TypeCode.UInt64;

    private static Type? RuntimeType(ITypeSymbol type) => type.SpecialType switch
    {
        SpecialType.System_Object => typeof(object),
        SpecialType.System_String => typeof(string),
        SpecialType.System_Boolean => typeof(bool),
        SpecialType.System_Char => typeof(char),
        SpecialType.System_SByte => typeof(sbyte),
        SpecialType.System_Byte => typeof(byte),
        SpecialType.System_Int16 => typeof(short),
        SpecialType.System_UInt16 => typeof(ushort),
        SpecialType.System_Int32 => typeof(int),
        SpecialType.System_UInt32 => typeof(uint),
        SpecialType.System_Int64 => typeof(long),
        SpecialType.System_UInt64 => typeof(ulong),
        SpecialType.System_Single => typeof(float),
        SpecialType.System_Double => typeof(double),
        SpecialType.System_Decimal => typeof(decimal),
        SpecialType.System_DateTime => typeof(DateTime),
        _ => type.ToDisplayString() switch
        {
            "System.TimeSpan" => typeof(TimeSpan),
            "System.Guid" => typeof(Guid),
            "System.DateTimeOffset" => typeof(DateTimeOffset),
            "System.Uri" => typeof(Uri),
            "System.Version" => typeof(Version),
            _ => null
        }
    };
}
