using System.Collections.Immutable;
using IxIFlow.Analyzers;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Dsl.Documents;
using IxIFlow.Dsl.Compilation;
using IxIFlow.Extensions;
using Json.Schema;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Generators.Tests;

[Trait("Feature", "authoring")]
[Trait("Spec", "W-04")]
public sealed class AssignYamlTests
{
    private const string Header = "workflow: {name: AssignYaml, dataType: IxIFlow.Generators.Tests.AssignYamlData}\ndefinitions:\n";

    [Fact]
    public async Task ArithmeticTypedNullAndCatalogMatchThroughYamlAndJsonRoundTrips()
    {
        var yaml = Header + """
            - kind: assign
              id: total
              to: {path: workflow.Count}
              value: {kind: binary, operator: add, left: {path: workflow.Count}, right: {kind: constant, value: 2}}
            - kind: assign
              id: text
              to: {path: workflow.Text}
              value: {kind: constant, value: null}
            """;
        var fluent = Workflow.Create<AssignYamlData>("AssignYaml").Assign(ctx => ctx.WorkflowData.Count, ctx => ctx.WorkflowData.Count + 2).WithId("total")
            .Assign(ctx => ctx.WorkflowData.Text, ctx => (string?)null).WithId("text").Build();
        foreach (var source in Readers(yaml))
        {
            var document = WorkflowYamlSerializer.Read(source);
            var schema = JsonSchema.FromText(DocumentSchema.Write(typeof(WorkflowDocument)), new BuildOptions { SchemaRegistry = new SchemaRegistry() });
            Assert.True(schema.Evaluate(System.Text.Json.JsonDocument.Parse(WorkflowJsonSerializer.Write(document)).RootElement).IsValid);
            var generated = GeneratorRegressionTests.Build(source);
            Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(generated));
            Assert.Equal(fluent.StepTable.Select(step => (step.Kind, step.Assignment, step.AuthorId)), generated.StepTable.Select(step => (step.Kind, step.Assignment, step.AuthorId)));
            foreach (var definition in new[] { fluent, generated })
            {
                var (data, trace) = await Run(definition);
                Assert.Equal(3, data.Count);
                Assert.Null(data.Text);
                Assert.Equal(2, trace.Count(item => item.Entry.EntryType == TraceEntryType.DataAssigned));
            }
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task IndexIntrinsicUsesTheIterationPositionInPathsAndAssets(bool asset)
    {
        var target = "workflow.Lines[index(line)].Value";
        var rhs = asset ? "{kind: function, function: replacement}" : "{kind: binary, operator: add, left: {path: line.Value}, right: {kind: constant, value: 10}}";
        var yaml = Header + "- kind: forEach\n  items: {path: workflow.Lines}\n  as: line\n  steps:\n" +
            "    - kind: assign\n      to: {path: '" + target + "'}\n      value: " + rhs + "\n" +
            (asset ? "code:\n  replacement: {language: csharp-expression, source: 'line.Index + index(line)'}\n" : "");
        var bodyOnly = new WorkflowBuilder<AssignYamlData, object>([], "AssignYaml")
            .ForEach(itemsExpression: ctx => ctx.WorkflowData.Lines, body: (b, line) =>
            {
                if (asset) b.Assign(ctx => ctx.WorkflowData.Lines[ctx.Index(line)].Value, ctx => ctx.Item(line).Index + ctx.Index(line));
                else b.Assign(ctx => ctx.WorkflowData.Lines[ctx.Index(line)].Value, ctx => ctx.Item(line).Value + 10);
            }).Build();
        foreach (var source in Readers(yaml))
        {
            var generatedSource = WorkflowSourceTranslator.Translate(source, "assign.yaml");
            Assert.Contains("ctx.Index(", generatedSource);
            Assert.Contains("ctx.Item(", generatedSource);
            var generated = GeneratorRegressionTests.Build(source);
            Assert.Equal(WorkflowDefinitionValidator.Inspect(bodyOnly), WorkflowDefinitionValidator.Inspect(generated));
            var (data, _) = await Run(generated);
            Assert.Equal(asset ? new[] { 20, 31 } : new[] { 12, 13 }, data.Lines.Select(line => line.Value));
        }
    }

    [Fact]
    public async Task IndexIntrinsicSupportsAnItemWithoutAnIndexMemberAndNestedAliases()
    {
        var yaml = Header + """
            - kind: forEach
              items: {path: workflow.Numbers}
              as: outer
              steps:
                - kind: forEach
                  items: {path: workflow.Numbers}
                  as: inner
                  steps:
                    - kind: assign
                      to: {path: 'workflow.Numbers[index(inner)]'}
                      value: {kind: function, function: replacement}
            code:
              replacement: {language: csharp-expression, source: 'index(outer) + index(inner) + inner'}
            """;
        var definition = GeneratorRegressionTests.Build(yaml);
        var (data, _) = await Run(definition);
        Assert.Equal(new[] { 2, 5 }, data.Numbers);
    }

    [Theory]
    [InlineData("index()")]
    [InlineData("index(line, line)")]
    [InlineData("index(missing)")]
    [InlineData("index(workflow)")]
    [InlineData("index(line.Value)")]
    public void InvalidIntrinsicArityAndScopeArePositioned(string expression)
    {
        var yaml = Header + "- kind: assign\n  to: {path: 'workflow.Numbers[" + expression + "]'}\n  value: {kind: constant, value: 1}\n";
        var error = Assert.Throws<WorkflowSourceException>(() => WorkflowSourceTranslator.Translate(yaml, "assign.yaml"));
        Assert.Contains("index(alias)", error.Message);
        Assert.Equal(4, error.Line);
    }

    [Theory]
    [InlineData("workflow", "ASSIGN001")]
    [InlineData("previousStep.Count", "ASSIGN001")]
    [InlineData("fault.Count", "ASSIGN001")]
    [InlineData("workflow.Count + 1", "ASSIGN001")]
    public void InvalidTargetsArePositionedAtTo(string target, string code)
    {
        var error = Assert.Throws<WorkflowSourceException>(() => WorkflowSourceTranslator.Translate(Header +
            "- kind: assign\n  to: {path: '" + target + "'}\n  value: {kind: constant, value: 1}\n", "assign.yaml"));
        Assert.Contains(code, error.Message);
        Assert.Equal(4, error.Line);
    }

    [Theory]
    [InlineData("{kind: assign}", "ASSIGN001")]
    [InlineData("{kind: assign, to: null, value: {kind: constant, value: 1}}", "ASSIGN001")]
    [InlineData("{kind: assign, to: {path: workflow.Count}}", "ASSIGN003")]
    [InlineData("{kind: assign, to: {path: workflow.Count}, value: null}", "ASSIGN003")]
    public void MissingAndNullMembersHaveAssignmentDiagnostics(string step, string code)
    {
        var error = Assert.Throws<WorkflowSourceException>(() => WorkflowSourceTranslator.Translate(Header + "- " + step, "assign.yaml"));
        Assert.Contains(code, error.Message);
        Assert.Equal(3, error.Line);
    }

    [Fact]
    public async Task CatchBindingsShadowAndRestoreThroughNestedBodies()
    {
        var yaml = Header + """
            - kind: try
              steps: [{kind: activity, activity: IxIFlow.Generators.Tests.SwitchYamlFail}]
              catches:
                - exception: System.InvalidOperationException
                  faultType: IxIFlow.Generators.Tests.SwitchYamlFault
                  steps:
                    - kind: parallel
                      branches:
                        - steps:
                            - kind: try
                              steps: [{kind: activity, activity: IxIFlow.Generators.Tests.SwitchYamlFailInner}]
                              catches:
                                - exception: System.ArgumentException
                                  faultType: IxIFlow.Generators.Tests.SwitchYamlFault
                                  steps:
                                    - kind: assign
                                      to: {path: workflow.Text}
                                      value: {path: fault.Message}
                              finally:
                                - kind: assign
                                  to: {path: workflow.Text}
                                  value: {kind: function, function: outer}
            code:
              outer: {language: csharp-expression, source: 'workflow.Text + "|" + fault.Message'}
            """;
        var fluent = new WorkflowBuilder<AssignYamlData, object>([], "AssignYaml").Try(t => t.Step<SwitchYamlFail>())
            .Catch<InvalidOperationException, SwitchYamlFault>(c => c.Parallel(p => p.Do(b => b.Try(t => t.Step<SwitchYamlFailInner>())
                .Catch<ArgumentException, SwitchYamlFault>(inner => inner.Assign(ctx => ctx.WorkflowData.Text, ctx => ctx.Fault.Message))
                .Finally(f => f.Assign(ctx => ctx.WorkflowData.Text, ctx => ctx.WorkflowData.Text + "|" + ctx.Fault.Message))))).Build();
        foreach (var source in Readers(yaml))
        {
            var generated = GeneratorRegressionTests.Build(source);
            Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(generated));
            Assert.Equal("inner|fault", (await Run(generated)).Data.Text);
        }
    }

