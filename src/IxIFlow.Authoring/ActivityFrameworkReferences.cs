using IxIFlow.Core;
using Microsoft.CodeAnalysis;

namespace IxIFlow.Authoring;

// Reference metadata is never loaded into an AssemblyLoadContext. Runtime inspection
// and package dependency construction use the host's implementation profile instead.
internal static class ActivityFrameworkReferences
{
    private const string Prefix = "IxIFlow.Authoring.References.net10.0.";
    private static readonly IReadOnlyDictionary<string, PortableExecutableReference> References = Load();
    private static readonly Lazy<string[]> ExtractedPaths = new(Extract);

    internal static IEnumerable<PortableExecutableReference> Metadata => References.Values;
    internal static IReadOnlyList<string> Paths => ExtractedPaths.Value;
    internal static bool Contains(string name) => References.ContainsKey(name);

    private static IReadOnlyDictionary<string, PortableExecutableReference> Load()
    {
        var assembly = typeof(ActivityFrameworkReferences).Assembly;
        var references = new SortedDictionary<string, PortableExecutableReference>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.StartsWith(Prefix, StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            references.Add(Path.GetFileNameWithoutExtension(resource[Prefix.Length..]), MetadataReference.CreateFromStream(stream, filePath: resource));
        }
        if (!references.ContainsKey("System.Runtime"))
            throw new WorkflowEngineException(WorkflowErrorKind.Infrastructure, "The .NET 10 activity compilation reference profile is missing");
        return references;
    }

    // The public ReferencePaths API also supports external Roslyn callers which
    // require files. Materialize the same bytes once, only when that API is used;
    // product compilation uses stable resource names without extracted framework files.
    private static string[] Extract()
    {
        var directory = Path.Combine(Path.GetTempPath(), "IxIFlow.ActivityReferences", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var paths = new List<string>();
        void Cleanup()
        {
            try
            {
                foreach (var path in paths) File.Delete(path);
                Directory.Delete(directory);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        try
        {
            foreach (var name in References.Keys)
            {
                var path = Path.Combine(directory, name + ".dll");
                paths.Add(path);
                using var source = typeof(ActivityFrameworkReferences).Assembly.GetManifestResourceStream(Prefix + name + ".dll")!;
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                source.CopyTo(output);
            }
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
            return paths.ToArray();
        }
        catch { Cleanup(); throw; }
    }
}
