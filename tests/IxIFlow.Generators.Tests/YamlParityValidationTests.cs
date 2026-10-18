using IxIFlow.Dsl.Documents;
using Json.Schema;
using Microsoft.CodeAnalysis;

namespace IxIFlow.Generators.Tests;

[Trait("Spec", "F-25")]
public sealed class YamlParityValidationTests
{
    public static IEnumerable<object[]> InvalidDocuments()
    {
        yield return ["- kind: parallel\n  join: conditionally\n  branches: []", "kind: parallel"];
        yield return ["- kind: whileDo\n  condition: {path: outside.Value}", "condition:"];
        yield return ["- kind: doWhile\n  condition: {path: outside.Value}", "condition:"];
        yield return ["- kind: try\n  catches:\n    - faultType: ParityFault", "faultType:"];
        yield return ["- kind: saga\n  onError:\n    - then: continue", "then:"];
        yield return ["- kind: saga\n  steps:\n    - kind: activity\n      activity: ParityWork\n      compensation:\n        - activity: MissingUndo", "activity: MissingUndo"];
        yield return ["- kind: saga\n  steps: [{kind: activity, activity: ParityWork, onError: [{}]}]", "onError:"];
        yield return ["- kind: saga\n  steps:\n    - kind: activity\n      activity: ParityWork\n      onError:\n        - then: ignore\n          retry: {}", "retry:"];
        yield return ["- kind: saga\n  onError:\n    - compensate: all\n      then: retry\n      retry: {initialInterval: '-00:00:01'}", "retry: {initialInterval"];
        yield return ["- kind: delay\n  duration: '00:00:00'", "duration:"];
        yield return ["- kind: request\n  activity: ParityRequest\n  key: ''", "key:"];
        yield return ["- kind: waitFor\n  event: Answer\n  key: answer\n  timeout: '-00:00:01'", "timeout:"];
        yield return ["- kind: suspend\n  event: Answer\n  onTimeout: [{kind: activity, activity: ParityEcho}]", "onTimeout:"];
        yield return ["- kind: outcome\n  value: {kind: constant, value: true}", "kind: outcome"];
        yield return ["- kind: result\n  value: {path: outside.Value}", "value:"];
        yield return ["- kind: parallel\n  branches: []\n  combine:\n    - property: {path: workflow.Result}\n      propertyType: ''\n      combiner: 'IxIFlow.Core.Sum<int>'", "propertyType:"];
        yield return ["- kind: try\n  catches:\n    - steps: [{kind: delay, duration: '00:00:01'}]", "steps: [{kind: delay"];
        yield return ["- kind: saga\n  steps: [{kind: forEach, items: {path: workflow.Values}}]", "steps: [{kind: forEach"];
        yield return ["- kind: activity\n  activity: ParityEcho\n  configure: false\n  input: [{target: Value, from: {kind: constant, value: 1}}]", "configure:"];
        yield return ["- kind: invokeWorkflowType\n  workflowType: ''\n  dataType: IxIFlow.Generators.Tests.FlowData", "workflowType:"];
        yield return ["- kind: invokeWorkflow\n  workflow: {name: Child, version: 3}\n  cancelWithParent: maybe", "cancelWithParent:"];
    }

    [Theory]
    [MemberData(nameof(InvalidDocuments))]
    public void InvalidParityDocumentsHavePositionedDiagnostics(string body, string marker)
    {
        var yaml = YamlParityTests.Document(body);
        var errors = WorkflowGeneratorHarness.Generate(yaml, "invalid-parity.yaml").Diagnostics;
        Assert.NotEmpty(errors);
        var line = Array.FindIndex(yaml.Split('\n'), text => text.Contains(marker, StringComparison.Ordinal));
        Assert.True(line >= 0, marker);
        Assert.Contains(errors, error => error.Id == "IXIYAML001" && error.Location.GetLineSpan().Path == "invalid-parity.yaml" &&
            error.Location.GetLineSpan().StartLinePosition.Line == line && error.Location.GetLineSpan().StartLinePosition.Character > 0);
    }

