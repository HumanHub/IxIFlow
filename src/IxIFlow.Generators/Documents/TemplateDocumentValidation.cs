using System.Globalization;
using System.Text.RegularExpressions;
using IxIFlow.Dsl.Compilation;
using Microsoft.CodeAnalysis.CSharp;

namespace IxIFlow.Dsl.Documents;

/// <summary>Pure template rules; binding and engine execution belong to publishing.</summary>
public static class TemplateDocumentValidator
{
    public static WorkflowValidationResult Validate(TemplateDocument document)
    {
        var diagnostics = DocumentRules.Validate(document).Diagnostics.ToList();

        void Invalid(string code, string message, string path) =>
            diagnostics.Add(new(code, message, WorkflowDiagnosticSeverity.Error, path));

        bool Missing(string path) => diagnostics.Any(item => item.Code == "MEMBER001" && item.Path is { } missing &&
            (path == missing || path.StartsWith(missing + ".", StringComparison.Ordinal)));

        var header = document.Template;

        if (header != null)
        {
            if (!Missing("template.key") && (header.Key.Length > 100 || header.Key != header.Key.ToLowerInvariant() ||
                    !Regex.IsMatch(header.Key, @"\A\w+(?:[.-]\w+)*\z", RegexOptions.CultureInvariant)))
                Invalid("TPL002", "Template key must use lowercase package-id grammar, 1–100 characters", "template.key");

            if (!Missing("template.version") && header.Version < 1)
                Invalid("TPL004", "Template versions start at 1", "template.version");

            if (!Missing("template.engine") && header.Engine != "liquid")
                Invalid("TPL001", "Unknown template engine; use liquid", "template.engine");

            if (!Missing("template.model") && SyntaxFactory.ParseTypeName(header.Model).ContainsDiagnostics)
                Invalid("TPL005", "Template model must be a complete C# type expression", "template.model");

            if (header.DisplayName != null && (string.IsNullOrWhiteSpace(header.DisplayName) || header.DisplayName.Length > 200))
                Invalid("MEMBER001", "Display name must be nonblank and at most 200 characters", "template.displayName");
        }

        if (document.Imports?.Workflows.Count > 0)
            Invalid("TPL008", "Template documents cannot import workflows", "imports.workflows");

        var cultures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in document.Cultures ?? new())
        {
            var path = "cultures." + pair.Key;

            try
            {
                var culture = CultureInfo.GetCultureInfo(pair.Key);

                if (!CultureInfo.GetCultures(CultureTypes.AllCultures).Any(known => known.Name == culture.Name))
                    throw new CultureNotFoundException();

                if (culture.Equals(CultureInfo.InvariantCulture) || !cultures.Add(culture.Name))
                    throw new CultureNotFoundException();
            }
            catch (CultureNotFoundException)
            {
                Invalid("TPL003", "Variant cultures must be known, nonempty and unique", path);
            }

            if (pair.Value == null)
                Invalid("MEMBER001", "Variant text cannot be null", path);
        }

        return new(diagnostics);
    }
}