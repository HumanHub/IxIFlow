using System.Collections;
using IxIFlow.Dsl.Compilation;

namespace IxIFlow.Dsl.Documents;

/// <summary>Required members and closed values shared by all document roots.</summary>
public static class DocumentRules
{
    public static WorkflowValidationResult Validate(DocumentNode document)
    {
        var diagnostics = new List<WorkflowDiagnostic>();
        ValidateMembers(document, "", document.Members.Count > 0, diagnostics);
        return new(diagnostics);
    }

    private static void Invalid(string code, string message, string path, List<WorkflowDiagnostic> diagnostics)
    {
        if (!diagnostics.Any(item => (item.Code == "MEMBER001" || item.Code is "SWITCH001" or "SWITCH002" or "ASSIGN001" or "ASSIGN003" && item.Message.StartsWith("Missing '", StringComparison.Ordinal)) && item.Path is { } missing &&
            (path == missing || path.StartsWith(missing + ".", StringComparison.Ordinal) || path.StartsWith(missing + "[", StringComparison.Ordinal))))
            diagnostics.Add(new(code, message, WorkflowDiagnosticSeverity.Error, path));
    }

    private static void ValidateMembers(object? value, string path, bool fromSource, List<WorkflowDiagnostic> diagnostics)
    {
        if (value is DocumentNode node)
        {
            foreach (var member in DocumentModel.GetMembers(node.GetType()))
            {
                var name = DocumentModel.GetName(member);
                var memberPath = path.Length == 0 ? name : path + "." + name;
                var child = member.GetValue(node);
                if (member.IsDefined(typeof(DocumentRequiredAttribute), true) &&
                    (fromSource && !node.Members.ContainsKey(name) || child is string text && string.IsNullOrWhiteSpace(text) || child == null))
                {
                    Invalid(node is AssignStepDocument ? (name == "to" ? "ASSIGN001" : "ASSIGN003") : node is ThrowStepDocument ? (name == "exception" ? "THROW001" : "THROW002") : node is SwitchStepDocument && name == "value" ? "SWITCH001" : node is SwitchCaseDocument && name == "value" ? "SWITCH002" : "MEMBER001", $"Missing '{name}'", memberPath, diagnostics);
                    continue;
                }
                var values = DocumentModel.GetValues(node.GetType(), member.Name);
                if (child == null && !DocumentModel.AllowsNull(member))
                {
                    Invalid("MEMBER001", $"Member '{name}' cannot be null", memberPath, diagnostics);
                    continue;
                }
                if (child is string choice && values.Count > 0 && !values.Contains(choice))
                    Invalid(((DocumentValuesAttribute)member.GetCustomAttributes(typeof(DocumentValuesAttribute), true).Single()).DiagnosticCode,
                        $"Unknown {name} '{choice}'; use {string.Join(", ", values)}", memberPath, diagnostics);
                ValidateMembers(child, memberPath, fromSource, diagnostics);
            }
        }
        else if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary) ValidateMembers(entry.Value, path + "." + entry.Key, fromSource, diagnostics);
        }
        else if (value is IList list)
        {
            for (var index = 0; index < list.Count; index++) ValidateMembers(list[index], $"{path}[{index}]", fromSource, diagnostics);
        }
    }

}