    [Theory]
    [InlineData("{kind: parallel, branches: [], join: unknown}")]
    [InlineData("{kind: whileDo}")]
    [InlineData("{kind: doWhile}")]
    [InlineData("{kind: try, catches: [{exception: 123}]}")]
    [InlineData("{kind: saga, onError: [{compensate: everything}]}")]
    [InlineData("{kind: delay}")]
    [InlineData("{kind: request, activity: ParityRequest}")]
    [InlineData("{kind: waitFor, event: Answer}")]
    [InlineData("{kind: outcome}")]
    [InlineData("{kind: result}")]
    [InlineData("{kind: invokeWorkflowType}")]
    [InlineData("{kind: invokeWorkflow, workflow: {name: Child}, cancelWithParent: 1}")]
    [InlineData("{kind: parallel, branches: [], combine: [{property: {path: workflow.Result}, combiner: Sum}]}")]
    [InlineData("{kind: saga, steps: [{kind: activity, activity: ParityWork, compensation: [{}]}]}")]
    [InlineData("{kind: saga, steps: [{kind: activity, activity: ParityWork, onError: [{then: stop}]}]}")]
    public void SchemaRejectsInvalidParityForms(string step)
    {
        var schema = JsonSchema.FromFile(WorkflowSchemaFiles.SchemaPath, new BuildOptions { SchemaRegistry = new SchemaRegistry() });
        Assert.False(schema.Evaluate(WorkflowYamlSchemaValueTests.Read(YamlParityTests.Document("- " + step))).IsValid);
    }

    [Theory]
    [InlineData("whileDo", "condition", "  condition: {path: workflow.Missing}")]
    [InlineData("doWhile", "condition", "  condition: {path: workflow.Missing}")]
    [InlineData("parallel", "condition", "  branches: []\n  join: conditionally\n  condition: {path: workflow.Missing}")]
    [InlineData("request", "subject", "  activity: ParityRequest\n  key: request\n  subject: {path: workflow.Missing}")]
    public void GeneratedParityExpressionsMapCompilerErrorsToYaml(string kind, string member, string fields)
    {
        var yaml = YamlParityTests.Document("- kind: " + kind + "\n" + fields);
        var source = WorkflowSourceTranslator.Translate(yaml, "member.yaml");
        var diagnostic = Assert.Single(GeneratorRegressionTests.Compilation(source).GetDiagnostics().Where(error => error.Id == "CS1061"));
        var expected = Array.FindIndex(yaml.Split('\n'), line => line.Contains(member + ":", StringComparison.Ordinal));
        Assert.Equal(expected, diagnostic.Location.GetMappedLineSpan().StartLinePosition.Line);
        Assert.Equal("member.yaml", diagnostic.Location.GetMappedLineSpan().Path);
    }

