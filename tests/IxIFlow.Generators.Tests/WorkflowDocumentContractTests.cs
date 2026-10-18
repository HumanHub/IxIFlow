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

    // One translatable document per closed member; a member declared without a case here fails DeclaredValuesTranslate.
    private static readonly Dictionary<(Type, string), Func<string, WorkflowDocument>> ValueDocuments = new()
    {
        [(typeof(WorkflowDocument), nameof(WorkflowDocument.SchemaVersion))] = value =>
            Document(new ActivityStepDocument { Activity = "Multiply" }) with { SchemaVersion = value },
        [(typeof(BinaryExpressionDocument), nameof(BinaryExpressionDocument.Operator))] = value =>
            Document(new ActivityStepDocument
            {
                Activity = "Multiply",
                Input = [new() { Target = "Value", From = new BinaryExpressionDocument
                {
                    Operator = value, Left = new ConstantExpressionDocument { Value = value is "and" or "or" ? true : 1 },
                    Right = new ConstantExpressionDocument { Value = value is "and" or "or" ? false : 2 }
                } }]
            }),
        [(typeof(ForEachParallelDocument), nameof(ForEachParallelDocument.Join))] = value =>
            Document(new ForEachStepDocument
            {
                Items = new PathExpressionDocument { Path = "workflow.Values" },
                Parallel = new() { Join = value, ResultType = "int", Condition = new ConstantExpressionDocument { Value = true } },
                Result = new ConstantExpressionDocument { Value = 1 }, Into = new() { Path = "workflow.Results" }
            }),
        [(typeof(ParallelStepDocument), nameof(ParallelStepDocument.Join))] = value =>
            Document(new ParallelStepDocument
            {
                Join = value, Condition = value == "conditionally" ? new ConstantExpressionDocument { Value = true } : null
            }),
        [(typeof(SagaErrorHandlerDocument), nameof(SagaErrorHandlerDocument.Compensate))] = value =>
            Document(new SagaStepDocument { OnError = [new() { Compensate = value }] }),
        [(typeof(SagaErrorHandlerDocument), nameof(SagaErrorHandlerDocument.Then))] = value =>
            Document(new SagaStepDocument { OnError = [new() { Compensate = "all", Then = value }] }),
        [(typeof(StepErrorPolicyDocument), nameof(StepErrorPolicyDocument.Then))] = value =>
            Document(new SagaStepDocument { Steps = [new ActivityStepDocument
            {
                Activity = "Multiply", OnError = [new() { Then = value }]
            }] }),
    };

    private static WorkflowDocument Document(WorkflowStepDocument step) =>
        new() { Workflow = new() { Name = "Values", DataType = "FlowData" }, Definitions = [step] };

    private static IEnumerable<(Type Type, PropertyInfo Member, IReadOnlyList<string> Values)> ClosedMembers() =>
        typeof(WorkflowDocument).Assembly.GetTypes().Where(type => typeof(DocumentNode).IsAssignableFrom(type))
            .SelectMany(type => DocumentModel.GetMembers(type).Where(member => member.DeclaringType == type)
                .Select(member => (type, member, DocumentModel.GetValues(type, member.Name))))
            .Where(entry => entry.Item3.Count > 0);

    public static IEnumerable<object[]> DeclaredValues() =>
        ClosedMembers().SelectMany(entry => entry.Values.Select(value => new object[] { entry.Type, entry.Member.Name, value }));

    public static IEnumerable<object[]> ClosedMemberNames() =>
        ClosedMembers().Select(entry => new object[] { entry.Type, entry.Member.Name });

    [Theory]
    [MemberData(nameof(DeclaredValues))]
    public void DeclaredValuesTranslate(Type type, string member, string value)
    {
        Assert.True(ValueDocuments.TryGetValue((type, member), out var document), $"Add a translation case for {type.Name}.{member}");
        Assert.NotEmpty(WorkflowSourceTranslator.Translate(document!(value), "values.yaml"));
    }

    [Theory]
    [MemberData(nameof(ClosedMemberNames))]
    public void UndeclaredValuesAreRejected(Type type, string member)
    {
        Assert.True(ValueDocuments.TryGetValue((type, member), out var document), $"Add a translation case for {type.Name}.{member}");
        Assert.Throws<WorkflowSourceException>(() => WorkflowSourceTranslator.Translate(document!("undeclared"), "values.yaml"));
    }

    [Fact]
    public void SchemaIdNamesTheReleaseTag()
    {
        var version = typeof(IxIFlow.Core.WorkflowDefinition).Assembly.GetName().Version!.ToString(3);
        Assert.Equal($"https://raw.githubusercontent.com/HumanHub/IxIFlow/v{version}/src/IxIFlow/schemas/workflow.schema.json",
            WorkflowDocumentSchema.Id);
        Assert.Equal(WorkflowDocumentSchema.Id, JsonDocument.Parse(File.ReadAllText(WorkflowSchemaFiles.SchemaPath)).RootElement.GetProperty("$id").GetString());
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
