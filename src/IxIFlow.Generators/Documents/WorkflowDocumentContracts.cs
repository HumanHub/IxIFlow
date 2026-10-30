using Microsoft.CodeAnalysis;

namespace IxIFlow.Dsl.Compilation;

/// <summary>Member directions available to document validation without loading workflow assemblies.</summary>
public sealed record WorkflowMemberDocumentContract(
    string Name,
    bool Setter,
    bool Getter,
    bool Input,
    bool Output,
    bool Required,
    bool HasDefault,
    bool Indexer = false);

public sealed record WorkflowDataDocumentContract(IReadOnlyList<WorkflowMemberDocumentContract> Members)
{
    internal string Snapshot() => string.Join(";", Members.Select(member => member.Name + "," +
        string.Concat(new[]
                { member.Setter, member.Getter, member.Input, member.Output, member.Required, member.HasDefault, member.Indexer }
            .Select(value => value ? '1' : '0'))));

    internal static WorkflowDataDocumentContract ReadSnapshot(string text) => new(text
        .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(row =>
        {
            var parts = row.Split(',');
            var flags = parts[1];

            return new WorkflowMemberDocumentContract(parts[0], flags[0] == '1', flags[1] == '1', flags[2] == '1', flags[3] == '1',
                flags[4] == '1', flags[5] == '1', flags[6] == '1');
        }).ToArray());

    public bool Declared => Members.Any(member => member.Input || member.Output);

    public WorkflowDataDocumentContract Select(IReadOnlyList<string>? inputs, IReadOnlyList<string>? outputs)
    {
        var declared = Declared || inputs != null || outputs != null;

        return new(Members.Select(member => member with
        {
            Input = inputs is { Count: 0 } ? false : !declared ? member.Setter : member.Input || inputs?.Contains(member.Name) == true,
            Output = outputs is { Count: 0 } ? false : !declared ? member.Getter : member.Output || outputs?.Contains(member.Name) == true
        }).ToArray());
    }

    public static WorkflowDataDocumentContract FromSymbol(INamedTypeSymbol type)
    {
        var members = new Dictionary<string, WorkflowMemberDocumentContract>(StringComparer.Ordinal);

        for (var current = type; current != null; current = current.BaseType)
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>().Where(property => !property.IsStatic &&
                         (SymbolEqualityComparer.Default.Equals(current, type) || property.DeclaredAccessibility != Accessibility.Private)))
            {
                var attributes = Attributes(property).ToArray();
                bool Has(string name) => attributes.Any(attribute => IsAttribute(attribute.AttributeClass, name));
                var input = Has("IxIFlow.ActivitySdk.WorkflowInputAttribute") || Has("IxIFlow.ActivitySdk.WorkflowInputOutputAttribute");
                var output = Has("IxIFlow.ActivitySdk.WorkflowOutputAttribute") || Has("IxIFlow.ActivitySdk.WorkflowInputOutputAttribute");

                if (!members.ContainsKey(property.Name))
                    members.Add(property.Name, new(property.Name,
                        property.SetMethod?.DeclaredAccessibility == Accessibility.Public,
                        property.GetMethod?.DeclaredAccessibility == Accessibility.Public, input, output,
                        Has("System.ComponentModel.DataAnnotations.RequiredAttribute"),
                        Has("System.ComponentModel.DefaultValueAttribute"), property.IsIndexer));
            }

        return new(members.Values.OrderBy(member => member.Name, StringComparer.Ordinal).ToArray());
    }

    private static bool IsAttribute(INamedTypeSymbol? type, string name)
    {
        for (var current = type; current != null; current = current.BaseType)
            if (current.ToDisplayString() == name)
                return true;

        return false;
    }

    // Attribute.GetCustomAttributes follows property overrides (unlike PropertyInfo.GetCustomAttributes).
    // Match its AttributeUsage inheritance and nearest-declaration rules without loading the DTO.
    private static IEnumerable<AttributeData> Attributes(IPropertySymbol property)
    {
        var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        for (var current = property; current != null; current = current.OverriddenProperty)
            foreach (var attribute in current.GetAttributes())
            {
                var type = attribute.AttributeClass;

                if (type == null)
                    continue;

                AttributeData? usage = null;

                for (var parent = type; parent != null && usage == null; parent = parent.BaseType)
                    usage = parent.GetAttributes()
                        .FirstOrDefault(item => item.AttributeClass?.ToDisplayString() == "System.AttributeUsageAttribute");

                var inherited = usage?.NamedArguments.FirstOrDefault(pair => pair.Key == "Inherited").Value.Value as bool? ?? true;
                var multiple = usage?.NamedArguments.FirstOrDefault(pair => pair.Key == "AllowMultiple").Value.Value as bool? ?? false;

                if (!SymbolEqualityComparer.Default.Equals(current, property) && !inherited)
                    continue;

                if (!multiple && !seen.Add(type))
                    continue;

                yield return attribute;
            }
    }
}

/// <summary>Semantic contracts supplied by a compiler or publishing host.</summary>
public interface IWorkflowContractValidationContext
{
    WorkflowDataDocumentContract? FindDataContract(string dataType);
    WorkflowDataDocumentContract? FindWorkflowContract(string name, int? version);
    WorkflowDataDocumentContract? FindWorkflowTypeContract(string workflowType, string? dataType);
}