using System.Collections.Immutable;
using IxIFlow.Analyzers;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Dsl.Documents;
using IxIFlow.Dsl.Compilation;
using IxIFlow.Extensions;
using Json.Schema;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Generators.Tests;

[Trait("Feature", "authoring"), Trait("Spec", "W-03")]
public sealed class SwitchYamlParityTests
{
    [Theory]
    [InlineData("System.IComparable", false)] [InlineData("System.IComparable", true)]
    [InlineData("System.String", false)] [InlineData("System.String", true)]
    [InlineData("IxIFlow.Generators.Tests.SwitchYamlReference", false)]
    [InlineData("IxIFlow.Generators.Tests.SwitchYamlReference", true)]
    public async Task NullLabelsForReferenceAndInterfaceTypesMatchFluent(string valueType, bool isNull)
    {
        var custom = valueType.EndsWith("SwitchYamlReference", StringComparison.Ordinal);
        var selector = custom ? "workflow.Reference" : "workflow.Text";
        var yaml = Header + "- kind: switch\n  valueType: " + valueType + "\n  value: {path: '" + selector + "'}\n" +
            "  cases: [{value: {kind: constant, value: null}, steps: " + Mark("selected") + "}]\n";
        void Selected(IWorkflowBuilder<SwitchYamlData, object> b) => b.Step<SwitchYamlMark>(a => a.Input(x => x.Text)
            .From(ctx => "selected").Output(x => x.Text).To(ctx => ctx.WorkflowData.Text));
        var fluent = valueType switch
        {
            "System.IComparable" => Workflow.Create<SwitchYamlData>("SwitchYaml").Switch<IComparable?>(ctx => ctx.WorkflowData.Text, c => c.Case(null, Selected)).Build(),
            "System.String" => Workflow.Create<SwitchYamlData>("SwitchYaml").Switch<string?>(ctx => ctx.WorkflowData.Text, c => c.Case(null, Selected)).Build(),
            _ => Workflow.Create<SwitchYamlData>("SwitchYaml").Switch<SwitchYamlReference?>(ctx => ctx.WorkflowData.Reference, c => c.Case(null, Selected)).Build()
        };
        foreach (var source in Readers(yaml))
        {
            var generated = GeneratorRegressionTests.Build(source);
            Assert.Equal(Hash(fluent), Hash(generated));
            foreach (var definition in new[] { fluent, generated })
            {
                await using var services = new ServiceCollection().AddLogging().AddIxIFlow().Services.BuildServiceProvider();
                var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition,
                    new SwitchYamlData { Text = isNull ? null! : "other" });
                Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
                Assert.Equal(custom || isNull ? "selected" : "other", Assert.IsType<SwitchYamlData>(result.WorkflowData).Text);
            }
        }
    }

    [Theory]
    [InlineData(1, true, false)] [InlineData(2, true, false)] [InlineData(2, false, false)]
    [InlineData(1, true, true)] [InlineData(2, true, true)]
    public async Task CasesDefaultNoMatchAndEmptyScopesMatchThroughBothReaders(int route, bool fallback, bool empty)
    {
        var child = empty ? "[]" : Mark("case");
        var yaml = Header + "- kind: switch\n  value: {path: workflow.Route}\n  valueType: int\n  cases:\n" +
            "    - value: {kind: constant, value: 1}\n      steps: " + child + "\n" +
            (fallback ? "  default: " + (empty ? "[]" : Mark("default")) + "\n" : "");
        var fluent = Workflow.Create<SwitchYamlData>("SwitchYaml").Switch(ctx => ctx.WorkflowData.Route, c =>
        {
            c.Case(1, b => { if (!empty) b.Step<SwitchYamlMark>(a => a.Input(x => x.Text).From(ctx => "case").Output(x => x.Text).To(ctx => ctx.WorkflowData.Text)); });
            if (fallback) c.Default(b => { if (!empty) b.Step<SwitchYamlMark>(a => a.Input(x => x.Text).From(ctx => "default").Output(x => x.Text).To(ctx => ctx.WorkflowData.Text)); });
        }).Build();
        foreach (var source in Readers(yaml))
        {
            var generated = GeneratorRegressionTests.Build(source);
            Assert.Equal(Hash(fluent), Hash(generated));
            Assert.Equal(await Execute(fluent, route), await Execute(generated, route));
            Assert.Equal(await ControlTrace(fluent, route), await ControlTrace(generated, route));
        }
    }

    [Theory]
    [InlineData("enum")] [InlineData("decimal")] [InlineData("tuple")] [InlineData("private")]
    [InlineData("object")] [InlineData("null")]
    public async Task TypedLabelAssetsAndExplicitValueTypesMatchFluent(string type)
    {
        var (selector, label, valueType) = type switch
        {
            "enum" => ("System.DayOfWeek.Monday", "System.DayOfWeek.Monday", "System.DayOfWeek"),
            "decimal" => ("1.25m", "1.250m", "decimal"),
            "tuple" => ("new System.ValueTuple<int>(1)", "new System.ValueTuple<int>(1)", "System.ValueTuple<int>"),
            "private" => ("new IxIFlow.Generators.Tests.SwitchYamlLabel(1)", "new IxIFlow.Generators.Tests.SwitchYamlLabel(1)", "IxIFlow.Generators.Tests.SwitchYamlLabel"),
            "object" => ("(object)1", "1L", "object"),
            _ => ("(string?)null", "(string?)null", "string?")
        };
        var yaml = Header + "- kind: switch\n  valueType: '" + valueType + "'\n  value: {kind: function, function: selector}\n" +
            "  cases: [{value: {kind: function, function: label}, steps: " + Mark("selected") + "}]\n" +
            "code:\n  selector: {language: csharp-expression, source: '" + selector + "'}\n  label: {language: csharp-expression, source: '" + label + "'}\n";
        var builder = Workflow.Create<SwitchYamlData>("SwitchYaml");
        WorkflowDefinition fluent;
        void Selected(IWorkflowBuilder<SwitchYamlData, object> body) => body.Step<SwitchYamlMark>(s => s.Input(a => a.Text).From(ctx => "selected").Output(a => a.Text).To(ctx => ctx.WorkflowData.Text));
        fluent = type switch
        {
            "enum" => builder.Switch(ctx => DayOfWeek.Monday, c => c.Case(DayOfWeek.Monday, Selected)).Build(),
            "decimal" => builder.Switch(ctx => 1.25m, c => c.Case(1.250m, Selected)).Build(),
            "tuple" => builder.Switch(ctx => new ValueTuple<int>(1), c => c.Case(new ValueTuple<int>(1), Selected)).Build(),
            "private" => builder.Switch(ctx => new SwitchYamlLabel(1), c => c.Case(new SwitchYamlLabel(1), Selected)).Build(),
            "object" => builder.Switch<object>(ctx => (object)1, c => c.Case(1L, Selected)).Build(),
            _ => builder.Switch<string?>(ctx => (string?)null, c => c.Case(null, Selected)).Build()
        };
        foreach (var source in Readers(yaml))
        {
            var generated = GeneratorRegressionTests.Build(source);
            Assert.Equal(Hash(fluent), Hash(generated));
            Assert.Equal(type == "object" ? "" : "selected", await Execute(generated, 1));
        }
    }

    [Theory]
    [InlineData("sequence")] [InlineData("if")] [InlineData("switch")] [InlineData("while")]
    [InlineData("do")] [InlineData("parallel")] [InlineData("each")] [InlineData("parallel-each")]
    [InlineData("graph")] [InlineData("state")] [InlineData("try")] [InlineData("saga")]
    public async Task LexicalFaultReadsCompileBuildAndMatchFluentInEveryChildCallback(string shape)
    {
        var read = "{kind: switch, value: {path: fault.Message}, cases: [{value: {kind: constant, value: fault}, steps: " + MarkFault + "}]}";
        var nested = shape switch
        {
            "sequence" => "{kind: sequence, steps: [" + read + "]}",
            "if" => "{kind: if, condition: {path: fault.Message == \"fault\"}, then: [" + read + "]}",
            "switch" => "{kind: switch, value: {path: fault.Message}, cases: [{value: {kind: constant, value: fault}, steps: [" + read + "]}]}",
            "while" => "{kind: whileDo, condition: {path: 'fault.Message == \"fault\" && workflow.Text == \"\"'}, steps: [" + read + "]}",
            "do" => "{kind: doWhile, condition: {path: fault.Message == \"again\"}, steps: [" + read + "]}",
            "parallel" => "{kind: parallel, branches: [{steps: [" + read + "]}]}",
            "each" => "{kind: forEach, items: {path: workflow.Items}, as: item, steps: [" + read + "]}",
            "parallel-each" => "{kind: forEach, items: {path: workflow.Items}, as: item, parallel: {maxConcurrency: 1, join: any}, steps: [" + read + "]}",
            "graph" => "{kind: flowchart, start: a, nodes: [{key: a, steps: [" + read + "]}]}",
            "state" => "{kind: stateMachine, start: a, states: [{key: a, entry: [" + read + "], transitions: [{to: done, steps: [" + read + "]}]}, {key: done, final: true}]}",
            "try" => "{kind: try, steps: [" + read + "], catches: [{exception: System.ArgumentException, steps: [{kind: activity, activity: IxIFlow.Generators.Tests.SwitchYamlSeed}]}], finally: [" + read + "]}",
            _ => "{kind: saga, steps: [" + read + "]}"
        };
        var yaml = Header + "- {kind: activity, activity: IxIFlow.Generators.Tests.SwitchYamlSeed}\n" +
            "- kind: try\n  steps: [{kind: activity, activity: IxIFlow.Generators.Tests.SwitchYamlFail}]\n" +
            "  catches: [{exception: System.InvalidOperationException, faultType: IxIFlow.Generators.Tests.SwitchYamlFault, steps: [" + nested + "]}]\n";
        var fluent = Workflow.Create<SwitchYamlData>("SwitchYaml").Step<SwitchYamlSeed>().Try(t => t.Step<SwitchYamlFail>())
            .Catch<InvalidOperationException, SwitchYamlFault>(caught =>
            {
                void Read(ICatchWorkflowBuilder<SwitchYamlData, SwitchYamlFault, SwitchYamlSeed> body) => body.Switch(ctx => ctx.Fault.Message, c => c.Case("fault", b => b.Step<SwitchYamlMark>(a => a.Input(x => x.Text).From(ctx => ctx.Fault.Message).Output(x => x.Text).To(ctx => ctx.WorkflowData.Text))));
                switch (shape)
                {
                    case "sequence": caught.Sequence(Read); break;
                    case "if": caught.If(ctx => ctx.Fault.Message == "fault", Read); break;
                    case "switch": caught.Switch(ctx => ctx.Fault.Message, c => c.Case("fault", Read)); break;
                    case "while": caught.WhileDo(conditionExpression: ctx => ctx.Fault.Message == "fault" && ctx.WorkflowData.Text == "", configure: Read); break;
                    case "do": caught.DoWhile(Read, conditionExpression: ctx => ctx.Fault.Message == "again"); break;
                    case "parallel": caught.Parallel(p => p.Do(Read)); break;
                    case "each": caught.ForEach(itemsExpression: ctx => ctx.WorkflowData.Items, body: (b, item) => Read(b), setup: s => s.As("item")); break;
                    case "parallel-each": caught.ForEach(itemsExpression: ctx => ctx.WorkflowData.Items, body: (b, item) => Read(b), setup: s => s.As("item").Parallel(1).WaitAny()); break;
                    case "graph": caught.Flowchart(g => g.Start("a").Node("a", b => { Read(b); return b; })); break;
                    case "state": caught.StateMachine(m => m.Start("a").State("a", s => s.Entry(b => { Read(b); return b; }).Immediately(t => t.Otherwise("done", Read))).State("done", s => s.Final())); break;
                    case "try": caught.Try(Read).Catch<ArgumentException>(b => b.Step<SwitchYamlSeed>()).Finally(Read); break;
                    case "saga": caught.Saga(s => s.Switch(ctx => ctx.Fault.Message, c => c.Case("fault", b => b.Step<SwitchYamlMark>(a => a.Input(x => x.Text).From(ctx => ctx.Fault.Message).Output(x => x.Text).To(ctx => ctx.WorkflowData.Text))))); break;
                }
            }).Build();
        foreach (var source in Readers(yaml))
        {
            var generated = GeneratorRegressionTests.Build(source);
            Assert.Equal(Hash(fluent), Hash(generated));
            Assert.Equal("fault", await Execute(generated, 1));
        }
    }

    [Theory]
    [InlineData("{kind: switch}", "SWITCH001")]
    [InlineData("{kind: switch, valueType: int, value: {kind: constant, value: 1}, cases: [{value: {kind: constant, value: wrong}}]}", "SWITCH002")]
    [InlineData("{kind: switch, value: {kind: constant, value: 1}, cases: [{value: {kind: constant, value: 1}}, {value: {kind: constant, value: 1}}]}", "SWITCH003")]
    [InlineData("{kind: switch, value: {kind: constant, value: 1}, cases: [{value: {kind: function, function: opaque}}]}", "SWITCH004")]
    public void InvalidSwitchesHavePositionedDiagnostics(string step, string code)
    {
        var yaml = Header + "- " + step + "\ncode:\n  opaque: {language: csharp-expression, source: 'new object()'}\n";
        if (code == "SWITCH004") yaml = yaml.Replace("value: {kind: constant, value: 1}", "valueType: object, value: {kind: constant, value: 1}");
        var error = Record.Exception(() => GeneratorRegressionTests.Build(yaml));
        Assert.NotNull(error);
        Assert.Contains(code == "SWITCH001" ? "Missing" : code, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("regression.yaml", error.Message);
    }

    [Theory]
    [InlineData("fault.Message")] [InlineData("workflow.Route")] [InlineData("ctx.UtcNow")]
    public async Task LabelAssetsHaveNoRuntimeContext(string expression)
    {
        var yaml = Header + "- {kind: switch, value: {kind: constant, value: 1}, cases: [{value: {kind: function, function: label}}]}\n" +
            "code:\n  label: {language: csharp-expression, source: '" + expression + "'}\n";
        var document = WorkflowYamlSerializer.Read(yaml);
        var validation = await new WorkflowDocumentValidator().ValidateAsync(document, new WorkflowSourceContracts(new WorkflowSourceOptions()));
        var diagnostic = Assert.Single(validation.Diagnostics, d => d.Code == "EXPR001");
        Assert.Equal("definitions[0].cases[0].value", diagnostic.Path);
        Assert.Contains("'" + expression.Split('.')[0] + "'", diagnostic.Message);
        var error = Assert.Throws<WorkflowSourceException>(() => WorkflowSourceTranslator.Translate(document, "labels.yaml"));
        var member = ((SwitchStepDocument)document.Definitions[0]).Cases[0].Value.Location;
        Assert.Equal(member.Line, error.Line);
        Assert.Equal(member.Column, error.Column);
    }

    [Fact]
    public async Task SwitchTemplatesAcquireTheirCallersLexicalFaultBinding()
    {
        var template = "steps:\n  read:\n    steps: [{kind: switch, value: {path: fault.Message}, cases: [{value: {kind: constant, value: fault}, steps: " + MarkFault + "}]}]\n";
        var yaml = Header + "- {kind: activity, activity: IxIFlow.Generators.Tests.SwitchYamlSeed}\n" +
            "- kind: try\n  steps: [{kind: activity, activity: IxIFlow.Generators.Tests.SwitchYamlFail}]\n" +
            "  catches: [{exception: System.InvalidOperationException, faultType: IxIFlow.Generators.Tests.SwitchYamlFault, steps: [{kind: stepRef, ref: read}]}]\n" + template;
        Assert.Equal("fault", await Execute(GeneratorRegressionTests.Build(yaml), 1));
        var invalid = Header + "- {kind: stepRef, ref: read}\n" + template;
        Assert.Throws<WorkflowSourceException>(() => WorkflowSourceTranslator.Translate(invalid, "outside.yaml"));
    }

    [Fact]
    public async Task NestedSwitchFaultBindingsShadowAndRestoreAfterASavedJoinedWait()
    {
        var inner = "{kind: switch, value: {path: fault.Message}, cases: [{value: {kind: constant, value: inner}, steps: " + MarkFault + "}]}";
        var cleanup = "{kind: switch, value: {path: fault.Message}, cases: [{value: {kind: constant, value: fault}, steps: " +
            MarkFault.Replace("{path: fault.Message}", "{path: 'workflow.Text + \"|\" + fault.Message'}") + "}]}";
        var yaml = Header + "- {kind: activity, activity: IxIFlow.Generators.Tests.SwitchYamlSeed}\n" +
            "- kind: try\n  steps: [{kind: activity, activity: IxIFlow.Generators.Tests.SwitchYamlFail}]\n" +
            "  catches: [{exception: System.InvalidOperationException, faultType: IxIFlow.Generators.Tests.SwitchYamlFault, steps: [" +
            "{kind: parallel, branches: [{steps: [{kind: try, steps: [{kind: activity, activity: IxIFlow.Generators.Tests.SwitchYamlFailInner}], " +
            "catches: [{exception: System.ArgumentException, faultType: IxIFlow.Generators.Tests.SwitchYamlFault, steps: [" +
            "{kind: waitFor, key: inner, event: IxIFlow.Generators.Tests.SwitchYamlGo, resumeCondition: {path: 'fault.Message == \"inner\"'}}, " + inner + "]}], finally: [" + cleanup + "]}]}]}]}]\n";
        var definition = GeneratorRegressionTests.Build(yaml);
        var repository = new InMemoryWorkflowStateRepository();
        var registry = new WorkflowCatalog();
        await registry.RegisterWorkflowAsync(definition);
        ServiceProvider Services() => new ServiceCollection().AddLogging().AddIxIFlow(o => o.PayloadThresholdBytes = 1).Services
            .AddSingleton<IWorkflowStateRepository>(repository).AddSingleton<IWorkflowCatalog>(registry).BuildServiceProvider();
        string id;
        await using (var first = Services())
        {
            var idle = await first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new SwitchYamlData());
            Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
            id = idle.InstanceId;
        }
        await using var second = Services();
        var result = await second.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "inner", new SwitchYamlGo());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("inner|fault", Assert.IsType<SwitchYamlData>(result.WorkflowData).Text);
        Assert.Throws<WorkflowSourceException>(() => WorkflowSourceTranslator.Translate(Header + "- " + inner, "child.yaml"));
    }

    [Theory]
    [InlineData("ctx.WorkflowData.Items.Remove(1)")]
    [InlineData("Remove(ctx.WorkflowData.Items)")]
    [InlineData("new Bad(ctx.WorkflowData.Items).Value")]
    [InlineData("new Bad(ctx.WorkflowData.Items) + 1")]
    public async Task SwitchMutationAnalyzerFollowsArgumentAndReceiverAliases(string expression)
    {
        var source = """
            using System.Collections.Generic;
            using IxIFlow.Builders;
            using IxIFlow.Generators.Tests;
            public static class Author
            {
                public static void Build() { Workflow.Create<SwitchYamlData>("Mutation").Switch(ctx => EXPR, c => {}); }
                private static bool Remove(List<int> items) { var alias = items; return alias.Remove(1); }
                private sealed class Bad
                {
                    private readonly List<int> _items;
                    public Bad(List<int> items) { _items = items; items.Clear(); }
                    public bool Value => _items.Remove(1);
                    public static bool operator +(Bad value, int amount) => value._items.Remove(amount);
                }
            }
            """.Replace("EXPR", expression);
        var compilation = GeneratorRegressionTests.Compilation(source);
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new PurityAnalyzer())).GetAnalyzerDiagnosticsAsync();
        Assert.Contains(diagnostics, diagnostic => diagnostic.GetMessage().Contains("state mutation"));
    }

    [Fact]
    public async Task SwitchMutationAnalyzerAllowsFreshReplacementCollections()
    {
        var compilation = GeneratorRegressionTests.Compilation("""
            using System.Collections.Generic;
            using IxIFlow.Builders;
            using IxIFlow.Generators.Tests;
            public static class Author
            {
                public static void Build() { Workflow.Create<SwitchYamlData>("Fresh").Switch(ctx => Make(ctx.WorkflowData.Route), c => c.Case(new List<int>{1}.Count, b => {})); }
                private static int Make(int n) { var items = new List<int>(); var alias = items; alias.Add(n); return items.Count; }
            }
            """);
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new PurityAnalyzer())).GetAnalyzerDiagnosticsAsync();
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void SwitchSchemaIsGeneratedFromTheSharedDocumentContract()
    {
        var schema = File.ReadAllText(WorkflowSchemaFiles.SchemaPath);
        Assert.Equal(WorkflowDocumentSchema.Write(), schema);
        var parsed = JsonSchema.FromText(schema, new BuildOptions { SchemaRegistry = new SchemaRegistry() });
        // Building the same $id again must use another private registry as well.
        _ = JsonSchema.FromText(schema, new BuildOptions { SchemaRegistry = new SchemaRegistry() });
        var valid = Header + "- {kind: switch, valueType: int, value: {path: workflow.Route}, cases: [{value: {kind: constant, value: 1}, steps: []}], default: []}\n";
        Assert.True(parsed.Evaluate(WorkflowYamlSchemaValueTests.Read(valid)).IsValid);
        Assert.False(parsed.Evaluate(WorkflowYamlSchemaValueTests.Read(valid.Replace("valueType: int", "valueType: 42"))).IsValid);
        Assert.False(parsed.Evaluate(WorkflowYamlSchemaValueTests.Read(valid.Replace("kind: switch", "kind: outcome"))).IsValid);
    }

    [Theory]
    [InlineData("ship", "shipping")] [InlineData("hold", "on hold")] [InlineData("other", "manual review")]
    public async Task SwitchSampleFluentAndGeneratedYamlHaveIdenticalRouting(string route, string expected)
    {
        var fluent = IxIFlow.Samples.SwitchRouting.Definition();
        var generated = IxIFlow.Samples.Generated.Workflow_switch_routing_workflow.Build();
        Assert.Equal(Hash(fluent), Hash(generated));
        foreach (var definition in new[] { fluent, generated })
        {
            await using var services = new ServiceCollection().AddLogging().AddIxIFlow().Services.BuildServiceProvider();
            var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new IxIFlow.Samples.RoutingData { Route = route });
            Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
            Assert.Equal(expected, Assert.IsType<IxIFlow.Samples.RoutingData>(result.WorkflowData).Result);
        }
    }

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public async Task SavedSwitchInspectionScopesAndCatalogsMatchFluent(int route)
    {
        var yaml = Header + "- {kind: switch, value: {path: workflow.Route}, cases: [{value: {kind: constant, value: 1}, steps: [{kind: waitFor, key: inside, event: IxIFlow.Generators.Tests.SwitchYamlGo}]}], " +
            "default: [{kind: waitFor, key: inside, event: IxIFlow.Generators.Tests.SwitchYamlGo}]}\n";
        var fluent = Workflow.Create<SwitchYamlData>("SwitchYaml").Switch(ctx => ctx.WorkflowData.Route,
            c => c.Case(1, b => b.WaitFor<SwitchYamlGo>("inside")).Default(b => b.WaitFor<SwitchYamlGo>("inside"))).Build();
        var generated = GeneratorRegressionTests.Build(yaml);
        Assert.Equal(Hash(fluent), Hash(generated));
        var frames = new List<string[]>();
        foreach (var definition in new[] { fluent, generated })
        {
            await using var services = new ServiceCollection().AddLogging().AddIxIFlow(o => o.Debugging = true).Services.BuildServiceProvider();
            var run = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new SwitchYamlData { Route = route });
            Assert.Equal(WorkflowExecutionStatus.Suspended, run.Status);
            var inspection = (await services.GetRequiredService<IWorkflowOperations>().InspectAsync(run.InstanceId))!;
            frames.Add(inspection.Threads.SelectMany(t => t.Frames).Select(f => f.Kind + ":" + f.ScopeId + ":" + f.NextStepIndex).ToArray());
        }
        Assert.Equal(frames[0], frames[1]);
        Assert.Equal(fluent.SwitchTable.Single().ValueType, generated.SwitchTable.Single().ValueType);
        Assert.Equal(fluent.SwitchTable.Single().Cases.ToArray(), generated.SwitchTable.Single().Cases.ToArray());
    }

    private const string Header = "workflow: {name: SwitchYaml, dataType: IxIFlow.Generators.Tests.SwitchYamlData}\ndefinitions:\n";
    private static string Mark(string value) => "[{kind: activity, activity: IxIFlow.Generators.Tests.SwitchYamlMark, input: [{target: Text, from: {kind: constant, value: " + value + "}}], output: [{source: Text, to: {path: workflow.Text}}]}]";
    private const string MarkFault = "[{kind: activity, activity: IxIFlow.Generators.Tests.SwitchYamlMark, input: [{target: Text, from: {path: fault.Message}}], output: [{source: Text, to: {path: workflow.Text}}]}]";
    private static IEnumerable<string> Readers(string yaml)
    {
        var document = WorkflowYamlSerializer.Read(yaml);
        yield return yaml;
        yield return WorkflowYamlSerializer.Write(document);
        yield return WorkflowYamlSerializer.Write(WorkflowJsonSerializer.Read(WorkflowJsonSerializer.Write(document)));
    }
    private static string Hash(WorkflowDefinition definition) => WorkflowDefinitionValidator.Inspect(definition);
    private static async Task<string[]> ControlTrace(WorkflowDefinition definition, int route)
    {
        await using var services = new ServiceCollection().AddLogging().AddIxIFlow(o => o.Debugging = true).Services.BuildServiceProvider();
        var run = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new SwitchYamlData { Route = route });
        var trace = await services.GetRequiredService<IWorkflowOperations>().GetTraceAsync(run.InstanceId);
        return trace.Where(t => t.Entry.EntryType is TraceEntryType.StepEntered or TraceEntryType.SwitchSelected)
            .Select(t => t.Entry.Payload is SwitchTrace selected ? $"{t.Entry.EntryType}:{selected.StepId}:{selected.Path}:{selected.Index}:{selected.ScopeId}" : $"{t.Entry.EntryType}:{t.Entry.StepId}").ToArray();
    }
    private static async Task<string> Execute(WorkflowDefinition definition, int route)
    {
        await using var services = new ServiceCollection().AddLogging().AddIxIFlow().Services.BuildServiceProvider();
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new SwitchYamlData { Route = route });
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        return Assert.IsType<SwitchYamlData>(result.WorkflowData).Text;
    }
}
public sealed class SwitchYamlData
{
    public SwitchYamlReference? Reference { get; set; }
    public int Route { get; set; }
    public string Text { get; set; } = "";
    public List<int> Items { get; set; } = [1];
}
public sealed class SwitchYamlReference { }
public readonly struct SwitchYamlLabel(int value) : IEquatable<SwitchYamlLabel>
{
    private readonly int _value = value;
    public bool Equals(SwitchYamlLabel other) => _value == other._value;
}
public sealed class SwitchYamlFault { public string Message { get; set; } = ""; }
public sealed class SwitchYamlMark : IAsyncActivity
{
    public string Text { get; set; } = "";
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
public sealed class SwitchYamlSeed : IAsyncActivity
{ public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask; }
public sealed class SwitchYamlFail : IAsyncActivity
{ public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.FromException(new InvalidOperationException("fault")); }
public sealed class SwitchYamlFailInner : IAsyncActivity
{ public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.FromException(new ArgumentException("inner")); }
public sealed class SwitchYamlGo { }
