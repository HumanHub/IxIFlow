using System.Text.RegularExpressions;

namespace IxIFlow.Tests;

/// <summary>
/// Every specification item marked done has a test tagged with its ID, and every tagged ID exists.
/// The private specification is found through IXIFLOW_SPEC_DIR or a parent design/spec; without it the test
/// is skipped (spec J-01). Cleanup items (workstream I) remove code and are covered by the full
/// suite, so they need no tag.
/// </summary>
[Trait("Feature", "guard")]
[Trait("Spec", "J-01")]
public sealed partial class SpecTraceabilityTests
{
    [SpecFact]
    public void DoneItemsHaveTestsAndTestsReferToItems()
    {
        var root = SpecFactAttribute.RepositoryRoot!;
        // README.md explains the item format with an example item, so it is not read.
        var items = Directory.GetFiles(SpecFactAttribute.SpecDirectory!, "*.md")
            .Where(file => Path.GetFileName(file) != "README.md")
            .SelectMany(file => ItemPattern().Matches(File.ReadAllText(file)))
            .ToDictionary(match => match.Groups["id"].Value,
                match => match.Groups["status"].Value.Trim());
        var tagged = Directory.GetFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                           !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(file => SpecTraitPattern().Matches(File.ReadAllText(file))
                .Select(match => (Id: match.Groups["id"].Value, File: Path.GetFileName(file))))
            .ToList();

        Assert.Empty(tagged.Where(tag => !items.ContainsKey(tag.Id))
            .Select(tag => $"{tag.File} refers to {tag.Id}, which is not in the specification"));
        var taggedIds = tagged.Select(tag => tag.Id).ToHashSet();
        Assert.Empty(items
            .Where(item => item.Value.StartsWith("done", StringComparison.Ordinal) &&
                           !item.Key.StartsWith("I-", StringComparison.Ordinal) &&
                           !taggedIds.Contains(item.Key))
            .Select(item => $"{item.Key} is done but no test is tagged with it"));
    }

    [GeneratedRegex(@"^### (?<id>[A-Z]-\d{2}) .*?^Status: (?<status>[^\r\n]*)", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex ItemPattern();

    [GeneratedRegex(@"\[Trait\(""Spec"", ""(?<id>[A-Z]-\d{2})""\)\]")]
    private static partial Regex SpecTraitPattern();
}

/// <summary>A test that needs the private specification; skipped without it.</summary>
public sealed class SpecFactAttribute : FactAttribute
{
    public static string? RepositoryRoot { get; } = FindRoot();
    public static string? SpecDirectory { get; } = FindSpecDirectory();

    public SpecFactAttribute()
    {
        if (RepositoryRoot == null || SpecDirectory == null)
            Skip = "Neither IXIFLOW_SPEC_DIR nor a parent design/spec is available";
    }

    private static string? FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "IxIFlow.sln")))
                return directory.FullName;
        }
        return null;
    }

    private static string? FindSpecDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("IXIFLOW_SPEC_DIR");
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
            return configured;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "design", "spec");
            if (Directory.Exists(candidate))
                return candidate;
        }
        return null;
    }
}
