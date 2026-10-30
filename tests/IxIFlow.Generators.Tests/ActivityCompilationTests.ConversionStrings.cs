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
    public sealed class ConversionStrings
    {
        [Theory]
        [InlineData("bool", "true", typeof(bool))]
        [InlineData("long", "42", typeof(long))]
        [InlineData("decimal", "12.5", typeof(decimal))]
        [InlineData("double", "12.5", typeof(double))]
        [InlineData("DayOfWeek", "Friday", typeof(DayOfWeek))]
        [InlineData("TimeSpan", "'00:00:05'", typeof(TimeSpan))]
        public void ScalarDefaultsEmitTypedMetadata(string typeName, string literal, Type expectedType)
        {
            var yaml = "activity: {key: acme.defaults, version: 1}\ninput: {Value: {type: " + typeName + ", default: " + literal +
                "}}\nbody: |\n  await Task.Yield();";

            var result = ActivityCompilation.Compile(yaml);
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
            var context = new AssemblyLoadContext("activity-default", isCollectible: true);

            try
            {
                var assembly = context.LoadFromStream(new MemoryStream(result.Assembly));
                var property = assembly.GetType("IxIFlow.Activities.Acme.Defaults")!.GetProperty("Value")!;
                Assert.IsType(expectedType, property.GetCustomAttribute<DefaultValueAttribute>()!.Value);
            }
            finally
            {
                context.Unload();
            }
        }

        [Theory]
        [InlineData("sbyte", typeof(sbyte))]
        [InlineData("byte", typeof(byte))]
        [InlineData("short", typeof(short))]
        [InlineData("ushort", typeof(ushort))]
        [InlineData("int", typeof(int))]
        [InlineData("uint", typeof(uint))]
        [InlineData("long", typeof(long))]
        [InlineData("ulong", typeof(ulong))]
        public void IntegralConversionStringsKeepWhitespaceRadixAndExactBoundaries(string alias, Type runtime)
        {
            var previous = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

            try
            {
                var minimum = runtime.GetField("MinValue")!.GetValue(null)!;
                var maximum = runtime.GetField("MaxValue")!.GetValue(null)!;
                var hundred = Convert.ChangeType(100, runtime, CultureInfo.InvariantCulture);

                foreach (var type in new[] { alias, "System . Nullable /* conversion string */ < @System . @" + runtime.Name + " >" })
                    AssertCompiledDefaults(type,
                    [
                        ("\" 100 \"", hundred), ("\"0x64\"", hundred), ("\" 0X64 \"", hundred),
                        ("\"#64\"", hundred), ("\"&h64\"", hundred),
                        ("\" " + Convert.ToString(minimum, CultureInfo.InvariantCulture) + " \"", minimum),
                        ("\" " + Convert.ToString(maximum, CultureInfo.InvariantCulture) + " \"", maximum),
                        ("1e2", hundred), ("100.0", hundred)
                    ]);
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        [Fact]
        public void SignedHexadecimalStringsKeepTheConvertersTwosComplementValue() => AssertCompiledDefaults("int?",
            [("\"0xFFFFFFFF\"", -1), ("\"0x80000000\"", int.MinValue), ("\"0x7fffffff\"", int.MaxValue)]);
    }
}