using System.Text.Json;
using IxIFlow.Core;
using IxIFlow.Dsl.Compilation;
using IxIFlow.Dsl.Documents;
using Json.Schema;

namespace IxIFlow.Generators.Tests;

[Trait("Spec", "F-25")]
public sealed class GraphDocumentSchemaTests
{
    [Theory]
    [InlineData("a", "true", "{to: end}")]
    [InlineData("a", "true", "{cases: [{to: end}]}")]
    [InlineData("a", "true", "{to: end, steps: [{kind: transition, to: end}]}")]
    [InlineData("a", "true", "{cases: [{to: end, steps: [{kind: transition, to: end}]}]}")]
    [InlineData("null", "true", "{to: end}")]
    [InlineData("'a/b'", "false", "{to: end}")]
    public async Task InvalidStateKeysKeepSourceIndexedChildDiagnostics(string key, string final, string transition)
    {
        var yaml = GraphYamlParityTests.Document("""
            - kind: stateMachine
              start: a
              states:
                - {key: a, final: FINAL}
                - key: KEY
                  entry: [{kind: transition, to: end}]
                  exit: [{kind: transition, to: end}]
                  transitions: [TRANSITION]
            """.Replace("FINAL", final).Replace("KEY", key).Replace("TRANSITION", transition));
        var document = WorkflowYamlSerializer.Read(yaml);
        var result = await new WorkflowDocumentValidator().ValidateAsync(document, new Context());
        var invalidKey = Assert.Single(result.Diagnostics, d => d.Code == (key == "null" ? "MEMBER001" : "GRAPH001"));
        Assert.Equal("definitions[0].states[1].key", invalidKey.Path);
        Assert.Equal(7, DocumentModel.GetLocation(document, invalidKey.Path).Line);
        foreach (var body in new[] { "entry", "exit" })
        {
            var placement = Assert.Single(result.Diagnostics, d => d.Code == "GRAPH011" && d.Path == "definitions[0].states[1]." + body + "[0]");
            Assert.Equal(body == "entry" ? 8 : 9, DocumentModel.GetLocation(document, placement.Path).Line);
        }
        if (transition.Contains("steps:"))
        {
            var casePath = transition.Contains("cases:") ? ".cases[0]" : "";
            var placement = Assert.Single(result.Diagnostics, d => d.Code == "GRAPH011" &&
                d.Path == "definitions[0].states[1].transitions[0]" + casePath + ".steps[0]");
            Assert.Equal(10, DocumentModel.GetLocation(document, placement.Path).Line);
        }
        var driver = WorkflowGeneratorHarness.Driver(yaml, "duplicate-state.yaml").RunGenerators(GeneratorRegressionTests.Compilation());
        var generated = Assert.Single(driver.GetRunResult().Results);
        Assert.Null(generated.Exception);
        Assert.NotEmpty(generated.Diagnostics);
        Assert.All(generated.Diagnostics, d =>
        {
            Assert.Equal("duplicate-state.yaml", d.Location.GetLineSpan().Path);
            Assert.True(d.Location.GetLineSpan().StartLinePosition.Line >= 2);
        });
    }

    [Theory]
    [InlineData("after: '00:01:00', activityVersion: '1.0'", false)]
    [InlineData("activityVersion: '1.0'", false)]
    [InlineData("on: go, event: Answer, activityVersion: '1.0'", false)]
    [InlineData("after: '00:01:00', input: [{target: Value, from: {kind: constant, value: 1}}]", false)]
    [InlineData("input: [{target: Value, from: {kind: constant, value: 1}}]", false)]
    [InlineData("after: '00:01:00', output: [{source: Value, to: {path: workflow.Value}}]", false)]
    [InlineData("output: [{source: Value, to: {path: workflow.Value}}]", false)]
    [InlineData("after: '00:01:00', activityVersion: '', input: [], output: []", true)]
    [InlineData("activity: ParityEcho, activityVersion: '1.0', input: [{target: Value, from: {kind: constant, value: 1}}], output: [{source: Value, to: {path: workflow.Value}}]", true)]
    [InlineData("on: go, event: Answer, input: [{target: Value, from: {kind: constant, value: 1}}], output: [{source: Value, to: {path: workflow.Value}}]", true)]
    public async Task SchemaAndValidatorAgreeOnLocalTriggerRestrictions(string trigger, bool valid)
    {
        var yaml = GraphYamlParityTests.Document("- {kind: stateMachine, start: a, states: [{key: a, transitions: [{" + trigger + ", to: end}]}]}");
        Assert.Equal(valid, Schema().Evaluate(WorkflowYamlSchemaValueTests.Read(yaml)).IsValid);
        Assert.Equal(valid, (await new WorkflowDocumentValidator().ValidateAsync(WorkflowYamlSerializer.Read(yaml), new Context())).IsValid);
    }