    [Theory]
    [InlineData("sequence")] [InlineData("if")] [InlineData("switch")] [InlineData("default")]
    [InlineData("while")] [InlineData("do")] [InlineData("parallel")] [InlineData("each")]
    [InlineData("finally")] [InlineData("flowchart")] [InlineData("entry")] [InlineData("exit")]
    [InlineData("action")] [InlineData("saga")]
    public async Task FaultAwareAssignHasFluentYamlTwinsUnderEveryChildCallback(string shape)
    {
        const string body = "[{kind: assign, to: {path: workflow.Text}, value: {path: fault.Message}}]";
        var nested = shape switch
        {
            "sequence" => "{kind: sequence, steps: " + body + "}",
            "if" => "{kind: if, condition: {kind: constant, value: true}, then: " + body + "}",
            "switch" => "{kind: switch, value: {path: fault.Message}, cases: [{value: {kind: constant, value: fault}, steps: " + body + "}]}",
            "default" => "{kind: switch, value: {path: fault.Message}, default: " + body + "}",
            "while" => "{kind: whileDo, condition: {path: 'workflow.Text == \"\"'}, steps: " + body + "}",
            "do" => "{kind: doWhile, condition: {kind: constant, value: false}, steps: " + body + "}",
            "parallel" => "{kind: parallel, branches: [{steps: " + body + "}]}",
            "each" => "{kind: forEach, as: line, items: {path: workflow.Numbers}, steps: " + body + "}",
            "finally" => "{kind: try, steps: [], finally: " + body + "}",
            "flowchart" => "{kind: flowchart, start: a, nodes: [{key: a, steps: " + body + "}]}",
            "entry" => "{kind: stateMachine, start: a, states: [{key: a, entry: " + body + ", transitions: [{to: done}]}, {key: done, final: true}]}",
            "exit" => "{kind: stateMachine, start: a, states: [{key: a, exit: " + body + ", transitions: [{to: done}]}, {key: done, final: true}]}",
            "action" => "{kind: stateMachine, start: a, states: [{key: a, transitions: [{to: done, steps: " + body + "}]}, {key: done, final: true}]}",
            _ => "{kind: saga, steps: " + body + "}"
        };
        var yaml = Header + "- {kind: activity, activity: IxIFlow.Generators.Tests.AssignYamlSeed}\n" +
            "- kind: try\n  steps: [{kind: activity, activity: IxIFlow.Generators.Tests.SwitchYamlFail}]\n  catches:\n" +
            "    - exception: System.InvalidOperationException\n      faultType: IxIFlow.Generators.Tests.SwitchYamlFault\n      steps:\n        - " + nested + "\n";
        ICatchWorkflowBuilder<AssignYamlData, SwitchYamlFault, AssignYamlSeed> Set(ICatchWorkflowBuilder<AssignYamlData, SwitchYamlFault, AssignYamlSeed> b) =>
            b.Assign(ctx => ctx.WorkflowData.Text, ctx => ctx.Fault.Message);
        var fluent = Workflow.Create<AssignYamlData>("AssignYaml").Step<AssignYamlSeed>().Try(t => t.Step<SwitchYamlFail>())
            .Catch<InvalidOperationException, SwitchYamlFault>(c =>
            {
                switch (shape)
                {
                    case "sequence": c.Sequence(b => Set(b)); break;
                    case "if": c.If(ctx => true, b => Set(b)); break;
                    case "switch": c.Switch(ctx => ctx.Fault.Message, cases => cases.Case("fault", b => Set(b))); break;
                    case "default": c.Switch(ctx => ctx.Fault.Message, cases => cases.Default(b => Set(b))); break;
                    case "while": c.WhileDo(conditionExpression: ctx => ctx.WorkflowData.Text == "", configure: b => Set(b)); break;
                    case "do": c.DoWhile(b => Set(b), conditionExpression: ctx => false); break;
                    case "parallel": c.Parallel(p => p.Do(b => Set(b))); break;
                    case "each": c.ForEach(itemsExpression: ctx => ctx.WorkflowData.Numbers, body: (b, _) => Set(b)); break;
                    case "finally": c.Try(_ => { }).Finally(b => Set(b)); break;
                    case "flowchart": c.Flowchart(g => g.Start("a").Node("a", b => Set(b))); break;
                    case "entry": c.StateMachine(m => m.Start("a").State("a", s => s.Entry(b => Set(b)).Immediately(t => t.Otherwise("done"))).State("done", s => s.Final())); break;
                    case "exit": c.StateMachine(m => m.Start("a").State("a", s => s.Exit(b => Set(b)).Immediately(t => t.Otherwise("done"))).State("done", s => s.Final())); break;
                    case "action": c.StateMachine(m => m.Start("a").State("a", s => s.Immediately(t => t.Otherwise("done", b => Set(b)))).State("done", s => s.Final())); break;
                    default: c.Saga(s => s.Assign(ctx => ctx.WorkflowData.Text, ctx => ctx.Fault.Message)); break;
                }
            }).Build();
        foreach (var source in Readers(yaml))
        {
            var generated = GeneratorRegressionTests.Build(source);
            Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(generated));
            Assert.Equal("fault", (await Run(generated)).Data.Text);
        }
    }

    [Fact]
    public async Task GeneratedMutationDiagnosticsPointToValueAndTargetIndexes()
    {
        foreach (var target in new[] { false, true })
        {
            var yaml = Header + "- kind: assign\n  to: {path: '" + (target ? "workflow.Numbers[workflow.Numbers.Remove(1) ? 0 : 1]" : "workflow.Removed") +
                "'}\n  value: " + (target ? "{kind: constant, value: 1}" : "{kind: function, function: mutate}") +
                "\ncode:\n  mutate: {language: csharp-expression, source: 'workflow.Numbers.Remove(1)'}\n";
            var source = WorkflowSourceTranslator.Translate(yaml, "assign.yaml");
            var compilation = GeneratorRegressionTests.Compilation(source);
            var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new PurityAnalyzer())).GetAnalyzerDiagnosticsAsync();
            Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "AD0001");
            Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "IXIF0101" && diagnostic.Location.GetMappedLineSpan().Path == "assign.yaml");
        }
    }

    [Theory]
    [InlineData("code:\n  index: {language: csharp-expression, source: '1'}\n")]
    [InlineData("steps:\n  index: {steps: []}\n")]
    [InlineData("steps:\n  block: {parameters: {index: {type: int}}, steps: []}\n")]
    public void IntrinsicNameIsReservedAcrossCodeAndTemplates(string declaration)
    {
        var error = Assert.Throws<WorkflowSourceException>(() => WorkflowSourceTranslator.Translate(Header +
            "- {kind: assign, to: {path: workflow.Count}, value: {kind: constant, value: 1}}\n" + declaration, "assign.yaml"));
        Assert.Contains("reserved", error.Message);
    }

    [Fact]
    public void AnIterationIndexDoesNotEscapeItsBody()
    {
        var error = Assert.Throws<WorkflowSourceException>(() => WorkflowSourceTranslator.Translate(Header + """
            - {kind: forEach, as: line, items: {path: workflow.Numbers}, steps: []}
            - kind: assign
              to: {path: 'workflow.Numbers[index(line)]'}
              value: {kind: constant, value: 1}
            """, "assign.yaml"));
        Assert.Contains("in-scope", error.Message);
        Assert.Equal(5, error.Line);
    }

    private static IEnumerable<string> Readers(string yaml)
    {
        var document = WorkflowYamlSerializer.Read(yaml);
        yield return yaml;
        yield return WorkflowYamlSerializer.Write(document);
        yield return WorkflowYamlSerializer.Write(WorkflowJsonSerializer.Read(WorkflowJsonSerializer.Write(document)));
    }
    private static async Task<(AssignYamlData Data, IReadOnlyList<StoredTraceEntry> Trace)> Run(WorkflowDefinition definition)
    {
        await using var services = new ServiceCollection().AddLogging().AddIxIFlow(o => o.Debugging = true).Services.BuildServiceProvider();
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new AssignYamlData());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        return (Assert.IsType<AssignYamlData>(result.WorkflowData), await services.GetRequiredService<IWorkflowOperations>().GetTraceAsync(result.InstanceId));
    }
}

public sealed class AssignYamlData
{
    public int Count { get; set; } = 1;
    public string? Text { get; set; } = "";
    public bool Removed { get; set; }
    public List<AssignYamlLine> Lines { get; set; } = [new() { Index = 20, Value = 2 }, new() { Index = 30, Value = 3 }];
    public List<int> Numbers { get; set; } = [1, 2];
}
public sealed class AssignYamlLine { public int Index { get; set; } public int Value { get; set; } }
public sealed class AssignYamlSeed : IAsyncActivity
{ public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask; }
