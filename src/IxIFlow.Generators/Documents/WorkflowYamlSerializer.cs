using System.Collections;
using System.Globalization;
using System.Reflection;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace IxIFlow.Dsl.Documents;

/// <summary>Reads and writes the shared document directly through YamlDotNet.</summary>
public static class WorkflowYamlSerializer
{
    public static WorkflowDocument Read(string yaml)
    {
        if (yaml is null) throw new ArgumentNullException(nameof(yaml));
        if (string.IsNullOrWhiteSpace(yaml)) throw new ArgumentException("The value cannot be empty or whitespace.", nameof(yaml));
        var stream = new YamlStream();
        using var reader = new StringReader(yaml);
        stream.Load(reader);
        if (stream.Documents.Count == 0)
            throw new YamlException(new Mark(0, 1, 1), new Mark(0, 1, 1), "A workflow YAML file must contain one document");
        if (stream.Documents.Count != 1)
            throw new YamlException(stream.Documents[1].RootNode.Start, stream.Documents[1].RootNode.End,
                "A workflow YAML file must contain one document");
        if (stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new YamlException(stream.Documents[0].RootNode.Start, stream.Documents[0].RootNode.End,
                "A workflow YAML root must be a mapping");
        return (WorkflowDocument)ReadValue(typeof(WorkflowDocument), root)!;
    }

    public static string Write(WorkflowDocument document)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));
        var stream = new YamlStream(new YamlDocument(ToYaml(document)));
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        stream.Save(writer, assignAnchors: false);
        return writer.ToString();
    }

    private static object? ReadValue(Type type, YamlNode node)
    {
        if (type == typeof(object)) return Untyped(node);
        if (node is YamlScalarNode scalar)
        {
            if (Scalar(scalar) == null && (type == typeof(string) || Nullable.GetUnderlyingType(type) != null)) return null;
            if (type == typeof(string)) return scalar.Value;
            if (type == typeof(int) || type == typeof(int?))
            {
                if (int.TryParse(scalar.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) return integer;
            }
            else if (type == typeof(bool) && bool.TryParse(scalar.Value, out var flag)) return flag;
            throw Invalid(type, node);
        }
        if (type.IsArray && node is YamlSequenceNode array)
        {
            var result = Array.CreateInstance(type.GetElementType()!, array.Children.Count);
            for (var index = 0; index < array.Children.Count; index++)
                result.SetValue(ReadValue(type.GetElementType()!, array.Children[index]), index);
            return result;
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>) && node is YamlSequenceNode sequence)
        {
            var list = (IList)Activator.CreateInstance(type)!;
            foreach (var child in sequence.Children) list.Add(ReadValue(type.GetGenericArguments()[0], child));
            return list;
        }
        if (node is not YamlMappingNode mapping) throw Invalid(type, node);
        var entries = Entries(mapping);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            var dictionary = (IDictionary)Activator.CreateInstance(type)!;
            foreach (var pair in entries) dictionary.Add(pair.Key, ReadValue(type.GetGenericArguments()[1], pair.Value));
            return dictionary;
        }
        if (type == typeof(WorkflowStepDocument) || type == typeof(ExpressionDocument))
        {
            var kind = entries.TryGetValue("kind", out var discriminator) ? (discriminator as YamlScalarNode)?.Value :
                type == typeof(ExpressionDocument) && entries.ContainsKey("path") ? "path" : null;
            type = (type == typeof(WorkflowStepDocument), kind) switch
            {
                (true, "activity") => typeof(ActivityStepDocument),
                (true, "activityRef") => typeof(ActivityReferenceStepDocument),
                (true, "stepRef") => typeof(StepReferenceStepDocument),
                (true, "if") => typeof(ConditionalStepDocument),
                (true, "sequence") => typeof(SequenceStepDocument),
                (true, "forEach") => typeof(ForEachStepDocument),
                (true, "suspend") => typeof(SuspendStepDocument),
                (true, "invokeWorkflow") => typeof(InvokeWorkflowStepDocument),
                (false, "constant") => typeof(ConstantExpressionDocument),
                (false, "path") => typeof(PathExpressionDocument),
                (false, "binary") => typeof(BinaryExpressionDocument),
                (false, "function") => typeof(FunctionExpressionDocument),
                _ => throw new YamlException(discriminator?.Start ?? node.Start, discriminator?.End ?? node.End,
                    $"Unknown workflow document kind '{kind}'")
            };
        }
        var value = Activator.CreateInstance(type) ?? throw Invalid(type, node);
        foreach (var property in Properties(type))
            if (entries.TryGetValue(property.Name, out var child)) property.SetValue(value, ReadValue(property.PropertyType, child));
        if (value is DocumentNode document)
        {
            document.Location = Location(node);
            document.Members = entries.ToDictionary(pair => pair.Key, pair => Location(pair.Value), StringComparer.OrdinalIgnoreCase);
        }
        return value;
    }

    private static Dictionary<string, YamlNode> Entries(YamlMappingNode mapping)
    {
        var entries = new Dictionary<string, YamlNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in mapping.Children)
        {
            if (pair.Key is not YamlScalarNode key || string.IsNullOrWhiteSpace(key.Value) || entries.ContainsKey(key.Value!))
                throw new YamlException(pair.Key.Start, pair.Key.End, "Workflow YAML contains an invalid or duplicate mapping key");
            entries.Add(key.Value!, pair.Value);
        }
        return entries;
    }

    private static object? Untyped(YamlNode node) => node switch
    {
        YamlScalarNode scalar => Scalar(scalar),
        YamlSequenceNode sequence => sequence.Children.Select(Untyped).ToList(),
        YamlMappingNode mapping => Entries(mapping).ToDictionary(pair => pair.Key, pair => Untyped(pair.Value)),
        _ => throw new InvalidOperationException($"Unsupported YAML node {node.GetType().Name}")
    };

    private static object? Scalar(YamlScalarNode scalar)
    {
        var value = scalar.Value;
        if (scalar.Style != ScalarStyle.Plain) return value;
        if (value is null or "null" or "~") return null;
        if (value == "true") return true;
        if (value == "false") return false;
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) return integer;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return number;
        return value;
    }

    private static YamlNode ToYaml(object? value)
    {
        if (value == null) return new YamlScalarNode("null");
        if (value is string text) return new YamlScalarNode(text) { Style = ScalarStyle.DoubleQuoted };
        if (value is bool flag) return new YamlScalarNode(flag ? "true" : "false");
        if (value is DocumentNode)
        {
            var mapping = new YamlMappingNode();
            var kind = value switch
            {
                ActivityStepDocument => "activity", ActivityReferenceStepDocument => "activityRef",
                StepReferenceStepDocument => "stepRef", ConditionalStepDocument => "if",
                SequenceStepDocument => "sequence", ForEachStepDocument => "forEach",
                SuspendStepDocument => "suspend", InvokeWorkflowStepDocument => "invokeWorkflow",
                ConstantExpressionDocument => "constant", PathExpressionDocument => "path",
                BinaryExpressionDocument => "binary", FunctionExpressionDocument => "function", _ => null
            };
            if (kind != null) mapping.Add("kind", kind);
            foreach (var property in Properties(value.GetType()))
            {
                if (value is WorkflowStepDocument && property.Name == nameof(WorkflowStepDocument.Kind)) continue;
                var child = property.GetValue(value);
                if (child != null) mapping.Add(CamelCase(property.Name), ToYaml(child));
            }
            return mapping;
        }
        if (value is IDictionary dictionary)
        {
            var mapping = new YamlMappingNode();
            foreach (DictionaryEntry pair in dictionary) mapping.Add(new YamlScalarNode((string)pair.Key), ToYaml(pair.Value));
            return mapping;
        }
        if (value is IEnumerable sequence) return new YamlSequenceNode(sequence.Cast<object?>().Select(ToYaml));
        if (value is IFormattable number) return new YamlScalarNode(number.ToString(null, CultureInfo.InvariantCulture));
        throw new InvalidOperationException($"Unsupported document value {value.GetType().Name}");
    }

    private static IEnumerable<PropertyInfo> Properties(Type type) => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => property.Name is not (nameof(DocumentNode.Location) or nameof(DocumentNode.Members)));
    private static string CamelCase(string name) => char.ToLowerInvariant(name[0]) + name.Substring(1);
    private static DocumentLocation Location(YamlNode node) => new((int)node.Start.Line, (int)node.Start.Column);
    private static YamlException Invalid(Type type, YamlNode node) =>
        new(node.Start, node.End, $"Workflow YAML does not match {type.Name}");
}
