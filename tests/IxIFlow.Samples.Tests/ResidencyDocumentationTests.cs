namespace IxIFlow.Samples.Tests;

/// <summary>The residency guidance stays in the published documentation (spec H-07).</summary>
[Trait("Spec", "H-07")]
public sealed class ResidencyDocumentationTests
{
    [Fact]
    public void PlacementGuideStatesThatDemandsPlaceComputeAndResidencyNeedsAStorePerRegion()
    {
        var section = Section(File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "coordinator-and-hosts.md")),
            "## Placement and data residency");

        Assert.Contains("Demands place compute only", section);
        Assert.Contains("not where its data is stored", section);
        Assert.Contains("live in the one state store shared by the cluster", section);
        Assert.Contains("Full data residency therefore requires a separate store, and a separate cluster of hosts, coordinators and store, per region", section);
        Assert.Contains("does not select a database, move data between stores, partition a shared store by region", section);
    }

    [Theory]
    [InlineData("architecture.md")]
    [InlineData("engine-capabilities.md")]
    [InlineData("execution-model.md")]
    public void OverviewPagesLinkToTheResidencySection(string page) =>
        Assert.Contains("coordinator-and-hosts.md#placement-and-data-residency",
            File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", page)));

    private static string Section(string markdown, string heading)
    {
        var start = markdown.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing section '{heading}'");
        var end = markdown.IndexOf("\n## ", start + heading.Length, StringComparison.Ordinal);
        return end < 0 ? markdown[start..] : markdown[start..end];
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "IxIFlow.sln")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Cannot find the repository containing the documentation");
    }
}
