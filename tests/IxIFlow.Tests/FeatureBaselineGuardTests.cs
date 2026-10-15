using System.Reflection;
using System.Text.Json;

namespace IxIFlow.Tests;

/// <summary>
/// Every test belongs to one feature area, and no area drops below the minimum in
/// tests/feature-baseline.json. Lowering a minimum is a deliberate change to that file.
/// </summary>
[Trait("Feature", Guard)]
public sealed class FeatureBaselineGuardTests
{
    private const string Guard = "guard";

    [Fact]
    public void EveryTestHasOneKnownFeatureAndNoAreaFallsBelowItsMinimum()
    {
        var minimums = JsonSerializer.Deserialize<Dictionary<string, int>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "feature-baseline.json")))!;
        var tests = typeof(FeatureBaselineGuardTests).Assembly.GetTypes()
            .SelectMany(type => type
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(method => method.GetCustomAttributes<FactAttribute>(inherit: true).Any())
                .Select(method => (
                    Name: $"{type.FullName}.{method.Name}",
                    Features: FeaturesOf(method).Concat(FeaturesOf(type)).ToList())))
            .Where(test => !test.Features.Contains(Guard))
            .ToList();

        Assert.Empty(tests.Where(test => test.Features.Count != 1)
            .Select(test => $"{test.Name} has {test.Features.Count} Feature traits"));
        Assert.Empty(tests.Select(test => test.Features[0]).Distinct()
            .Where(area => !minimums.ContainsKey(area))
            .Select(area => $"Area '{area}' is not in feature-baseline.json"));

        var counts = tests.GroupBy(test => test.Features[0])
            .ToDictionary(group => group.Key, group => group.Count());
        Assert.Empty(minimums
            .Where(minimum => counts.GetValueOrDefault(minimum.Key) < minimum.Value)
            .Select(minimum => $"Area '{minimum.Key}' has {counts.GetValueOrDefault(minimum.Key)} tests; " +
                               $"the minimum is {minimum.Value}"));
    }

    private static IEnumerable<string> FeaturesOf(MemberInfo member) => member.GetCustomAttributesData()
        .Where(attribute => attribute.AttributeType == typeof(TraitAttribute) &&
                            (string?)attribute.ConstructorArguments[0].Value == "Feature")
        .Select(attribute => (string)attribute.ConstructorArguments[1].Value!);
}