    [Fact]
    public void NewEnumChoicesRoundTripThroughBothReadersAndTheDerivedSchema()
    {
        var schema = JsonSchema.FromFile(WorkflowSchemaFiles.SchemaPath, new BuildOptions { SchemaRegistry = new SchemaRegistry() });
        var yaml = YamlParityTests.Document("""
            - kind: saga
              steps:
                - kind: activity
                  activity: ParityWork
                  onError: [{then: ignore}]
              onError: [{compensateUpTo: ParityWork, then: retry, retry: {backoffCoefficient: 1.5}}]
            - kind: parallel
              branches: []
              join: conditionally
              condition: {kind: constant, value: true}
            """);
        var document = WorkflowYamlSerializer.Read(yaml);
        var json = WorkflowJsonSerializer.Write(document);
        Assert.Contains("\"compensateUpTo\":\"IxIFlow.Generators.Tests.ParityWork\"", json);
        Assert.Contains("\"then\":\"retry\"", json);
        using var parsed = System.Text.Json.JsonDocument.Parse(json);
        Assert.True(schema.Evaluate(parsed.RootElement).IsValid);
        Assert.Equal(json, WorkflowJsonSerializer.Write(WorkflowYamlSerializer.Read(WorkflowYamlSerializer.Write(WorkflowJsonSerializer.Read(json)))));
        Assert.Throws<System.Text.Json.JsonException>(() => WorkflowJsonSerializer.Read(json.Replace("conditionally", "unknown", StringComparison.Ordinal)));
    }
    [Theory]
    [InlineData("whileDo")]
    [InlineData("parallel")]
    [InlineData("waitFor")]
    [InlineData("request")]
    public async Task GeneratedControlExpressionsKeepBuilderAndAnalyzerPurityChecks(string kind)
    {
        var body = kind switch
        {
            "whileDo" => "- kind: whileDo\n  condition: {kind: function, function: clock}",
            "parallel" => "- kind: parallel\n  branches: []\n  join: conditionally\n  condition: {kind: function, function: clock}",
            "waitFor" => "- kind: waitFor\n  key: answer\n  event: Answer\n  resumeCondition: {kind: function, function: clock}",
            _ => "- kind: request\n  key: answer\n  activity: ParityRequest\n  subject: {kind: function, function: clock}\n- kind: waitFor\n  key: answer\n  event: Answer"
        };
        var yaml = YamlParityTests.Document(body + "\ncode:\n  clock:\n    language: csharp-expression\n    source: " +
            (kind == "request" ? "System.DateTime.UtcNow.ToString()" : "System.DateTime.UtcNow.Year > 0"));
        var source = WorkflowSourceTranslator.Translate(yaml, "purity.yaml");
        var diagnostics = await Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzerExtensions.WithAnalyzers(
            GeneratorRegressionTests.Compilation(source), [new IxIFlow.Analyzers.PurityAnalyzer()]).GetAnalyzerDiagnosticsAsync();
        Assert.Contains(diagnostics, diagnostic => diagnostic.GetMessage().Contains("DateTime.UtcNow", StringComparison.Ordinal) &&
            diagnostic.Location.GetMappedLineSpan().Path == "purity.yaml");
        var error = Assert.Throws<IxIFlow.Core.WorkflowEngineException>(() => GeneratorRegressionTests.Build(yaml));
        Assert.Contains("DateTime.UtcNow", error.Message);
        Assert.Contains("regression.yaml:", error.Message);
    }

    [Theory]
    [InlineData("- kind: try\n  catches: [{steps: [{kind: activity, activity: ParityEcho, name: named}]}]", "SCOPE002", "name: named")]
    [InlineData("- kind: saga\n  steps: [{kind: activity, activity: ParityEcho, name: named}]", "SCOPE002", "name: named")]
    [InlineData("- kind: saga\n  onError: [{compensate: all, then: terminate, ignoreCompensationErrors: true}]", "SAGA004", "ignoreCompensationErrors:")]
    [InlineData("- kind: saga\n  steps: [{kind: activity, activity: ParityWork, compensation: [{activity: ParityUndo, replyType: Answer, previousActivity: ParityUndo}]}]", "SAGA008", "compensation:")]
    [InlineData("- kind: saga\n  steps: [{kind: activity, activity: ParityWork, compensation: [{activity: ParityUndo, previousStepInput: [{target: Value, from: {path: previousStep.Value}}]}]}]", "SAGA009", "compensation:")]
    [InlineData("- kind: saga\n  onError: [{faultType: ParityFault}]", "FAULT001", "faultType:")]
    [InlineData("- kind: forEach\n  items: {path: workflow.Values}\n  steps: [{kind: result, value: {kind: constant, value: 1}}]\n  result: {kind: constant, value: 2}", "EACH007", "  result:")]
    [InlineData("- kind: saga\n  steps: [{kind: activity, activity: ParityWork}]\n  onError: [{compensateUpTo: Missing, then: continue}]", "SAGA011", "compensateUpTo:")]
    [InlineData("- kind: saga\n  onError: [{compensate: all, compensateUpTo: ParityWork}]", "SAGA005", "compensateUpTo:")]
    [InlineData("- kind: saga\n  steps: [{kind: activity, activity: ParityWork}, {kind: activity, activity: ParityWork}]\n  onError: [{compensateUpTo: ParityWork}]", "SAGA011", "compensateUpTo:")]
    public async Task DocumentRulesHavePositionedDiagnostics(string body, string code, string marker)
    {
        var yaml = YamlParityTests.Document(body);
        var document = WorkflowYamlSerializer.Read(yaml);
        var validation = await new IxIFlow.Dsl.Compilation.WorkflowDocumentValidator().ValidateAsync(document, new Contracts());
        var diagnostic = Assert.Single(validation.Diagnostics, item => item.Code == code);
        var location = DocumentModel.GetLocation(document, diagnostic.Path);
        Assert.Equal(Array.FindIndex(yaml.Split('\n'), line => line.Contains(marker, StringComparison.Ordinal)) + 1, location.Line);
        Assert.True(location.Column > 1);
        var generated = WorkflowGeneratorHarness.Generate(yaml, "rules.yaml");
        Assert.Contains(generated.Diagnostics, error => error.Id == "IXIYAML001" &&
            error.Location.GetLineSpan().StartLinePosition.Line == location.Line - 1);
        Assert.Throws<WorkflowSourceException>(() => WorkflowSourceTranslator.Translate(yaml, "rules.yaml"));
    }

