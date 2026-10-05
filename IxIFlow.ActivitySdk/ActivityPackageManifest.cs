using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace IxIFlow.ActivitySdk;

public sealed class ActivityPackageManifest
{
    public string SchemaVersion { get; set; } = "1.0";
    public ActivityPackageIdentity Package { get; set; } = new();
    public List<ActivityManifestEntry> Activities { get; set; } = [];

    public string PackageReference => $"{Package.Name}@{Package.Version}";

    public static ActivityPackageManifest Parse(string yaml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yaml);
        ActivityPackageManifest manifest;
        try
        {
            manifest = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build()
                .Deserialize<ActivityPackageManifest>(yaml)
                ?? throw new InvalidOperationException("Activity package manifest is empty");
        }
        catch (Exception exception) when (exception is not InvalidOperationException)
        {
            throw new InvalidOperationException("Activity package manifest is invalid YAML", exception);
        }

        manifest.Validate();
        return manifest;
    }

    public void Validate()
    {
        if (SchemaVersion != "1.0" ||
            string.IsNullOrWhiteSpace(Package?.Name) ||
            string.IsNullOrWhiteSpace(Package.Version) ||
            Activities is not { Count: > 0 })
        {
            throw new InvalidOperationException("Activity package manifest needs schemaVersion 1.0, package identity, and activities");
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var activity in Activities)
        {
            if (string.IsNullOrWhiteSpace(activity.Key) ||
                string.IsNullOrWhiteSpace(activity.Version) ||
                string.IsNullOrWhiteSpace(activity.Name) ||
                string.IsNullOrWhiteSpace(activity.Category) ||
                !keys.Add(activity.Key))
            {
                throw new InvalidOperationException($"Activity package {PackageReference} has a missing or duplicate activity key or metadata");
            }

            var fieldKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in activity.Fields)
            {
                if (string.IsNullOrWhiteSpace(field.Key) ||
                    string.IsNullOrWhiteSpace(field.Label) ||
                    !fieldKeys.Add(field.Key) ||
                    !SupportedControls.Contains(field.Control))
                {
                    throw new InvalidOperationException($"Activity {activity.Key} has an invalid field");
                }
            }

            foreach (var key in activity.Defaults.Keys)
            {
                if (!fieldKeys.Contains(key))
                {
                    throw new InvalidOperationException($"Activity {activity.Key} has a default for unknown field {key}");
                }
            }
        }

    }

    private static readonly HashSet<string> SupportedControls =
        ["text", "code", "binding", "connection"];
}

public sealed class ActivityPackageIdentity
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
}

public sealed class ActivityManifestEntry
{
    public string Key { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string? Designer { get; set; }
    public List<ActivityManifestField> Fields { get; set; } = [];
    public List<string> Outputs { get; set; } = [];
    public Dictionary<string, string> Defaults { get; set; } = new();
}

public sealed class ActivityManifestField
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Control { get; set; } = "text";
    public bool Required { get; set; }
    public string? Help { get; set; }
}
