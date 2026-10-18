using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using IxIFlow.Dsl.Documents;
using IxIFlow.Generators.Tests.Generated;

namespace IxIFlow.Generators.Tests;

public sealed class WorkflowDocumentContractTests
{
    [Fact]
    public void SchemaFileMatchesTheDocumentModel()
    {
        var generated = WorkflowDocumentSchema.Write();
        Assert.DoesNotContain("\r", generated);
        Assert.EndsWith("\n", generated);
        if (Environment.GetEnvironmentVariable("IXIFLOW_WRITE_SCHEMA") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(WorkflowSchemaFiles.SchemaPath)!);
            File.WriteAllText(WorkflowSchemaFiles.SchemaPath, generated);
            return;
        }
        Assert.True(File.Exists(WorkflowSchemaFiles.SchemaPath) && File.ReadAllText(WorkflowSchemaFiles.SchemaPath).Replace("\r\n", "\n") == generated,
            "Regenerate the schema with IXIFLOW_WRITE_SCHEMA=1 dotnet test tests/IxIFlow.Generators.Tests --filter SchemaFileMatchesTheDocumentModel");
    }

    public static IEnumerable<object[]> DeclaredValues()
    {
        foreach (var type in new[] { typeof(BinaryExpressionDocument), typeof(ForEachParallelDocument), typeof(OutputMappingDocument) })
            foreach (var member in DocumentModel.GetMembers(type))
                if (member.GetCustomAttribute<DocumentValuesAttribute>() is { } values)
                    foreach (var value in values.Values) yield return [type, value];
    }

    [Theory]
    [MemberData(nameof(DeclaredValues))]
    public void DeclaredValuesTranslate(Type type, string value)
    {
        var document = new WorkflowDocument { Workflow = new() { Name = "Values", DataType = "FlowData" } };
        document.Definitions.Add(type == typeof(ForEachParallelDocument)
            ? new ForEachStepDocument
            {
                Items = new PathExpressionDocument { Path = "workflow.Values" },
                Parallel = new() { Join = value, ResultType = "int", Condition = new ConstantExpressionDocument { Value = true } },
                Result = new ConstantExpressionDocument { Value = 1 }, Into = new() { Path = "workflow.Results" }
            }
            : new ActivityStepDocument
            {
                Activity = "Multiply",
                Input = type == typeof(BinaryExpressionDocument)
                    ? [new() { Target = "Value", From = new BinaryExpressionDocument
                    {
                        Operator = value, Left = new ConstantExpressionDocument { Value = value is "and" or "or" ? true : 1 },
                        Right = new ConstantExpressionDocument { Value = value is "and" or "or" ? false : 2 }
                    } }] : [],
                Output = type == typeof(OutputMappingDocument)
                    ? [new() { Source = "Result", SourceContext = value, To = new() { Path = "workflow.Result" } }] : []
            });
        Assert.NotEmpty(WorkflowSourceTranslator.Translate(document, "values.yaml"));
    }

    [Fact]
    public async Task CatalogFixtureExecutesTemplatesAssetsAndBothLoopModes()
    {
        var definition = Workflow_catalog.Build();
        Assert.Equal("Catalog workflow", definition.Metadata["DisplayName"]);
        Assert.Equal("Exercises reusable authoring features.", definition.Description);
        Assert.Equal(new[] { "catalog", "schema" }, definition.Tags);
        var result = await GeneratedWorkflowTests.Execute(definition, new FlowData { Value = 4, Values = [1, 2, 3] });
        Assert.Equal(30, result.Data.Result);
        Assert.Equal(new[] { 3, 6, 9 }, result.Data.Results.Select(item => item.Value).Order());
    }

    [Fact]
    public void JsonReaderUsesTheSharedImpliedPathContract()
    {
        var document = WorkflowJsonSerializer.Read("""
            {"workflow":{"name":"Implied","dataType":"FlowData"},"definitions":[
              {"kind":"activity","activity":"Multiply","input":[{"target":"Value","from":{"path":"workflow.Value"}}]}
            ]}
            """);
        Assert.Equal("workflow.Value", Assert.IsType<PathExpressionDocument>(Assert.IsType<ActivityStepDocument>(document.Definitions[0]).Input[0].From).Path);
        Assert.Throws<JsonException>(() => WorkflowJsonSerializer.Read("""
            {"definitions":[{"kind":"if","condition":{"kind":"unknown","path":"workflow.Value"}}]}
            """));
    }

    [Fact]
    public void ContractMembersHaveDescriptionsAndStableOrdering()
    {
        foreach (var type in typeof(WorkflowDocument).Assembly.GetTypes().Where(type => type != typeof(DocumentNode) && typeof(DocumentNode).IsAssignableFrom(type)))
        {
            Assert.NotNull(type.GetCustomAttribute<DescriptionAttribute>());
            Assert.All(DocumentModel.GetMembers(type), member => Assert.NotNull(member.GetCustomAttribute<DescriptionAttribute>()));
        }
        Assert.Equal(new[] { "id", "kind", "name", "description", "ref" },
            DocumentModel.GetMembers(typeof(ActivityReferenceStepDocument)).Select(DocumentModel.GetName));
    }
}

internal static class WorkflowSchemaFiles
{
    internal static string RepositoryRoot([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../.."));
    internal static string SchemaPath => Path.Combine(RepositoryRoot(), "src/IxIFlow/schemas/workflow.schema.json");
}
