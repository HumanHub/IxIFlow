using System.Text.Json;
using IxIFlow.Authoring;
using IxIFlow.Dsl.Documents;
using Json.Schema;

namespace IxIFlow.Generators.Tests;

public sealed class TemplateDocumentTests
{
    internal const string Yaml = """
        template: {key: order, version: 3, engine: liquid, format: html, model: Shop.Envelope<Shop.Order>}
        content: |
          First
          {{ Value.Number }}
        cultures:
          de: |
            Bestellung
            {{ Value.Number }}
        """;

    [Fact]
    public void TemplateSchemaMatchesTheDocumentModel()
    {
        var path = Path.Combine(WorkflowSchemaFiles.RepositoryRoot(), "src/IxIFlow/schemas/template.schema.json");
        var generated = DocumentSchema.Write(typeof(TemplateDocument));
        if (Environment.GetEnvironmentVariable("IXIFLOW_WRITE_SCHEMA") == "1") File.WriteAllText(path, generated);
        Assert.Equal(generated, File.ReadAllText(path).Replace("\r\n", "\n"));
        Assert.Equal(DocumentSchema.Id(typeof(TemplateDocument)), JsonDocument.Parse(generated).RootElement.GetProperty("$id").GetString());
        Assert.True(JsonSchema.FromText(generated, new BuildOptions { SchemaRegistry = new SchemaRegistry() }).Evaluate(WorkflowYamlSchemaValueTests.Read(Yaml)).IsValid);
    }

    [Fact]
    public void SharedReadersKeepIdentityRequiredPresenceCultureEntriesAndLiteralLines()
    {
        var document = WorkflowYamlSerializer.ReadTemplate(Yaml);
        Assert.True(TemplateDocumentValidator.Validate(document).IsValid);
        var identity = WorkflowDocumentKinds.Identify(Yaml);
        Assert.Equal(("template", "order", 3), (identity.Kind, identity.Name, identity.Version));
        Assert.Equal(new DocumentLocation(4, 5), DocumentModel.GetTextLocation(document, "content", 2, 3));
        Assert.Equal(new DocumentLocation(8, 7), DocumentModel.GetTextLocation(document, "cultures.de", 2, 3));
        var json = WorkflowJsonSerializer.Write(document);
        var reread = WorkflowJsonSerializer.ReadTemplate(json);
        Assert.Equal(document.Cultures, reread.Cultures);
        Assert.Equal(document.Content, WorkflowYamlSerializer.ReadTemplate(WorkflowYamlSerializer.Write(reread)).Content);
        Assert.Equal(new DocumentLocation(6, 7), DocumentModel.GetLocation(document, "cultures.de"));
        Assert.Contains(TemplateDocumentValidator.Validate(WorkflowYamlSerializer.ReadTemplate(Yaml.Replace("version: 3, ", ""))).Diagnostics,
            item => item.Code == "MEMBER001" && item.Path == "template.version");
        var folded = WorkflowYamlSerializer.ReadTemplate(Yaml.Replace("content: |", "content: >"));
        Assert.Equal(DocumentModel.GetLocation(folded, "content"), DocumentModel.GetTextLocation(folded, "content", 2, 3));
        var indented = WorkflowYamlSerializer.ReadTemplate("""
            template: {key: order, version: 3, engine: liquid, format: html, model: Shop.Order}
            content: |2
                text
                  {{ Missing }}
            """);
        Assert.Equal(new DocumentLocation(4, 7), DocumentModel.GetTextLocation(indented, "content", 2, 5));
        const string header = "template: {key: order, version: 3, engine: liquid, format: html, model: Shop.Order}\n";
        var empty = WorkflowYamlSerializer.ReadTemplate(header + "content: ''\n");
        Assert.True(TemplateDocumentValidator.Validate(empty).IsValid);
        Assert.Contains(TemplateDocumentValidator.Validate(WorkflowYamlSerializer.ReadTemplate(header)).Diagnostics,
            item => item.Code == "MEMBER001" && item.Path == "content");
    }

    [Theory]
    [InlineData("key: order", "key: Order", "TPL002", "template.key")]
    [InlineData("version: 3", "version: 0", "TPL004", "template.version")]
    [InlineData("engine: liquid", "engine: unknown", "TPL001", "template.engine")]
    [InlineData("  de: |", "  not-a-culture: |", "TPL003", "cultures.not-a-culture")]
    [InlineData("Shop.Envelope<Shop.Order>", "Shop.Envelope<", "TPL005", "template.model")]
    public void PureDiagnosticsKeepTheFailingMember(string before, string after, string code, string path)
    {
        var document = WorkflowYamlSerializer.ReadTemplate(Yaml.Replace(before, after));
        Assert.Contains(TemplateDocumentValidator.Validate(document).Diagnostics, item => item.Code == code && item.Path == path);
        Assert.True(DocumentModel.GetLocation(document, path).Line > 0);
    }

    [Fact]
    public void WorkflowImportsAndMixedOrMultipleRootsAreRefused()
    {
        var document = WorkflowYamlSerializer.ReadTemplate(Yaml + "\nimports: {workflows: [Other@1]}\n");
        Assert.Contains(TemplateDocumentValidator.Validate(document).Diagnostics, item => item.Code == "TPL008" && item.Path == "imports.workflows");
        var duplicate = WorkflowYamlSerializer.ReadTemplate(Yaml + "\n  DE: Duplicate\n");
        Assert.Contains(TemplateDocumentValidator.Validate(duplicate).Diagnostics, item => item.Code == "TPL003" && item.Path == "cultures.DE");
        Assert.Equal(new DocumentLocation(9, 7), DocumentModel.GetLocation(duplicate, "cultures.DE"));
        Assert.Throws<YamlDotNet.Core.YamlException>(() => WorkflowDocumentKinds.Identify(Yaml + "\nworkflow: {name: Other, dataType: System.Object}"));
        Assert.Throws<YamlDotNet.Core.YamlException>(() => WorkflowDocumentKinds.Identify(Yaml + "\n---\n" + Yaml));
        Assert.Throws<YamlDotNet.Core.YamlException>(() => WorkflowYamlSerializer.ReadTemplate(Yaml.Replace("format: html", "format: pdf")));
        Assert.Throws<YamlDotNet.Core.YamlException>(() => WorkflowYamlSerializer.ReadTemplate(Yaml + "\nunrecognized: true"));
    }
}