    [Theory]
    [InlineData("", "case: 1,", false)]
    [InlineData("switch: {path: workflow.Value},", "when: {path: workflow.Selected},", false)]
    [InlineData("switch: {path: workflow.Value},", "default: false,", false)]
    [InlineData("switch: {path: workflow.Value},", "case: 1,", true)]
    [InlineData("switch: {path: workflow.Value},", "default: true,", true)]
    [InlineData("", "when: {path: workflow.Selected},", true)]
    [InlineData("", "default: false,", true)]
    public void SchemaConnectsSwitchPresenceWithEdgeForms(string selector, string edge, bool valid)
    {
        var yaml = GraphYamlParityTests.Document("- {kind: flowchart, start: a, nodes: [{key: a, " + selector + "transitions: [{" + edge + "to: end}]}]}");
        Assert.Equal(valid, Schema().Evaluate(WorkflowYamlSchemaValueTests.Read(yaml)).IsValid);
    }

    [Fact]
    public async Task FinalStateRejectsEvenAnEmptyExitBody()
    {
        var yaml = GraphYamlParityTests.Document("- {kind: stateMachine, start: a, states: [{key: a, final: true, exit: []}]}");
        var result = await new WorkflowDocumentValidator().ValidateAsync(WorkflowYamlSerializer.Read(yaml), new Context());
        Assert.Contains(result.Diagnostics, d => d.Code == "GRAPH008");
        Assert.False(Schema().Evaluate(WorkflowYamlSchemaValueTests.Read(yaml)).IsValid);
        Assert.Throws<WorkflowSourceException>(() => WorkflowSourceTranslator.Translate(yaml, "final-exit.yaml"));
    }

    [Fact]
    public void ExecutionGuideDescribesSupportedYamlAndRemainingConditionLimit()
    {
        var guide = File.ReadAllText(Path.Combine(WorkflowSchemaFiles.RepositoryRoot(), "docs", "execution-model.md"));
        Assert.DoesNotContain("YAML state-machine authoring remain unsupported", guide);
        Assert.Contains("YAML and fluent definitions lower to these same graph scopes", guide);
        Assert.Contains("Activity-valued conditions remain unsupported.", guide);
        Assert.Contains("composed workflow triggers are not yet specified", guide);
    }

