using System.Collections.Immutable;
using IxIFlow.Analyzers;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Generators.Tests;

[Trait("Feature", "authoring"), Trait("Spec", "W-03")]
public sealed class SwitchReviewYamlTests
{
    private const string Header = "workflow: {name: SwitchReview, dataType: IxIFlow.Generators.Tests.SwitchYamlData}\ndefinitions:\n";
    private const string Mark = "IxIFlow.Generators.Tests.SwitchYamlMark";
    private const string Go = "IxIFlow.Generators.Tests.SwitchReviewGo";

    [Theory]
    [InlineData("sequence")]
    [InlineData("if")]
    [InlineData("switch")]
    [InlineData("parallel")]
    [InlineData("each")]
    [InlineData("graph")]
    [InlineData("state")]
    [InlineData("try")]
    public async Task NestedCatchSuspendPredicatesUseLexicalPreviousAndRecoverLikeFluent(string shape)
    {
        var wait = "{kind: suspend, reason: go, event: " + Go +
            ", resumeCondition: {path: 'previousStep.Text == \"entry\" && event.Text == fault.Message'}, output: [{source: Text, to: {path: workflow.Text}}]}";

        var nested = shape switch
        {
            "sequence" => "{kind: sequence, steps: [" + wait + "]}",
            "if" => "{kind: if, condition: {kind: constant, value: true}, then: [" + wait + "]}",
            "switch" => "{kind: switch, value: {path: fault.Message}, cases: [{value: {kind: constant, value: fault}, steps: [" + wait +
                "]}]}",
            "parallel" => "{kind: parallel, branches: [{steps: [" + wait + "]}]}",
            "each" => "{kind: forEach, items: {path: workflow.Items}, as: item, steps: [" + wait + "]}",
            "graph" => "{kind: flowchart, start: a, nodes: [{key: a, steps: [" + wait + "]}]}",
            "state" => "{kind: stateMachine, start: a, states: [{key: a, entry: [" + wait +
                "], transitions: [{to: done}]}, {key: done, final: true}]}",
            _ => "{kind: try, steps: [" + wait + "], finally: []}"
        };

        var yaml = Header + "- {kind: activity, activity: " + Mark + ", input: [{target: Text, from: {kind: constant, value: entry}}]}\n" +
            "- {kind: try, steps: [{kind: activity, activity: IxIFlow.Generators.Tests.SwitchYamlFail}], catches: [{exception: System.InvalidOperationException, faultType: IxIFlow.Generators.Tests.SwitchYamlFault, steps: [" +
            nested + "]}]}\n";

        var fluent = Workflow.Create<SwitchYamlData>("SwitchReview").Step<SwitchYamlMark>(a => a.Input(x => x.Text).From(ctx => "entry"))
            .Try(t => t.Step<SwitchYamlFail>()).Catch<InvalidOperationException, SwitchYamlFault>(caught =>
            {
                ICatchWorkflowBuilder<SwitchYamlData, SwitchYamlFault, SwitchReviewGo> AddWait(
                    ICatchWorkflowBuilder<SwitchYamlData, SwitchYamlFault, SwitchYamlMark> b) => b.Suspend<SwitchReviewGo>("go",
                    matchesExpression: (evt, ctx) => ctx.PreviousStep.Text == "entry" && evt.Text == ctx.Fault.Message,
                    configure: s => s.Output(x => x.Text).To(ctx => ctx.WorkflowData.Text));

                switch (shape)
                {
                    case "sequence":
                        caught.Sequence(b => AddWait(b));

                        break;
                    case "if":
                        caught.If(ctx => true, b => AddWait(b));

                        break;
                    case "switch":
                        caught.Switch(ctx => ctx.Fault.Message, c => c.Case("fault", b => AddWait(b)));

                        break;
                    case "parallel":
                        caught.Parallel(p => p.Do(b => AddWait(b)));

                        break;
                    case "each":
                        caught.ForEach(itemsExpression: ctx => ctx.WorkflowData.Items, body: (b, item) => AddWait(b),
                            setup: s => s.As("item"));

                        break;
                    case "graph":
                        caught.Flowchart(g => g.Start("a").Node("a", b => AddWait(b)));

                        break;
                    case "state":
                        caught.StateMachine(m =>
                            m.Start("a").State("a", s => s.Entry(b => AddWait(b)).Otherwise("done")).State("done", s => s.Final()));

                        break;
                    default:
                        caught.Try(b => AddWait(b)).Finally(_ =>
                        {
                        });

                        break;
                }
            }).Build();

        var generated = GeneratorRegressionTests.Build(yaml);
        Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(generated));

