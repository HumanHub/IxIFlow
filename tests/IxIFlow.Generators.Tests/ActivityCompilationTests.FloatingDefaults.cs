using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.IO.Compression;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.Loader;
using System.Text;
using IxIFlow.ActivitySdk;
using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Dsl.Compilation;
using IxIFlow.Dsl.Documents;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace IxIFlow.Generators.Tests;

public sealed partial class ActivityCompilationTests
{
    [Trait("Feature", "authoring")]
    [Trait("Spec", "F-19")]
    public sealed class FloatingDefaults
    {
        [Theory]
        [InlineData("decimal", "0.1234567890123456789012345678")]
        [InlineData("decimal", "79228162514264337593543950335")]
        [InlineData("long", "9223372036854775807")]
        [InlineData("double", "1.234567890123456789e100")]
        [InlineData("float", "1.23456789e20")]
        public void NumericDefaultsKeepExactLiteralAndTypedValueThroughBothReaders(string type, string literal)
        {
            var yaml = "activity: {key: acme.defaults, version: 1}\ninput: {Value: {type: " + type +
                ", default: " + literal + "}}\nbody: |\n  return;\n";

            var json = "{\"activity\":{\"key\":\"acme.defaults\",\"version\":1},\"input\":{\"Value\":{\"type\":\"" + type +
                "\",\"default\":" + literal + "}},\"body\":\"return;\\n\"}";

            foreach (var document in new[]
                     {
                         Dsl.Documents.WorkflowYamlSerializer.ReadActivity(yaml), Dsl.Documents.WorkflowJsonSerializer.ReadActivity(json)
                     })
            {
                var writtenJson = Dsl.Documents.WorkflowJsonSerializer.Write(document);
                using var parsed = System.Text.Json.JsonDocument.Parse(writtenJson);
                Assert.Equal(literal, parsed.RootElement.GetProperty("input").GetProperty("Value").GetProperty("default").GetRawText());

                var writtenYaml =
                    Dsl.Documents.WorkflowYamlSerializer.Write(Dsl.Documents.WorkflowJsonSerializer.ReadActivity(writtenJson));

                Assert.Contains(literal, writtenYaml);
                var result = ActivityCompilation.Compile(writtenYaml);
                Assert.True(result.Success, string.Join("\n", result.Diagnostics));
                var context = new AssemblyLoadContext("exact-default", isCollectible: true);

                try
                {
                    var assembly = context.LoadFromStream(new MemoryStream(result.Assembly));

                    var actual = assembly.GetType("IxIFlow.Activities.Acme.Defaults")!.GetProperty("Value")!
                        .GetCustomAttribute<DefaultValueAttribute>()!.Value;

                    object expected = type switch
                    {
                        "decimal" => decimal.Parse(literal, System.Globalization.CultureInfo.InvariantCulture),
                        "long" => long.Parse(literal, System.Globalization.CultureInfo.InvariantCulture),
                        "double" => double.Parse(literal, System.Globalization.CultureInfo.InvariantCulture),
                        _ => float.Parse(literal, System.Globalization.CultureInfo.InvariantCulture)
                    };

                    Assert.Equal(expected, actual);
                }
                finally
                {
                    context.Unload();
                }
            }
        }

        [Theory]
        [InlineData("double", "1e400")]
        [InlineData("double", "-1e400")]
        [InlineData("double?", "1e400")]
        [InlineData("double?", "-1e400")]
        [InlineData("float", "1e400")]
        [InlineData("float", "-1e400")]
        [InlineData("float?", "1e400")]
        [InlineData("float?", "-1e400")]
        [InlineData("float", "3.402824e38")]
        [InlineData("double", "\"NaN\"")]
        [InlineData("float", "\"Infinity\"")]
        [InlineData("double?", "\"-Infinity\"")]
        public void NonFiniteFloatingDefaultsAreRefusedThroughBothReaders(string type, string literal) =>
            AssertInvalidNumericDefault(type, literal);

        [Theory]
        [InlineData("double", "1.7976931348623157e308")]
        [InlineData("double?", "-1.7976931348623157e308")]
        [InlineData("double", "4.9406564584124654e-324")]
        [InlineData("double?", "-4.9406564584124654e-324")]
        [InlineData("float", "3.4028234663852886e38")]
        [InlineData("float?", "-3.4028234663852886e38")]
        [InlineData("float", "1.401298464324817e-45")]
        [InlineData("float?", "-1.401298464324817e-45")]
        public void FiniteFloatingBoundariesKeepTheirTypedValues(string type, string literal)
        {
            var yaml = "activity: {key: acme.defaults, version: 1}\ninput: {Value: {type: '" + type + "', default: " + literal +
                "}}\nbody: |\n  return;\n";

            var json = WorkflowJsonSerializer.Write(WorkflowYamlSerializer.ReadActivity(yaml));

            foreach (var document in new[] { WorkflowYamlSerializer.ReadActivity(yaml), WorkflowJsonSerializer.ReadActivity(json) })
            {
                var result = ActivityCompilation.Compile(WorkflowYamlSerializer.Write(document));
                Assert.True(result.Success, string.Join("\n", result.Diagnostics));
                Assert.Empty(result.Diagnostics);
                var context = new AssemblyLoadContext("floating-boundary", isCollectible: true);

                try
                {
                    var activity = context.LoadFromStream(new MemoryStream(result.Assembly)).GetType("IxIFlow.Activities.Acme.Defaults")!;
                    var actual = activity.GetProperty("Value")!.GetCustomAttribute<DefaultValueAttribute>()!.Value;

                    if (type.StartsWith("double", StringComparison.Ordinal))
                        Assert.Equal(double.Parse(literal, CultureInfo.InvariantCulture), Assert.IsType<double>(actual));
                    else
                        Assert.Equal(float.Parse(literal, CultureInfo.InvariantCulture), Assert.IsType<float>(actual));
                }
                finally
                {
                    context.Unload();
                }
            }
        }

        [Theory]
        [MemberData(nameof(FloatingTypes), MemberType = typeof(ActivityCompilationTests))]
        public void FloatingTypeSyntaxCannotBypassFiniteConversion(string type, Type runtime)
        {
            AssertCompiledDefaults(type, [("1e2", Convert.ChangeType(100, runtime, CultureInfo.InvariantCulture))]);

            foreach (var literal in new[] { "1e400", "-1e400", "\"NaN\"", "\"Infinity\"", "\"-Infinity\"" })
                AssertInvalidNumericDefault(type, literal);

            if (runtime == typeof(float))
                AssertInvalidNumericDefault(type, "1e39");
        }

        [Theory]
        [InlineData("System.Nullable // line comment\n <int>", typeof(int), "100.1")]
        [InlineData("global::System.Nullable<System.\\u0049nt32>", typeof(int), "2147483648")]
        [InlineData("global::System.Nullable // line comment\n <@System.@Single>", typeof(float), "1e39")]
        [InlineData("@System.@Nullable < global::System.\\u0044ecimal >", typeof(decimal), "1e-29")]
        public void LineCommentsAndUnicodeIdentifierEscapesKeepNumericRules(string type, Type runtime, string invalid)
        {
            AssertCompiledDefaults(type, [("1e2", Convert.ChangeType(100, runtime, CultureInfo.InvariantCulture))]);
            AssertInvalidNumericDefault(type, invalid);
        }
    }
}