    [Theory]
    [InlineData("{kind: flowchart, start: a, nodes: [{key: a}, {key: a}]}", "GRAPH001")]
    [InlineData("{kind: flowchart, start: a, nodes: [{key: end}]}", "GRAPH001")]
    [InlineData("{kind: flowchart, start: a, nodes: [{key: 'a/b'}]}", "GRAPH001")]
    [InlineData("{kind: flowchart, start: missing, nodes: [{key: a}]}", "GRAPH002")]
    [InlineData("{kind: flowchart, start: a, nodes: [{key: a, transitions: [{to: missing}]}]}", "GRAPH003")]
    [InlineData("{kind: flowchart, start: a, nodes: [{key: a, transitions: [{case: 1, to: end}]}]}", "GRAPH004")]
    [InlineData("{kind: flowchart, start: a, nodes: [{key: a, switch: {path: workflow.Value}, transitions: [{when: {path: workflow.Selected}, to: end}]}]}", "GRAPH004")]
    [InlineData("{kind: flowchart, start: a, nodes: [{key: a, transitions: [{to: a}, {to: end}]}]}", "GRAPH005")]
    [InlineData("{kind: flowchart, start: a, nodes: [{key: a, switch: {path: workflow.Value}, transitions: [{case: 1, to: end}, {case: 1, to: a}]}]}", "GRAPH005")]
    [InlineData("{kind: stateMachine, start: a, states: [{key: a, transitions: [{on: go, after: '00:01:00', event: Answer, to: end}]}]}", "GRAPH006")]
    [InlineData("{kind: stateMachine, start: a, states: [{key: a, transitions: [{on: go, to: end}]}]}", "GRAPH006")]
    [InlineData("{kind: stateMachine, start: a, states: [{key: a, transitions: [{after: 20, to: end}]}]}", "GRAPH006")]
    [InlineData("{kind: stateMachine, start: a, states: [{key: a, transitions: [{after: null, to: end}]}]}", "GRAPH006")]
    [InlineData("{kind: stateMachine, start: a, states: [{key: a, transitions: [{on: null, to: end}]}]}", "GRAPH006")]
    [InlineData("{kind: stateMachine, start: a, states: [{key: 'a/b', transitions: [{to: end}]}]}", "GRAPH001")]
    [InlineData("{kind: stateMachine, start: a, states: [{key: a, transitions: [{on: go, event: Answer, cases: []}]}]}", "GRAPH007")]
    [InlineData("{kind: stateMachine, start: a, states: [{key: a, transitions: [{on: go, event: Answer, to: end, cases: [{to: end}]}]}]}", "GRAPH007")]
    [InlineData("{kind: stateMachine, start: a, states: [{key: a, final: true, exit: [{kind: activity, activity: ParityWork}]}]}", "GRAPH008")]
    [InlineData("{kind: stateMachine, start: a, states: [{key: a, transitions: [{when: {path: workflow.Selected}, to: end}]}]}", "GRAPH009")]
    [InlineData("{kind: stateMachine, start: a, states: [{key: a, transitions: [{on: go, event: Answer, to: end}, {to: end}]}]}", "GRAPH009")]
    [InlineData("{kind: stateMachine, start: a, states: [{key: a, transitions: [{reply: answer, event: Answer, when: {path: previousStep.Value > 0}, to: end}]}]}", "GRAPH010")]
    [InlineData("{kind: transition, to: end}", "GRAPH011")]
    [InlineData("{kind: flowchart, start: a, nodes: [{key: a, steps: [{kind: waitFor, key: go, event: Answer, timeout: '00:01:00', onTimeout: [{kind: activity, activity: ParityWork}]}]}]}", "GRAPH012")]
    public async Task InvalidGraphsHavePositionedValidatorAndGeneratorDiagnostics(string step, string code)
    {
        var yaml = GraphYamlParityTests.Document("- " + step);
        var document = WorkflowYamlSerializer.Read(yaml);
        var result = await new WorkflowDocumentValidator().ValidateAsync(document, new Context());
        var diagnostic = Assert.Single(result.Diagnostics.Where(d => d.Code == code).Take(1));
        var location = DocumentModel.GetLocation(document, diagnostic.Path);
        Assert.True(location.Line >= 3 && location.Column > 0);
        var build = Assert.Throws<WorkflowSourceException>(() => WorkflowSourceTranslator.Translate(yaml, "invalid-graph.yaml"));
        Assert.True(build.Line >= 3 && build.Column > 0);
    }

