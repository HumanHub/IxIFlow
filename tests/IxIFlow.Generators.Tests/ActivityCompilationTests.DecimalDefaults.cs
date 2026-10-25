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
    public sealed class DecimalDefaults
    {
        [Theory]
        [MemberData(nameof(DecimalTypes), MemberType = typeof(ActivityCompilationTests))]
        public void DecimalDefaultsRequireExactCoefficientAndScale(string type)
        {
            (string Literal, object Expected)[] controls =
            [
                ("79228162514264337593543950335", decimal.MaxValue), ("-79228162514264337593543950335", decimal.MinValue),
                ("0.0000000000000000000000000001", 0.0000000000000000000000000001m),
                ("-1e-28", -0.0000000000000000000000000001m),
                ("7.9228162514264337593543950335", 7.9228162514264337593543950335m),
                ("1.234567890123456789012345678", 1.234567890123456789012345678m),
                ("0.10000000000000000000000000000", 0.1m), ("10e-29", 0.0000000000000000000000000001m),
                ("792281625142643375935439503350e-1", decimal.MaxValue), ("0e-99999999999999999999", 0m)
            ];
            AssertCompiledDefaults(type, controls.Concat(controls.Select(item => ("\" " + item.Literal + " \"", item.Expected))).ToArray());
            foreach (var literal in new[]
                     {
                         "79228162514264337593543950335.1", "-79228162514264337593543950335.1",
                         "79228162514264337593543950336", "-79228162514264337593543950336",
                         "7.9228162514264337593543950336", "-7.9228162514264337593543950336",
                         "0.12345678901234567890123456789", "1.23456789012345678901234567891",
                         "1e-29", "-1e-29", "1e400", "1e-400"
                     })
            {
                AssertInvalidNumericDefault(type, literal);
                AssertInvalidNumericDefault(type, "\"" + literal + "\"");
            }
        }

    }
}
