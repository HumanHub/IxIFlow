using System.Globalization;
using System.Text.RegularExpressions;

namespace IxIFlow.Generators;

/// <summary>Derives package, assembly and C# identities from an activity key and integer version.</summary>
public sealed record ActivityPackageIdentity
{
    public static bool IsValidKey(string? key) => key != null && key.Length <= 100 &&
        Regex.IsMatch(key, @"\A[a-z][a-z0-9]*([.-][a-z][a-z0-9]*)*\z", RegexOptions.CultureInvariant);

    public ActivityPackageIdentity(string key, int version)
    {
        if (!IsValidKey(key)) throw new ArgumentException("Invalid activity key", nameof(key));
        if (version is < 1 or > 65534) throw new ArgumentOutOfRangeException(nameof(version));
        PackageId = key;
        ActivityVersion = version.ToString(CultureInfo.InvariantCulture);
        PackageVersion = ActivityVersion + ".0.0";
        AssemblyVersion = PackageVersion + ".0";
        var segments = key.Split('.').Select(segment => string.Concat(segment.Split('-')
            .Select(part => char.ToUpperInvariant(part[0]) + part.Substring(1)))).ToArray();
        ClassName = segments[segments.Length - 1];
        Namespace = "IxIFlow.Activities" + (segments.Length == 1 ? "" : "." + string.Join(".", segments.Take(segments.Length - 1)));
    }

    public string PackageId { get; }
    public string ActivityVersion { get; }
    public string PackageVersion { get; }
    public string AssemblyName => PackageId;
    public string AssemblyVersion { get; }
    public string InformationalVersion => PackageVersion;
    public string Namespace { get; }
    public string ClassName { get; }
    public string CSharpTypeName => "global::" + Namespace + "." + ClassName;
    public string MetadataTypeName => Namespace + "." + ClassName;
}
