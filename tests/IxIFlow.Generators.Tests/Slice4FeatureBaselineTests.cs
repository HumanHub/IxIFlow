using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace IxIFlow.Slice4.Tests;

[Trait("Feature", "guard")]
[Trait("Spec", "J-07")]
public sealed class Slice4FeatureBaselineTests(ITestOutputHelper output)
{
    [Fact]
    public void EveryAssemblyRetainsItsFeatureCoverage()
    {
        var assembly = typeof(Slice4FeatureBaselineTests).Assembly;

        var counts = assembly.GetTypes().SelectMany(type => type
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => method.GetCustomAttributes<FactAttribute>(true).Any())
                .Select(method => Features(method).Concat(Features(type)).FirstOrDefault() ?? "untagged"))
            .Where(feature => feature != "guard").GroupBy(feature => feature)
            .ToDictionary(group => group.Key, group => group.Count());

        output.WriteLine("FEATURE_COUNTS=" + assembly.GetName().Name + "=" + JsonSerializer.Serialize(counts));
        var baseline = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(File.ReadAllText(BaselinePath()))!;
        var minimums = baseline[assembly.GetName().Name!];
        Assert.Equal(minimums.Keys.Order(), counts.Keys.Order());

        foreach (var (feature, minimum) in minimums)
            Assert.True(
                counts.GetValueOrDefault(feature) >= minimum,
                $"{assembly.GetName().Name}/{feature}: {counts.GetValueOrDefault(feature)} methods; minimum {minimum}");
    }

    private static IEnumerable<string> Features(MemberInfo member) => member.GetCustomAttributesData()
        .Where(attribute => attribute.AttributeType == typeof(TraitAttribute) &&
            (string?)attribute.ConstructorArguments[0].Value == "Feature")
        .Select(attribute => (string)attribute.ConstructorArguments[1].Value!);

    private static string BaselinePath([CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "..", "slice4-feature-baseline.json");
}