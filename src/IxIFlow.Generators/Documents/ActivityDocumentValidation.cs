using System.Globalization;
using System.Text.RegularExpressions;
using IxIFlow.Generators;
using Microsoft.CodeAnalysis.CSharp;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace IxIFlow.Dsl.Compilation;

using Documents;

/// <summary>Pure activity-document rules, without type binding or package loading.</summary>
public static class ActivityDocumentValidator
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.Ordinal)
    {
        "ExecuteAsync", "Equals", "GetHashCode", "GetType", "ToString", "ReferenceEquals", "MemberwiseClone", "Finalize"
    };

    public static WorkflowValidationResult Validate(ActivityDocument document, string? sourceText = null)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));
        var diagnostics = DocumentRules.Validate(document).Diagnostics.ToList();
        void Invalid(string code, string message, string path) => diagnostics.Add(new(code, message, WorkflowDiagnosticSeverity.Error, path));
        bool Missing(string path) => diagnostics.Any(item => item.Code == "MEMBER001" && item.Path is { } missing &&
            (path == missing || path.StartsWith(missing + ".", StringComparison.Ordinal)));

        var header = document.Activity;
        if (header != null && !Missing("activity"))
        {
            if (!Missing("activity.key") && !ActivityPackageIdentity.IsValidKey(header.Key))
                Invalid("ACTD001", "Activity key must be a lowercase letter-led dotted or hyphenated name of at most 100 characters", "activity.key");
            if (!Missing("activity.version") && header.Version is < 1 or > 65534)
                Invalid("ACTD002", "Activity version must be from 1 through 65534", "activity.version");
            foreach (var pair in new[] { ("icon", header.Icon), ("designer", header.Designer) })
                if (pair.Item2 != null && !Regex.IsMatch(pair.Item2, @"\A[a-z][a-z0-9-]*\z", RegexOptions.CultureInvariant))
                    Invalid("ACTD008", "Icon and designer must be lowercase name tokens", "activity." + pair.Item1);
        }
        var className = header != null && ActivityPackageIdentity.IsValidKey(header.Key) && header.Version is >= 1 and <= 65534
            ? new ActivityPackageIdentity(header.Key, header.Version).ClassName : null;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var direction in new[] { ("input", document.Input), ("output", document.Output), ("inOut", document.InOut) })
            foreach (var pair in direction.Item2 ?? new())
            {
                var path = direction.Item1 + "." + pair.Key;
                var member = pair.Value;
                if (!SyntaxFacts.IsValidIdentifier(pair.Key) || SyntaxFacts.GetKeywordKind(pair.Key) != SyntaxKind.None ||
                    SyntaxFactory.ParseToken(pair.Key).ValueText != pair.Key || ReservedNames.Contains(pair.Key) || pair.Key == className)
                    Invalid("ACTD003", "Member name must be a non-reserved C# identifier whose parsed name matches the key exactly", path);
                if (!names.Add(pair.Key)) Invalid("ACTD004", "Member names must be unique ignoring case across input, output and inOut", path);
                if (member == null) { Invalid("MEMBER001", "Missing member declaration", path); continue; }
                if (direction.Item1 == "output")
                {
                    if (member.Required) Invalid("ACTD005", "Output-only members cannot be required", path + ".required");
                    if (member.HasDefault()) Invalid("ACTD006", "Output-only members cannot have a default", path + ".default");
                }
                if (!Missing(path + ".type") && SyntaxFactory.ParseTypeName(member.Type).ContainsDiagnostics)
                    Invalid("ACTD009", "Member type must be valid C# type syntax", path + ".type");
                if (member.HasDefault() && (!IsScalarDefault(member.Default) ||
                    !Missing(path + ".type") && !ActivityDefaultValue.TryGetText(member, out _)))
                    Invalid("ACTD010", "Default must be a supported finite scalar value", path + ".default");
                foreach (var text in new[] { ("label", member.Label), ("help", member.Help) })
                    if (text.Item2 != null && string.IsNullOrWhiteSpace(text.Item2))
                        Invalid("ACTD015", "Label and help must contain text", path + "." + text.Item1);
                if (direction.Item1 != "output" && member.Required && member.HasDefault() && member.Default == null)
                    Invalid("ACTD016", "A required input cannot have an explicit null default", path + ".default");
            }

        // A supplied blank body has its own diagnostic; absence is MEMBER001.
        if (string.IsNullOrWhiteSpace(document.Body) && (document.Members.Count == 0 || document.Members.ContainsKey("body")))
        {
            diagnostics.RemoveAll(item => item.Code == "MEMBER001" && item.Path == "body");
            Invalid("ACTD011", "Activity body must contain statements", "body");
        }
        var usings = document.Usings ?? [];
        for (var index = 0; index < usings.Count; index++)
            if (!ValidNamespace(usings[index]))
                Invalid("ACTD013", "Using must be a C# namespace name", "usings[" + index + "]");
        var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var imports = document.Packages ?? [];
        for (var index = 0; index < imports.Count; index++)
        {
            var import = imports[index] ?? "";
            var parts = import.Split('@');
            if (parts.Length != 2 || !Regex.IsMatch(parts[0], @"\A\w+(?:[.-]\w+)*\z", RegexOptions.CultureInvariant) || parts[0].Length > 100 ||
                !Regex.IsMatch(parts[1], @"\A[0-9]+(?:\.[0-9]+){0,3}(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z", RegexOptions.CultureInvariant) ||
                !parts[1].Split(new[] { '-', '+' })[0].Split('.').All(part => int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)) ||
                !packages.Add(parts[0]))
                Invalid("ACTD014", "Package imports require unique package identifiers and exact versions (id@version)", "packages[" + index + "]");
        }
        if (sourceText != null)
        {
            var stream = new YamlStream();
            using var reader = new StringReader(sourceText);
            stream.Load(reader);
            if (stream.Documents.Count == 1 && stream.Documents[0].RootNode is YamlMappingNode root)
                foreach (var pair in root.Children)
                    if (pair.Key is YamlScalarNode { Value: "body" or "declarations" } key &&
                        (key.Value == "body" || document.Declarations != null) &&
                        pair.Value is not YamlScalarNode { Style: ScalarStyle.Literal })
                        Invalid("ACTD012", "Body and declarations must use YAML literal blocks", key.Value!);
        }
        return new(diagnostics);
    }

    internal static bool IsScalarDefault(object? value) => value is null or string or bool or char or byte or sbyte or short or ushort or int or uint or long or ulong or decimal or DocumentNumber ||
        value is double number && !double.IsNaN(number) && !double.IsInfinity(number) ||
        value is float single && !float.IsNaN(single) && !float.IsInfinity(single);

    private static bool ValidNamespace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var name = SyntaxFactory.ParseName(value!);
        return !name.ContainsDiagnostics && name.ToFullString() == value &&
            value!.Split('.').All(part => SyntaxFacts.IsValidIdentifier(part) && SyntaxFacts.GetKeywordKind(part) == SyntaxKind.None);
    }
}
