namespace IxIFlow.Tests.Conformance;

/// <summary>Disjoint, stable row-index shards within each profile, independent of profile selection.</summary>
internal static class ScenarioCaseShards
{
    public static IEnumerable<object[]> Select(IEnumerable<object[]> rows, int index, int count) =>
        rows.GroupBy(row => (string)row[0], StringComparer.Ordinal)
            .SelectMany(profile => profile.Where((_, rowIndex) => rowIndex % count == index));
}