    [Theory]
    [InlineData("{kind: flowchart, start: a, nodes: [{key: a, transitions: [{to: end}]}]}", true)]
    [InlineData("{kind: flowchart, nodes: []}", false)]
    [InlineData("{kind: flowchart, start: a, nodes: [{key: a, transitions: [{}]}]}", false)]
    [InlineData("{kind: flowchart, start: a, nodes: [{key: a, transitions: [{when: {path: workflow.Selected}, case: 1, to: end}]}]}", false)]
    [InlineData("{kind: stateMachine, start: a, states: [{key: a, transitions: [{on: go, event: Answer, to: end}]}]}", true)]
    [InlineData("{kind: stateMachine, start: a, states: [{key: a, final: true, transitions: [{to: end}]}]}", false)]
    [InlineData("{kind: stateMachine, start: a, states: [{key: a, transitions: [{on: go, after: '00:01:00', event: Answer, to: end}]}]}", false)]
    [InlineData("{kind: stateMachine, start: a, states: [{key: a, transitions: [{on: go, to: end}]}]}", false)]
    [InlineData("{kind: stateMachine, start: a, states: [{key: a, transitions: [{after: {path: workflow.Timeout}, to: end}]}]}", true)]
    [InlineData("{kind: stateMachine, start: a, states: [{key: a, transitions: [{on: go, event: Answer, cases: []}]}]}", false)]
    [InlineData("{kind: stateMachine, start: a, states: [{key: a, transitions: [{on: go, event: Answer, cases: [{to: end}], steps: []}]}]}", false)]
    [InlineData("{kind: stateMachine, start: a, states: [{key: a, transitions: [{activity: ParityEcho, timeout: '00:01:00', to: end}]}]}", false)]
    public void SchemaChecksGraphShapes(string step, bool valid) => Assert.Equal(valid,
        Schema().Evaluate(WorkflowYamlSchemaValueTests.Read(GraphYamlParityTests.Document("- " + step))).IsValid);

    [Fact]
    public void NullDestinationsAndNullSwitchLabelsSurviveBothSerializers()
    {
        var yaml = GraphYamlParityTests.Document("""
            - kind: flowchart
              start: a
              nodes:
                - key: a
                  switch: {path: workflow.Subject}
                  transitions: [{case: null, to: null}, {to: end}]
            """);
        var definition = GeneratorRegressionTests.Build(yaml);
        var document = WorkflowYamlSerializer.Read(yaml);
        var yamlAgain = WorkflowYamlSerializer.Write(WorkflowYamlSerializer.Read(WorkflowYamlSerializer.Write(document)));
        var jsonAgain = WorkflowYamlSerializer.Write(WorkflowJsonSerializer.Read(WorkflowJsonSerializer.Write(document)));
        foreach (var roundtrip in new[] { yamlAgain, jsonAgain })
        {
            Assert.True(Schema().Evaluate(WorkflowYamlSchemaValueTests.Read(roundtrip)).IsValid);
            Assert.Equal(WorkflowDefinitionValidator.Inspect(definition), WorkflowDefinitionValidator.Inspect(GeneratorRegressionTests.Build(roundtrip)));
        }
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:00:01")]
    public void TimerDurationsMustBePositiveBeforeTranslation(string duration)
    {
        var yaml = GraphYamlParityTests.Document("- {kind: stateMachine, start: a, states: [{key: a, transitions: [{after: '" + duration + "', to: end}]}]}");
        Assert.Throws<WorkflowSourceException>(() => WorkflowSourceTranslator.Translate(yaml, "duration.yaml"));
    }

    [Fact]
    public void GraphExpressionCompilerErrorsPointBackToTheAuthoredYaml()
    {
        var yaml = GraphYamlParityTests.Document("""
            - kind: stateMachine
              start: a
              states:
                - key: a
                  transitions:
                    - on: go
                      event: Answer
                      when: {path: previousStep.NoSuchMember}
                      to: end
            """);
        WorkflowGeneratorHarness.Driver(yaml, "graph-source.yaml")
            .RunGeneratorsAndUpdateCompilation(GeneratorRegressionTests.Compilation(), out var output, out var diagnostics);
        Assert.Empty(diagnostics);
        var error = Assert.Single(output.GetDiagnostics().Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error));
        Assert.Equal("graph-source.yaml", error.Location.GetMappedLineSpan().Path);
        Assert.True(error.Location.GetMappedLineSpan().StartLinePosition.Line >= 7);
    }