    [Fact]
    public async Task JsonRequiredMembersUseTheSameValidatorAndRecordPresentMembers()
    {
        var document = WorkflowJsonSerializer.Read("""
            {
              "workflow": {"name": "MissingCondition", "dataType": "ParityData"},
              "definitions": [
                {"kind": "whileDo", "steps": []}
              ]
            }
            """);
        var loop = Assert.IsType<WhileDoStepDocument>(Assert.Single(document.Definitions));
        Assert.Equal(4, loop.Location.Line);
        Assert.Equal(4, loop.Members["steps"].Line);
        Assert.False(loop.Members.ContainsKey("condition"));
        var validation = await new IxIFlow.Dsl.Compilation.WorkflowDocumentValidator().ValidateAsync(document, new Contracts());
        var diagnostic = Assert.Single(validation.Diagnostics);
        Assert.Equal("MEMBER001", diagnostic.Code);
        Assert.Equal("definitions[0].condition", diagnostic.Path);
        var error = Assert.Throws<WorkflowSourceException>(() => WorkflowSourceTranslator.Translate(document, "missing.json"));
        Assert.Equal(4, error.Line);
        Assert.Contains("condition", error.Message);
    }

    [Theory]
    [InlineData("- kind: parallel\n  branches: []\n  join: unknown", "unknown")]
    [InlineData("- kind: forEach\n  items: {path: workflow.Values}\n  parallel: {join: unknown}", "unknown")]
    [InlineData("- kind: saga\n  onError: [{compensate: unknown}]", "unknown")]
    [InlineData("- kind: saga\n  onError: [{compensate: all, then: unknown}]", "unknown")]
    [InlineData("- kind: saga\n  steps: [{kind: activity, activity: ParityWork, onError: [{then: unknown}]}]", "unknown")]
    [InlineData("- kind: activity\n  activity: ParityEcho\n  output: [{source: Value, sourceContext: unknown, to: {path: workflow.Result}}]", "unknown")]
    [InlineData("- kind: activity\n  activity: ParityEcho\n  input: [{target: Value, from: {kind: binary, operator: unknown, left: {kind: constant, value: 1}, right: {kind: constant, value: 2}}}]", "unknown")]
    [InlineData("- kind: sequence\nschemaVersion: unknown", "unknown")]
    public void UnknownClosedValuesHaveReaderPositions(string body, string value)
    {
        var yaml = YamlParityTests.Document(body);
        var error = Assert.Throws<YamlDotNet.Core.YamlException>(() => WorkflowYamlSerializer.Read(yaml));
        var lines = yaml.Split('\n');
        var line = Array.FindIndex(lines, text => text.Contains(value, StringComparison.Ordinal));
        Assert.Equal(line + 1, error.Start.Line);
        Assert.Equal(lines[line].IndexOf(value, StringComparison.Ordinal) + 1, error.Start.Column);
        var json = WorkflowYamlSchemaValueTests.Read(yaml).GetRawText();
        var jsonError = Assert.Throws<System.Text.Json.JsonException>(() => WorkflowJsonSerializer.Read(json));
        Assert.NotNull(jsonError.LineNumber);
        Assert.NotNull(jsonError.BytePositionInLine);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KeptRequestEndingWithoutItsAnswerPointsToTheSagaHandler(bool typed)
    {
        var yaml = YamlParityTests.Document("""
            - kind: saga
              steps:
                - {kind: request, activity: ParityRequest, key: answer}
                - {kind: activity, activity: ParityFail}
                - {kind: waitFor, event: Answer, key: answer}
              onError:
                - id: recovery
                  compensate: none
                  then: continue
            """);
        if (typed) yaml = yaml.Replace("id: recovery", "id: recovery\n      exception: IxIFlow.Generators.Tests.ParityFailure\n      faultType: IxIFlow.Generators.Tests.ParityFault", StringComparison.Ordinal);
        var error = Assert.Throws<IxIFlow.Core.WorkflowEngineException>(() => GeneratorRegressionTests.Build(yaml));
        var line = Array.FindIndex(yaml.Split('\n'), text => text.Contains("id: recovery", StringComparison.Ordinal)) + 1;
        Assert.Contains("regression.yaml:" + line + ":", error.Message);
        Assert.Contains("would end without its answer", error.Message);
    }

    [Theory]
    [InlineData("- kind: activity", "activity")]
    [InlineData("- kind: delay", "duration")]
    [InlineData("- kind: forEach", "items")]
    [InlineData("- kind: stepRef", "ref")]
    [InlineData("- kind: invokeWorkflow", "workflow")]
    public async Task MissingMembersDoNotHideOtherDiagnosticsOrCauseCascades(string step, string member)
    {
        var yaml = YamlParityTests.Document(step + "\n- {kind: waitFor, key: k, event: Nope}");
        var document = WorkflowYamlSerializer.Read(yaml);
        var validation = await new IxIFlow.Dsl.Compilation.WorkflowDocumentValidator().ValidateAsync(document, new Contracts());
        Assert.Equal(2, validation.Diagnostics.Count);
        Assert.Contains(validation.Diagnostics, diagnostic => diagnostic.Code == "MEMBER001" && diagnostic.Path == "definitions[0]." + member);
        Assert.Contains(validation.Diagnostics, diagnostic => diagnostic.Code == "STEP009" && diagnostic.Path == "definitions[1].event");
        var generated = WorkflowGeneratorHarness.Generate(yaml, "missing.yaml");
        Assert.Equal(2, generated.Diagnostics.Length);
        Assert.Contains(generated.Diagnostics, diagnostic => diagnostic.GetMessage().Contains("Missing '" + member + "'", StringComparison.Ordinal));
        Assert.Contains(generated.Diagnostics, diagnostic => diagnostic.GetMessage().Contains("Unknown event 'Nope'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingExpressionDoesNotHideItsSiblingOrNestedSteps()
    {
        var document = WorkflowYamlSerializer.Read(YamlParityTests.Document("""
            - kind: forEach
              steps: [{kind: waitFor, key: k, event: Nope}]
              result: {path: outside.Value}
            """));
        var validation = await new IxIFlow.Dsl.Compilation.WorkflowDocumentValidator().ValidateAsync(document, new Contracts());
        Assert.Equal(3, validation.Diagnostics.Count);
        Assert.Contains(validation.Diagnostics, diagnostic => diagnostic.Code == "MEMBER001" && diagnostic.Path == "definitions[0].items");
        Assert.Contains(validation.Diagnostics, diagnostic => diagnostic.Code == "EXPR001" && diagnostic.Path == "definitions[0].result");
        Assert.Contains(validation.Diagnostics, diagnostic => diagnostic.Code == "STEP009" && diagnostic.Path == "definitions[0].steps[0].event");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CodeBuiltRequiredStringsReportMemberDiagnostics(string activity)
    {
        var document = new WorkflowDocument
        {
            Workflow = new() { Name = "Required", DataType = typeof(ParityData).FullName! },
            Definitions = [new ActivityStepDocument { Activity = activity }]
        };
        var validation = await new IxIFlow.Dsl.Compilation.WorkflowDocumentValidator().ValidateAsync(document, new Contracts());
        var diagnostic = Assert.Single(validation.Diagnostics);
        Assert.Equal("MEMBER001", diagnostic.Code);
        Assert.Equal("definitions[0].activity", diagnostic.Path);
        var error = Assert.Throws<WorkflowSourceException>(() => WorkflowSourceTranslator.Translate(document, "code.yaml"));
        Assert.Equal("Missing 'activity'", error.Detail);
    }

    [Fact]
    public void UnknownCompensateUpToTypeMapsCompilerErrorToItsMember()
    {
        var yaml = YamlParityTests.Document("""
            - kind: saga
              steps:
                - kind: activity
                  activity: MissingUndo
              onError:
                - id: recovery
                  compensateUpTo: MissingUndo
                  then: continue
            """);
        var source = WorkflowSourceTranslator.Translate(yaml, "target.yaml");
        var diagnostics = GeneratorRegressionTests.Compilation(source).GetDiagnostics().Where(error => error.Id == "CS0246").ToArray();
        Assert.Equal(2, diagnostics.Length);
        var line = Array.FindIndex(yaml.Split('\n'), text => text.Contains("compensateUpTo:", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Location.GetMappedLineSpan().Path == "target.yaml" &&
            diagnostic.Location.GetMappedLineSpan().StartLinePosition.Line == line);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void HandlerIdsShareTheStepIdNamespace(bool saga, bool handlerFirst)
    {
        var handler = saga ? "- kind: saga\n  onError:\n    - id: repeated" : "- kind: try\n  catches:\n    - id: repeated";
        var step = "- kind: activity\n  id: repeated\n  activity: ParityWork";
        var yaml = YamlParityTests.Document(handlerFirst ? handler + "\n" + step : step + "\n" + handler);
        var error = Assert.Throws<WorkflowSourceException>(() => WorkflowSourceTranslator.Translate(yaml, "ids.yaml"));
        Assert.Equal("Invalid or duplicate step ID 'repeated'", error.Detail);
        Assert.Equal(Array.FindLastIndex(yaml.Split('\n'), text => text.Contains("id: repeated", StringComparison.Ordinal)) + (handlerFirst ? 0 : 1), error.Line);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicateHandlerIdsFailDuringTranslation(bool saga)
    {
        var yaml = YamlParityTests.Document((saga ? "- kind: saga\n  onError:" : "- kind: try\n  catches:") +
            "\n    - id: repeated\n    - id: repeated");
        var error = Assert.Throws<WorkflowSourceException>(() => WorkflowSourceTranslator.Translate(yaml, "ids.yaml"));
        Assert.Equal("Invalid or duplicate step ID 'repeated'", error.Detail);
        Assert.Equal(Array.FindLastIndex(yaml.Split('\n'), text => text.Contains("id: repeated", StringComparison.Ordinal)) + 1, error.Line);
    }

    private sealed class Contracts : IxIFlow.Dsl.Compilation.IWorkflowValidationContext
    {
        public Task<IxIFlow.Dsl.Compilation.ActivityDocumentContract?> FindActivityAsync(string key, CancellationToken token) =>
            Task.FromResult<IxIFlow.Dsl.Compilation.ActivityDocumentContract?>(new(key, ""));
        public Task<bool> HasDataTypeAsync(string key, CancellationToken token) => Task.FromResult(true);
        public Task<bool> HasEventAsync(string key, CancellationToken token) => Task.FromResult(key != "Nope");
        public Task<bool> HasWorkflowAsync(string name, int? version, CancellationToken token) => Task.FromResult(true);
        public Task<bool> HasStepTemplateAsync(string key, CancellationToken token) => Task.FromResult(false);
    }
}