        foreach (var definition in new[] { fluent, generated })
            await RecoverWait(definition);
    }

    [Fact]
    public async Task CatchWaitForAcceptsPreviousStepInYaml()
    {
        var yaml = Header + "- {kind: activity, activity: " + Mark + ", input: [{target: Text, from: {kind: constant, value: entry}}]}\n" +
            "- {kind: try, steps: [{kind: activity, activity: IxIFlow.Generators.Tests.SwitchYamlFail}], catches: [{exception: System.InvalidOperationException, faultType: IxIFlow.Generators.Tests.SwitchYamlFault, steps: [{kind: waitFor, key: go, event: " +
            Go +
            ", resumeCondition: {path: 'previousStep.Text == \"entry\" && event.Text == fault.Message'}, output: [{source: Text, to: {path: workflow.Text}}]}]}]}";

        await RecoverWait(GeneratorRegressionTests.Build(yaml));
    }

    [Fact]
    public async Task StateIntakePreviousValueHasFluentYamlRecoveryParity()
    {
        var yaml = Header + "- {kind: activity, activity: " + Mark + ", input: [{target: Text, from: {kind: constant, value: entry}}]}\n" +
            "- {kind: try, steps: [{kind: activity, activity: IxIFlow.Generators.Tests.SwitchYamlFail}], catches: [{exception: System.InvalidOperationException, faultType: IxIFlow.Generators.Tests.SwitchYamlFault, steps: [{kind: stateMachine, start: a, states: [{key: a, transitions: [{on: go, event: " +
            Go +
            ", matches: {path: 'previousStep.Text == \"entry\" && event.Text == fault.Message'}, to: done, when: {path: 'previousStep.Text == \"fault\"'}, steps: [{kind: activity, activity: " +
            Mark +
            ", input: [{target: Text, from: {path: previousStep.Text}}], output: [{source: Text, to: {path: workflow.Text}}]}]}]}, {key: done, final: true}]}]}]}";

        var fluent = Workflow.Create<SwitchYamlData>("SwitchReview").Step<SwitchYamlMark>(a => a.Input(x => x.Text).From(ctx => "entry"))
            .Try(t => t.Step<SwitchYamlFail>()).Catch<InvalidOperationException, SwitchYamlFault>(c => c.StateMachine(m => m.Start("a")
                .State("a", s => s.On<SwitchReviewGo>("go",
                    t => t.Matches((evt, ctx) => ctx.PreviousStep.Text == "entry" && evt.Text == ctx.Fault.Message)
                        .When(ctx => ctx.PreviousStep.Text == "fault", "done",
                            b => b.Step<SwitchYamlMark>(a =>
                                a.Input(x => x.Text).From(ctx => ctx.PreviousStep.Text).Output(x => x.Text)
                                    .To(ctx => ctx.WorkflowData.Text)))))
                .State("done", s => s.Final()))).Build();

        var generated = GeneratorRegressionTests.Build(yaml);
        Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(generated));

        foreach (var definition in new[] { fluent, generated })
            await RecoverWait(definition);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FaultForEachExitAfterTryRetainsItsLexicalBuilder(bool resultStep)
    {
        var yaml = "workflow: {name: SwitchResult, dataType: IxIFlow.Generators.Tests.SwitchResultData}\ndefinitions:\n" +
            "- {kind: activity, activity: IxIFlow.Generators.Tests.SwitchYamlSeed}\n" +
            "- {kind: try, steps: [{kind: activity, activity: IxIFlow.Generators.Tests.SwitchYamlFail}], catches: [{exception: System.InvalidOperationException, faultType: IxIFlow.Generators.Tests.SwitchYamlFault, steps: [{kind: forEach, items: {path: workflow.Items}, as: item, steps: [{kind: try, steps: [], finally: []}" +
            (resultStep ? ", {kind: result, value: {path: fault.Message}}]" : "], result: {path: fault.Message}") +
            ", into: {path: workflow.Results}}]}]}";

        var fluent = Workflow.Create<SwitchResultData>("SwitchResult").Step<SwitchYamlSeed>().Try(t => t.Step<SwitchYamlFail>())
            .Catch<InvalidOperationException, SwitchYamlFault>(c => c.ForEach(itemsExpression: ctx => ctx.WorkflowData.Items,
                body: (b, item) =>
                {
                    b.Try(_ =>
                    {
                    }).Finally(_ =>
                    {
                    });

                    b.Result(ctx => ctx.Fault.Message);
                }, setup: s => s.As("item").Into<string>(ctx => ctx.WorkflowData.Results))).Build();

        var generated = GeneratorRegressionTests.Build(yaml);
        Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(generated));

        foreach (var definition in new[] { fluent, generated })
        {
            await using var services = new ServiceCollection().AddLogging().AddIxIFlow().Services.BuildServiceProvider();
            var run = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new SwitchResultData());
            Assert.Equal(WorkflowExecutionStatus.Success, run.Status);
            Assert.Equal("fault", Assert.Single(Assert.IsType<SwitchResultData>(run.WorkflowData).Results).Value);
        }
    }

    [Theory]
    [InlineData("{kind: result, value: {kind: constant, value: true}}", "Result")]
    [InlineData("{kind: transition, to: end}", "Transition")]
    public async Task DirectCatchExitsAreRejectedAtTheirAuthoredMember(string step, string member)
    {
        var yaml = Header + "- {kind: try, steps: [], catches: [{steps: [" + step + "]}]}";
        var document = IxIFlow.Dsl.Documents.WorkflowYamlSerializer.Read(yaml);

        var validation =
            await new IxIFlow.Dsl.Compilation.WorkflowDocumentValidator().ValidateAsync(document, new WorkflowSourceContracts(new()));

        var diagnostic = Assert.Single(validation.Diagnostics, d => d.Code == "SCOPE001");
        Assert.Equal("definitions[0].catches[0].steps[0]", diagnostic.Path);
        var error = Assert.Throws<WorkflowSourceException>(() => WorkflowSourceTranslator.Translate(document, "exit.yaml"));
        Assert.True(error.Column > 1);

        var source =
            "using IxIFlow.Builders; using IxIFlow.Generators.Tests; public class Author { public void Build() { Workflow.Create<SwitchYamlData>(\"Exit\").Step<SwitchYamlSeed>().Try(t => {}).Catch(c => c." +
            (member == "Result" ? "Result(ctx => true)" : "Transition(null)") + "); } }";

        Assert.Contains(GeneratorRegressionTests.Compilation(source).GetDiagnostics(),
            d => d.Id == "CS1061" && d.GetMessage().Contains(member));
    }

    [Theory]
    [InlineData("- {kind: switch, value: {kind: constant, value: true}}")]
    [InlineData(
        "- {kind: try, steps: [{kind: activity, activity: IxIFlow.Generators.Tests.SwitchYamlFail}], catches: [{steps: [{kind: activity, activity: IxIFlow.Generators.Tests.SwitchYamlMark, name: named}]}]}")]
    [InlineData(
        "- {kind: try, steps: [{kind: activity, activity: IxIFlow.Generators.Tests.SwitchYamlFail}], catches: [{steps: [{kind: delay, duration: '00:00:01'}]}]}")]
    public async Task FormerNegativeShapesCompileExecuteAndRecover(string steps)
    {
        var definition = GeneratorRegressionTests.Build(Header + steps);
        var time = new FakeTimeProvider();
        var repository = new InMemoryWorkflowStateRepository(time);
        var registry = new WorkflowCatalog();
        await registry.RegisterWorkflowAsync(definition);

        ServiceProvider Services() => new ServiceCollection().AddLogging().AddIxIFlow(o => o.Debugging = true).Services
            .AddSingleton<IWorkflowStateRepository>(repository).AddSingleton<IWorkflowCatalog>(registry).AddSingleton<TimeProvider>(time)
            .BuildServiceProvider();

        WorkflowExecutionResult run;

        await using (var first = Services())
            run = await first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new SwitchYamlData());

        if (steps.Contains("kind: delay"))
        {
            Assert.Equal(WorkflowExecutionStatus.Suspended, run.Status);
            time.Advance(TimeSpan.FromSeconds(2));
            await using var second = Services();
            run = await second.GetRequiredService<IWorkflowClient>().RecoverAsync(run.InstanceId);
        }

        Assert.Equal(WorkflowExecutionStatus.Success, run.Status);

        if (steps.Contains("name: named"))
            Assert.Contains(definition.StepTable, s => s.Name == "named");
    }

    [Fact]
    public async Task SagaHandlerSwitchKeepsOneOuterPolicyInYamlAndFluent()
    {
        var yaml = Header +
            "- {kind: activity, activity: IxIFlow.Generators.Tests.SwitchYamlSeed}\n- {kind: saga, steps: [{kind: activity, activity: IxIFlow.Generators.Tests.SwitchYamlFail}], onError: [{exception: System.InvalidOperationException, steps: [{kind: switch, value: {kind: constant, value: true}, cases: [{value: {kind: constant, value: true}, steps: [{kind: activity, activity: " +
            Mark +
            ", input: [{target: Text, from: {kind: constant, value: selected}}], output: [{source: Text, to: {path: workflow.Text}}]}]}], default: []}], compensate: none, then: continue}]}";

        var fluent = Workflow.Create<SwitchYamlData>("SwitchReview").Step<SwitchYamlSeed>().Saga(s => s.Step<SwitchYamlFail>())
            .OnError<InvalidOperationException>(e => e
                .Switch(ctx => true, c => c
                    .Case(true,
                        b => b.Step<SwitchYamlMark>(a =>
                            a.Input(x => x.Text).From(ctx => "selected").Output(x => x.Text).To(ctx => ctx.WorkflowData.Text))).Default(_ =>
                    {
                    }))
                .CompensateNone().ThenContinue()).Build();

        var generated = GeneratorRegressionTests.Build(yaml);
        Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(generated));

        foreach (var definition in new[] { fluent, generated })
        {
            await using var services = new ServiceCollection().AddLogging().AddIxIFlow().Services.BuildServiceProvider();
            var run = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new SwitchYamlData());
            Assert.Equal(WorkflowExecutionStatus.Success, run.Status);
            Assert.Equal("selected", Assert.IsType<SwitchYamlData>(run.WorkflowData).Text);
        }

        var source =
            "using IxIFlow.Builders; using IxIFlow.Generators.Tests; public class Author { public void Build() { Workflow.Create<SwitchYamlData>(\"Policy\").Step<SwitchYamlSeed>().Saga(s => s.Step<SwitchYamlFail>()).OnError(e => e.Switch(ctx => true, c => c.Case(true, b => b.CompensateNone().ThenContinue()))); } }";

        Assert.Contains(GeneratorRegressionTests.Compilation(source).GetDiagnostics(),
            d => d.Id == "CS1061" && d.GetMessage().Contains("CompensateNone"));
    }

    [Theory]
    [InlineData("if (flag) alias = new();", true)]
    [InlineData("if (flag) alias = new(); else alias = new();", false)]
    [InlineData("switch (flag) { case true: alias = new(); break; }", true)]
    [InlineData("while (flag) { alias = new(); break; }", true)]
    [InlineData("try { alias = new(); } catch { }", true)]
    public async Task SwitchMutationFreshnessMergesAcrossPaths(string branching, bool mutation)
    {
        var source =
            "using System.Collections.Generic; using IxIFlow.Builders; using IxIFlow.Generators.Tests; public class Author { public void Build() { Workflow.Create<SwitchYamlData>(\"Alias\").Switch(ctx => Pick(ctx.WorkflowData.Items, ctx.WorkflowData.Route > 0), c => {}); } private static int Pick(List<int> items, bool flag) { var alias = items; " +
            branching + " alias.Clear(); return 1; } }";

        var compilation = GeneratorRegressionTests.Compilation(source);
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

        var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new PurityAnalyzer()))
            .GetAnalyzerDiagnosticsAsync();

        Assert.Equal(mutation, diagnostics.Any(d => d.GetMessage().Contains("List.Clear (state mutation)")));
    }

    [Theory]
    [InlineData("var alias = new List<int>(); try { alias = items; throw new System.Exception(); } catch { alias.Clear(); }", true)]
    [InlineData("var alias = items; try { alias = new(); throw new System.Exception(); } catch { alias = new(); alias.Clear(); }", false)]
    [InlineData("var alias = new List<int>(); try { alias = items; throw new System.Exception(); } catch when (Clear(alias)) { }", true)]
    [InlineData("var alias = items; try { throw new System.Exception(); } catch when (Clear(alias = new())) { alias.Clear(); }", false)]
    [InlineData(
        "var alias = new List<int>(); try { alias = items; throw new System.Exception(); } catch when (flag) { alias = new(); } catch { alias.Clear(); }",
        true)]
    [InlineData(
        "var replacement = new List<int>(); var alias = new List<int>(); try { alias = items; throw new System.Exception(); } catch when (Clear(alias = replacement) && flag) { } catch { alias.Clear(); }",
        false)]
    [InlineData("var alias = items; try { throw new System.Exception(); } catch when (Clear(alias = new())) { } catch { alias.Clear(); }",
        true)]
    [InlineData("var alias = items; var ignored = flag switch { false => alias = items, _ => alias = new() }; alias.Clear();", true)]
    [InlineData("var alias = items; var ignored = flag switch { false => alias = new(), _ => alias = new() }; alias.Clear();", false)]
    [InlineData("var alias = new List<int>(); var ignored = flag switch { false when Clear(alias = items) => 0, _ => 1 }; alias.Clear();",
        true)]
    [InlineData("var alias = items; var ignored = flag switch { false when Clear(alias = new()) => 0, _ => 1 }; alias.Clear();", true)]
    [InlineData("var alias = items; switch (flag) { case true: alias = new(); break; default: alias = new(); break; } alias.Clear();",
        false)]
    [InlineData("var alias = items; switch (flag) { case true: alias = new(); break; case false: alias = new(); break; } alias.Clear();",
        false)]
    [InlineData("var alias = items; switch (flag) { case true: alias = new(); break; } alias.Clear();", true)]
    [InlineData("var alias = items; try { alias = new(); } catch { alias = new(); } alias.Clear();", false)]
    [InlineData("var alias = items; try { alias = new(); } catch { } alias.Clear();", true)]
    [InlineData("var alias = items; try { if (flag) return 0; alias = new(); } catch { alias = new(); } alias.Clear();", false)]
    [InlineData("var alias = items; while (flag) { if (Clear(alias = items)) break; alias = new(); } alias.Clear();", true)]
    [InlineData("var alias = new List<int>(); do { if (flag) { alias = items; break; } alias = new(); } while (flag); alias.Clear();",
        true)]
    [InlineData("var alias = items; do { if (flag) { alias = new(); break; } alias = new(); } while (flag); alias.Clear();", false)]
    [InlineData("var alias = new List<int>(); do { alias.Clear(); if (flag) { alias = items; continue; } alias = new(); } while (flag);",
        true)]
    [InlineData("var alias = items; do { alias = new(); alias.Clear(); if (flag) continue; alias = new(); } while (flag);", false)]
    [InlineData("var alias = items; if (flag) { return 0; } else { alias = new(); } alias.Clear();", false)]
    [InlineData(
        "var alias = items; switch (flag) { case true: throw new System.Exception(); default: alias = new(); break; } alias.Clear();",
        false)]
    [InlineData("var alias = items; try { if (flag) return 0; } finally { alias.Clear(); }", true)]
    [InlineData("var alias = items; try { if (flag) return 0; } finally { alias = new(); alias.Clear(); }", false)]
    [InlineData(
        "var alias = new List<int>(); try { try { alias = items; throw new System.Exception(); } finally { alias = new(); } } catch { alias.Clear(); }",
        true)]
    [InlineData(
        "var alias = new List<int>(); try { try { alias = items; throw new System.Exception(); } finally { alias = new(); } } catch { alias = new(); alias.Clear(); }",
        false)]
    [InlineData("var alias = items; do { try { break; } finally { alias = new(); } } while (flag); alias.Clear();", false)]
    [InlineData("var alias = new List<int>(); do { try { alias = items; continue; } finally { alias.Clear(); } } while (flag);", true)]
    [InlineData(
        "var replacement = new List<int>(); var alias = new List<int>(); try { try { alias = items; throw new System.Exception(); } finally { alias = replacement; } } catch when (Clear(alias)) { }",
        true)]
    [InlineData(
        "var replacement = new List<int>(); var alias = items; try { try { throw new System.Exception(); } finally { alias = replacement; } } catch { alias.Clear(); }",
        false)]
    [InlineData(
        "var replacement = new List<int>(); var alias = items; try { try { throw new System.Exception(); } finally { alias = replacement; } } catch when (flag) { alias.Clear(); }",
        false)]
    [InlineData(
        "var replacement = new List<int>(); var alias = new List<int>(); try { try { throw new System.Exception(); } finally { alias = replacement; } } catch when (Clear(alias = items)) { alias.Clear(); }",
        true)]
    [InlineData(
        "var replacement = new List<int>(); var alias = new List<int>(); try { try { throw new System.Exception(); } finally { alias = replacement; } } catch when ((alias = items) != null) { alias.Clear(); }",
        false)]
    [InlineData(
        "var replacement = new List<int>(); var alias = items; try { try { try { throw new System.Exception(); } finally { alias = replacement; throw new System.Exception(); } } finally { alias = items; } } catch when (Clear(alias)) { }",
        true)]
    [InlineData("var original = items; items = new(); items = original; items.Clear();", true)]
    [InlineData("items = new(); items.Clear();", false)]
    [InlineData(
        "var replacement = new List<int>(); var alias = items; try { var array = new[] { alias = replacement }; } catch { alias.Clear(); }",
        true)]
    [InlineData(
        "var replacement = new List<int>(); var alias = items; try { var array = new[] { alias = replacement }; } catch { alias = replacement; alias.Clear(); }",
        false)]
    public async Task SwitchMutationFlowTracksReachableNormalAndExceptionalExits(string body, bool mutation)
    {
        var source =
            "using System.Collections.Generic; using IxIFlow.Builders; using IxIFlow.Generators.Tests; public class Author { public void Build() { Workflow.Create<SwitchYamlData>(\"Flow\").Switch(ctx => Pick(ctx.WorkflowData.Items, ctx.WorkflowData.Route > 0), c => {}); } private static bool Clear(List<int> value) { value.Clear(); return true; } private static int Pick(List<int> items, bool flag) { " +
            body + " return 1; } }";

        var compilation = GeneratorRegressionTests.Compilation(source);
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

        var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new PurityAnalyzer()))
            .GetAnalyzerDiagnosticsAsync();

        Assert.DoesNotContain(diagnostics, d => d.Id == "AD0001");

        Assert.True(mutation == diagnostics.Any(d => d.Id == "IXIF0101" && d.GetMessage().Contains("state mutation")),
            body + "\n" + string.Join("\n", diagnostics));
    }

    [Theory]
    [InlineData("new ValueWrapper { State = { Flag = true } }.State.Flag", false)]
    [InlineData("new ValueWrapper { State = { Nested = { Flag = true } } }.State.Nested.Flag", false)]
    [InlineData("new ReferenceWrapper { State = { Flag = true } }.State.Flag", true)]
    [InlineData("new GetterWrapper { State = { Flag = true } }.State.Flag", true)]
    public async Task SwitchNestedInitializerFreshnessMatchesRuntime(string expression, bool mutation)
    {
        var source =
            "using IxIFlow.Builders; using IxIFlow.Generators.Tests; public class Author { public void Build() { Workflow.Create<SwitchYamlData>(\"Nested\").Switch(ctx => " +
            expression +
            ", c => {}); } } public struct NestedFlag { public bool Flag; } public struct State { public bool Flag; public NestedFlag Nested; } public class ValueWrapper { public State State; } public class ReferenceWrapper { public ReferenceState State = new(); } public class ReferenceState { public bool Flag; } public class GetterWrapper { public ReferenceState State => new(); }";

        var compilation = GeneratorRegressionTests.Compilation(source);
        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

        var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new PurityAnalyzer()))
            .GetAnalyzerDiagnosticsAsync();

        Assert.DoesNotContain(diagnostics, d => d.Id == "AD0001");
        Assert.Equal(mutation, diagnostics.Any(d => d.Id == "IXIF0101"));
    }

    private static async Task RecoverWait(WorkflowDefinition definition)
    {
        var repository = new InMemoryWorkflowStateRepository();
        var registry = new WorkflowCatalog();
        await registry.RegisterWorkflowAsync(definition);

        ServiceProvider Services() => new ServiceCollection().AddLogging().AddIxIFlow(o => o.PayloadThresholdBytes = 1).Services
            .AddSingleton<IWorkflowStateRepository>(repository).AddSingleton<IWorkflowCatalog>(registry).BuildServiceProvider();

        string id;

        await using (var first = Services())
        {
            var run = await first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new SwitchYamlData());
            Assert.Equal(WorkflowExecutionStatus.Suspended, run.Status);
            id = run.InstanceId;
        }

        await using var second = Services();
        var result = await second.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "go", new SwitchReviewGo { Text = "fault" });
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("fault", Assert.IsType<SwitchYamlData>(result.WorkflowData).Text);
    }
}

public sealed class SwitchReviewGo
{
    public string Text { get; set; } = "";
}

public sealed class SwitchResultData
{
    public List<int> Items { get; set; } = [1];
    public List<ItemResult<string>> Results { get; set; } = [];
}