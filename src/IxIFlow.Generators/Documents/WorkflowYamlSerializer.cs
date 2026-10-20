using System.Collections;
using System.Globalization;
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
            if (type == typeof(double) && double.TryParse(scalar.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
                !double.IsNaN(number) && !double.IsInfinity(number)) return number;
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
            var kind = entries.TryGetValue("kind", out var discriminator) ? (discriminator as YamlScalarNode)?.Value : null;
            type = DocumentModel.ResolveVariant(type, kind, entries.ContainsKey)
                ?? throw new YamlException(discriminator?.Start ?? node.Start, discriminator?.End ?? node.End,
                    $"Unknown workflow document kind '{kind}'");
        }
        var keys = DocumentModel.GetKeys(type);
        foreach (var pair in mapping.Children)
        {
            var name = ((YamlScalarNode)pair.Key).Value!;
            var key = keys.FirstOrDefault(candidate => candidate.Name == name);
            if (key.Name == null)
                throw new YamlException(pair.Key.Start, pair.Key.End,
                    $"Unknown member '{name}'; {type.Name} accepts {string.Join(", ", keys.Select(candidate => candidate.Name))}");
            var declared = DocumentModel.GetKind(type)!;
            if (key.IsDiscriminator && (pair.Value as YamlScalarNode)?.Value != declared.Kind)
                throw new YamlException(pair.Value.Start, pair.Value.End, $"Expected workflow document kind '{declared.Kind}'");
        }
        var value = Activator.CreateInstance(type) ?? throw Invalid(type, node);
        foreach (var property in DocumentModel.GetMembers(type))
            if (entries.TryGetValue(DocumentModel.GetName(property), out var child))
            {
                var memberValue = ReadValue(property.PropertyType, child);
                var values = DocumentModel.GetValues(type, property.Name);
                if (memberValue is string choice && values.Count > 0 && !values.Contains(choice))
                    throw new YamlException(child.Start, child.End, $"{(property.Name == nameof(WorkflowDocument.SchemaVersion) ? "Unsupported schema version" : "Unknown " + DocumentModel.GetName(property))} '{choice}'; use {string.Join(", ", values)}");
                property.SetValue(value, memberValue);
            }
        if (value is DocumentNode document)
        {
            document.Location = Location(node);
            var locations = entries.ToDictionary(pair => pair.Key, pair => Location(pair.Value), StringComparer.OrdinalIgnoreCase);
            if (entries.TryGetValue("defaults", out var defaults) && defaults is YamlMappingNode defaultValues)
                foreach (var pair in Entries(defaultValues)) locations.Add("defaults." + pair.Key, Location(pair.Value));
            document.Members = locations;
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
            foreach (var key in DocumentModel.GetKeys(value.GetType()))
                if (key.IsDiscriminator) mapping.Add(key.Name, DocumentModel.GetKind(value.GetType())!.Kind);
                else if (key.Member?.GetValue(value) is { } child) mapping.Add(key.Name, ToYaml(child));
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

    private static DocumentLocation Location(YamlNode node) => new((int)node.Start.Line, (int)node.Start.Column);
    private static YamlException Invalid(Type type, YamlNode node) =>
        new(node.Start, node.End, $"Workflow YAML does not match {type.Name}");
}