    [Fact]
    public void GraphChildrenReachTypeDiscoveryValidationAndTranslation()
    {
        var yaml = GraphYamlParityTests.Document("""
            - kind: stateMachine
              start: pending
              states:
                - key: pending
                  entry: [{kind: activity, activity: ParityEcho}]
                  exit: [{kind: activity, activity: ParitySecondWork}]
                  transitions:
                    - activity: GraphAttempt
                      cases: [{to: done, steps: [{kind: activity, activity: ParityWork}]}]
                    - on: answer
                      event: Answer
                      to: done
                - {key: done, final: true}
            """);
        var document = WorkflowYamlSerializer.Read(yaml);
        Assert.Equal(new[] { "pending", "pending/exit", "pending/on/0/steps", "pending/on/0/case/0", "pending/on/1/case/0", "done" },
            document.Definitions.Single().ChildScopes().Select(s => s.Key));
        foreach (var type in new[] { "ParityEcho", "ParitySecondWork", "GraphAttempt", "ParityWork", "Answer" })
            Assert.Contains("IxIFlow.Generators.Tests." + type, WorkflowSourceTranslator.TypeNames(document));
        var generated = GeneratorRegressionTests.Build(yaml);
        Assert.Contains(generated.StepTable, s => s.CatalogId.EndsWith("/on/0/run/1") && s.Kind == WorkflowStepType.Fire);
    }

    [Fact]
    public async Task GraphDurationExpressionsRoundTripThroughBothReadersAndSchema()
    {
        var yaml = GraphYamlParityTests.Document("""
            - kind: stateMachine
              start: pending
              states:
                - key: pending
                  transitions:
                    - after: {path: workflow.Value > 0 ? workflow.Timeout : workflow.Timeout}
                      cases: [{when: {path: workflow.Selected}, to: end}, {to: pending}]
            """);
        // Add quotes to protect the conditional expression's colon inside YAML flow syntax.
        yaml = yaml.Replace("path: workflow.Value > 0 ? workflow.Timeout : workflow.Timeout", "path: 'workflow.Value > 0 ? workflow.Timeout : workflow.Timeout'");
        var document = WorkflowYamlSerializer.Read(yaml);
        var fromJson = WorkflowJsonSerializer.Read(WorkflowJsonSerializer.Write(document));
        var roundtrip = WorkflowYamlSerializer.Read(WorkflowYamlSerializer.Write(fromJson));
        var transition = ((StateMachineStepDocument)roundtrip.Definitions.Single()).States[0].Transitions.Single();
        Assert.IsType<PathExpressionDocument>(transition.After);
        Assert.Equal(2, transition.Cases!.Count);
        Assert.True(Schema().Evaluate(WorkflowYamlSchemaValueTests.Read(WorkflowYamlSerializer.Write(roundtrip))).IsValid);
        Assert.True((await new WorkflowDocumentValidator().ValidateAsync(roundtrip, new Context())).IsValid);
    }

    private static JsonSchema Schema() => JsonSchema.FromFile(WorkflowSchemaFiles.SchemaPath, new BuildOptions { SchemaRegistry = new SchemaRegistry() });
    private sealed class Context : IWorkflowValidationContext
    {
        public Task<ActivityDocumentContract?> FindActivityAsync(string key, CancellationToken token) => Task.FromResult<ActivityDocumentContract?>(new(key, "1.0"));
        public Task<bool> HasDataTypeAsync(string key, CancellationToken token) => Task.FromResult(true);
        public Task<bool> HasEventAsync(string key, CancellationToken token) => Task.FromResult(true);
        public Task<bool> HasWorkflowAsync(string name, int? version, CancellationToken token) => Task.FromResult(true);
        public Task<bool> HasStepTemplateAsync(string key, CancellationToken token) => Task.FromResult(false);
        public Task<TemplateDocumentContract?> FindTemplateAsync(string key, int? version, CancellationToken token) => Task.FromResult<TemplateDocumentContract?>(null);
    }
}
