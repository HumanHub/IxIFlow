using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "registration")]
public sealed class PackageMetadataGuardTests
{
    [Fact]
    public void LibraryPackagesHaveMetadataReadmesAndRestorableProjectDependencies()
    {
        var root = RepositoryRoot();
        var shared = XDocument.Load(Path.Combine(root, "src", "Directory.Build.props"));

        var required = new Dictionary<string, string>
        {
            ["Version"] = "1.0.0", ["Authors"] = "HumanHub LLC", ["Company"] = "HumanHub LLC",
            ["RepositoryUrl"] = "https://github.com/HumanHub/IxIFlow",
            ["PackageProjectUrl"] = "https://github.com/HumanHub/IxIFlow", ["RepositoryType"] = "git",
            ["PackageLicenseExpression"] = "Apache-2.0", ["PublishRepositoryUrl"] = "true",
            ["EmbedUntrackedSources"] = "true", ["IncludeSymbols"] = "true",
            ["SymbolPackageFormat"] = "snupkg", ["Deterministic"] = "true", ["GenerateDocumentationFile"] = "true"
        };

        foreach (var pair in required)
            Assert.Equal(pair.Value, shared.Root!.Elements("PropertyGroup").Elements(pair.Key).Single().Value);

        Assert.False(string.IsNullOrWhiteSpace(shared.Descendants("Copyright").Single().Value));
        var ci = shared.Descendants("ContinuousIntegrationBuild").Single();
        Assert.Contains("$(CI)", ci.Attribute("Condition")!.Value);
        Assert.Contains("$(GITHUB_ACTIONS)", ci.Attribute("Condition")!.Value);
        Assert.DoesNotContain(shared.Descendants("NoWarn"), item => item.Value.Contains("1591", StringComparison.Ordinal));

        foreach (var project in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
                     .Append(Path.Combine(root, "samples", "IxIFlow.DemoActivities", "IxIFlow.DemoActivities.csproj")))
        {
            var xml = XDocument.Load(project);

            if (xml.Descendants("IsPackable").Any(item => item.Value == "false"))
                continue;

            Assert.All(xml.Descendants("Version"), item => Assert.Equal("1.0.0", item.Value));
            Assert.False(string.IsNullOrWhiteSpace(xml.Descendants("Description").Single().Value), project);
            var readme = xml.Descendants("PackageReadmeFile").Single().Value;

            Assert.Contains(xml.Descendants("None"), item =>
                string.Equals((string?)item.Attribute("Pack") ?? item.Element("Pack")?.Value, "true", StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(((string?)item.Attribute("Include") ?? "").Replace('\\', '/')) == readme &&
                File.Exists(Path.Combine(Path.GetDirectoryName(project)!,
                    ((string?)item.Attribute("Include") ?? "").Replace('\\', Path.DirectorySeparatorChar))));

            foreach (var reference in xml.Descendants("ProjectReference"))
            {
                // Analyzer-only build inputs are packed explicitly, never emitted as package dependencies.
                if ((string?)reference.Attribute("ReferenceOutputAssembly") == "false" &&
                    (string?)reference.Attribute("PrivateAssets") == "all")
                    continue;

                var target = XDocument.Load(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!,
                    reference.Attribute("Include")!.Value.Replace('\\', Path.DirectorySeparatorChar))));

                Assert.DoesNotContain(target.Descendants("IsPackable"), item => item.Value == "false");
            }
        }
    }

    private static string RepositoryRoot([CallerFilePath] string source = "") =>
        Path.GetFullPath("../../..", Path.GetDirectoryName(source)!);
}