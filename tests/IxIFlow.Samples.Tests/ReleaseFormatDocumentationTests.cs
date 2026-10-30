namespace IxIFlow.Samples.Tests;

[Trait("Feature", "durability"), Trait("Spec", "F-27")]
public sealed class ReleaseFormatDocumentationTests
{
    [Theory]
    [InlineData("src/IxIFlow.Relational/README.md", "Persisted execution state")]
    [InlineData("docs/stores.md", "`IWorkflowCatalog` supports")]
    public void PersistenceInstructionsDeclareTheReleaseContractAndRefusePreReleaseCheckpoints(string path, string paragraphStart)
    {
        var root = RepositoryRoot();
        var paragraphs = File.ReadAllText(Path.Combine(root, path)).Replace("\r\n", "\n").Split("\n\n");
        var paragraph = Assert.Single(paragraphs, text => text.StartsWith(paragraphStart, StringComparison.Ordinal));
        Assert.Contains("ixiflow-1", paragraph);
        Assert.Matches(@"(?i)\b(?:checkpoint (?:contract|schema)[ -]1|contract-1 checkpoint)\b", paragraph);
        Assert.Matches(@"(?i)(?:pre-release checkpoints[^.]*refus|refus[^.]*pre-release checkpoints)", paragraph);
        Assert.DoesNotMatch(@"(?i)\bschema[ -](?:[2-9]|\d{2,})\b", paragraph);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "IxIFlow.sln")))
                return directory.FullName;

        throw new DirectoryNotFoundException("Cannot find the repository containing persistence documentation");
    }
}