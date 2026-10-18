using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace IxIFlow.Generators.Tests;

public sealed class WorkflowYamlSchemaValueTests
{
    [Theory]
    [InlineData("", JsonValueKind.Null)]
    [InlineData("~", JsonValueKind.Null)]
    [InlineData("NULL", JsonValueKind.Null)]
    [InlineData("True", JsonValueKind.True)]
    [InlineData("FALSE", JsonValueKind.False)]
    [InlineData("0x10", JsonValueKind.Number)]
    [InlineData("0o10", JsonValueKind.Number)]
    [InlineData("012", JsonValueKind.Number)]
    [InlineData("-12", JsonValueKind.Number)]
    [InlineData("1e2", JsonValueKind.Number)]
    [InlineData(".5", JsonValueKind.Number)]
    [InlineData("+.INF", JsonValueKind.Number)]
    [InlineData("-.inf", JsonValueKind.Number)]
    [InlineData(".NaN", JsonValueKind.Number)]
    [InlineData("'true'", JsonValueKind.String)]
    [InlineData("\"1\"", JsonValueKind.String)]
    [InlineData("yes", JsonValueKind.String)]
    [InlineData("2026-10-17", JsonValueKind.String)]
    public void YamlConversionUsesCoreSchemaTypes(string scalar, JsonValueKind kind) =>
        Assert.Equal(kind, Read("value: " + scalar).GetProperty("value").ValueKind);

    internal static JsonElement Read(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return JsonSerializer.SerializeToElement(Convert(stream.Documents.Single().RootNode));

        static JsonNode? Convert(YamlNode node) => node switch
        {
            YamlMappingNode mapping => new JsonObject(mapping.Children.Select(pair =>
                new KeyValuePair<string, JsonNode?>(((YamlScalarNode)pair.Key).Value!, Convert(pair.Value)))),
            YamlSequenceNode sequence => new JsonArray(sequence.Children.Select(Convert).ToArray()),
            YamlScalarNode scalar => Scalar(scalar),
            _ => throw new InvalidOperationException("Unsupported YAML node")
        };
    }

    private static JsonNode? Scalar(YamlScalarNode scalar)
    {
        var value = scalar.Value ?? "";
        if (scalar.Style != ScalarStyle.Plain && scalar.Style != ScalarStyle.Any) return JsonValue.Create(value);
        if (value is "" or "~" or "null" or "Null" or "NULL") return null;
        if (value is "true" or "True" or "TRUE") return JsonValue.Create(true);
        if (value is "false" or "False" or "FALSE") return JsonValue.Create(false);
        if (Regex.IsMatch(value, @"^[+-]?[0-9]+$")) return JsonNode.Parse(BigInteger.Parse(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
        if (Regex.IsMatch(value, @"^0[ox][0-9a-fA-F]+$") &&
            (value[1] == 'x' || value.Substring(2).All(character => character is >= '0' and <= '7')))
        {
            var integer = BigInteger.Zero;
            foreach (var digit in value.Substring(2)) integer = integer * (value[1] == 'x' ? 16 : 8) + int.Parse(digit.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return JsonNode.Parse(integer.ToString(CultureInfo.InvariantCulture));
        }
        if (Regex.IsMatch(value, @"^[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?$"))
        {
            var number = double.Parse(value, CultureInfo.InvariantCulture);
            return JsonValue.Create(double.IsFinite(number) ? number : 0.5d);
        }
        // JSON has no non-finite numbers; a finite non-integer surrogate preserves schema typing.
        if (Regex.IsMatch(value, @"^(?:[+-]?\.(?:inf|Inf|INF)|\.(?:nan|NaN|NAN))$")) return JsonValue.Create(0.5d);
        return JsonValue.Create(value);
    }
}
