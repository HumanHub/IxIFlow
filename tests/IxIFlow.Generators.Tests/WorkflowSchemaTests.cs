using Json.Schema;

namespace IxIFlow.Generators.Tests;

public sealed class WorkflowSchemaTests
{
    private static readonly Lazy<JsonSchema> Schema = new(() => JsonSchema.FromFile(WorkflowSchemaFiles.SchemaPath));
    private const string Header = "workflow: {name: Schema, dataType: FlowData}\n";

    public static IEnumerable<object[]> WorkflowFiles() =>
        Directory.EnumerateFiles(Path.Combine(WorkflowSchemaFiles.RepositoryRoot(), "tests/IxIFlow.Generators.Tests/Fixtures"), "*.yaml")
            .Concat(Directory.EnumerateFiles(Path.Combine(WorkflowSchemaFiles.RepositoryRoot(), "samples"), "*", SearchOption.AllDirectories)
                .Where(file => file.EndsWith(".workflow.yaml", StringComparison.OrdinalIgnoreCase) ||
                               file.EndsWith(".workflow.yml", StringComparison.OrdinalIgnoreCase)))
            .Order(StringComparer.Ordinal).Select(file => new object[] { file });

    [Theory]
    [MemberData(nameof(WorkflowFiles))]
    public void AuthoringFilesValidate(string file)
    {
        var result = Evaluate(File.ReadAllText(file));
        Assert.True(result.IsValid, file + "\n" + Errors(result));
    }

    [Theory]
    [InlineData("definitions: [{kind: unknown}]", false)]
    [InlineData("definitions: [{kind: activity, activity: Multiply, ouptut: []}]", false)]
    [InlineData("definitions: [{kind: activity}]", false)]
    [InlineData("definitions: [{kind: activity, activity: Multiply, output: [{source: Result}]}]", false)]
    [InlineData("workflow: {name: Schema, dataType: FlowData, version: one}", false)]
    [InlineData("workflow: {name: Schema, dataType: FlowData, version: .inf}", false)]
    [InlineData("workflow: {name: Schema, dataType: FlowData, version: .nan}", false)]
    [InlineData("definitions: [{kind: if, condition: {kind: binary, operator: equals, left: {kind: constant}, right: {kind: constant}}}]", false)]
    [InlineData("definitions: [{kind: forEach, items: {path: workflow.Values}, parallel: {join: every}}]", false)]
    [InlineData("definitions: [{kind: if, condition: {}}]", false)]
    [InlineData("definitions: [{kind: if}]", false)]
    [InlineData("definitions: [{kind: forEach}]", false)]
    [InlineData("definitions: [{kind: suspend}]", false)]
    [InlineData("definitions: [{kind: invokeWorkflow}]", false)]
    [InlineData("definitions: [{kind: if, condition: {kind: unknown, path: workflow.Value}}]", false)]
    [InlineData("definitions: [{kind: if, condition: {kind: path}}]", false)]
    [InlineData("definitions: [{kind: activity, activity: Multiply, input: [{target: Value}]}]", false)]
    [InlineData("definitions: [{kind: activity, activity: Multiply, input: [{from: {kind: constant}}]}]", false)]
    [InlineData("definitions: [{kind: activity, activity: Multiply, input: [{target: Value, from: {path: workflow.Value}}]}]", true)]
    [InlineData("definitions: [{kind: activity, activity: Multiply, output: [{source: Result, to: {kind: path, path: workflow.Result}}]}]", true)]
    [InlineData("definitions: [{kind: activity, activity: Multiply, output: [{source: Result, to: {kind: constant, path: workflow.Result}}]}]", false)]
    [InlineData("definitions: [{kind: activity, activity: Multiply, input: [{target: Value, from: {kind: constant}}]}]", true)]
    [InlineData("schemaVersion: 1.0", false)]
    [InlineData("schemaVersion: '2.0'", false)]
    [InlineData("workflow: {name: Schema, dataType: FlowData, displayName: null}", true)]
    [InlineData("definitions: [{kind: suspend, event: Answer}]", true)]
    public void SchemaAcceptsCanonicalDocumentsAndRejectsInvalidDocuments(string body, bool valid)
    {
        var result = Evaluate(body.StartsWith("workflow:", StringComparison.Ordinal) ? body : Header + body);
        Assert.True(result.IsValid == valid, Errors(result));
    }

    [Fact]
    public void MissingWorkflowIsRejected() => Assert.False(Evaluate("definitions: []").IsValid);

    private static EvaluationResults Evaluate(string yaml) => Schema.Value.Evaluate(WorkflowYamlSchemaValueTests.Read(yaml),
        new EvaluationOptions { OutputFormat = OutputFormat.List });

    private static string Errors(EvaluationResults result) => string.Join("\n",
        (result.Errors ?? []).Select(error => result.InstanceLocation + ": " + error.Value)
            .Concat((result.Details ?? []).Select(Errors)).Where(error => error.Length > 0));
}
