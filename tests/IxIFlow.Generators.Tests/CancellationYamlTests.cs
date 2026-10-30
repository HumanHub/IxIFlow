using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Dsl.Documents;
using IxIFlow.Extensions;
using Json.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Generators.Tests;

[Trait("Feature", "authoring"), Trait("Spec", "W-06")]
public sealed class CancellationYamlTests
{
    private const string Header = "workflow: {name: CancellationYaml, dataType: IxIFlow.Generators.Tests.ThrowYamlData}\ndefinitions:\n";

    private const string Raised =
        "{kind: throw, exception: IxIFlow.Generators.Tests.ThrowYamlError, value: {kind: function, function: raise}}";

    private const string Factory =
        "code: {raise: {language: csharp-expression, source: 'new IxIFlow.Generators.Tests.ThrowYamlError(\"original\") { Code = 73 }'}}\n";

    [Theory, InlineData(false), InlineData(true)]
    public async Task CancellationHandlerMatchesFluentAcrossReadersSchemaAndRestart(bool empty)
    {
        var yaml = Header + "- kind: try\n  steps:\n    - kind: cancellationScope\n      id: wrapper\n      steps: [" + Raised +
            "]\n      onCancelled: " +
            (empty
                ? "[]"
                : "[{kind: assign, to: {path: workflow.Code}, value: {kind: constant, value: 4}}, {kind: waitFor, event: IxIFlow.Generators.Tests.ThrowYamlGo, key: cleanup}]") +
            "\n  catches:\n    - exception: IxIFlow.Generators.Tests.ThrowYamlError\n      faultType: IxIFlow.Generators.Tests.ThrowYamlFault\n      steps: [{kind: assign, to: {path: workflow.Result}, value: {path: fault.Code}}]\n" +
            Factory;

        var fluent = new WorkflowBuilder<ThrowYamlData, object>([], "CancellationYaml").Try(t => t.CancellationScope(
                b => b.Throw<ThrowYamlError>(c => new("original") { Code = 73 }), h =>
                {
                    if (!empty)
                        h.Assign(c => c.WorkflowData.Code, c => 4).WaitFor<ThrowYamlGo>("cleanup");
                }).WithId("wrapper"))
            .Catch<ThrowYamlError, ThrowYamlFault>(c => c.Assign(x => x.WorkflowData.Result, x => x.Fault.Code)).Build();

        var document = WorkflowYamlSerializer.Read(yaml);

        foreach (var text in new[] { yaml, WorkflowYamlSerializer.Write(document), WorkflowJsonSerializer.Write(document) })
        {
            var generated = GeneratorRegressionTests.Build(text);
            Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(generated));

            Assert.Equal(fluent.StepTable.Select(s => (s.CatalogId, s.Kind, s.CancellationScope)),
                generated.StepTable.Select(s => (s.CatalogId, s.Kind, s.CancellationScope)));

            Assert.All(generated.StepTable, step => Assert.NotNull(step.SourceLocation));

            foreach (var definition in new[] { generated, fluent })
            {
                var store = new InMemoryWorkflowStateRepository();
                var catalog = new WorkflowCatalog();
                await catalog.RegisterWorkflowAsync(definition);
                string id;

                ServiceProvider Open() => new ServiceCollection().AddIxIFlow(o =>
                    {
                        o.VerifyCheckpointParts = true;
                        o.PayloadThresholdBytes = 1;
                    }).Services
                    .AddSingleton<IWorkflowStateRepository>(store).AddSingleton<IWorkflowCatalog>(catalog).BuildServiceProvider();

                await using (var first = Open())
                {
                    var result = await first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new ThrowYamlData());
                    Assert.Equal(empty ? WorkflowExecutionStatus.Success : WorkflowExecutionStatus.Suspended, result.Status);
                    id = result.InstanceId;

                    if (empty)
                        Assert.Equal(73, Assert.IsType<ThrowYamlData>(result.WorkflowData).Result);
                }

                if (!empty)
                {
                    await using var second = Open();
                    var result = await second.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "cleanup", new ThrowYamlGo());
                    Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
                    Assert.Equal(73, Assert.IsType<ThrowYamlData>(result.WorkflowData).Result);
                    Assert.Equal(4, Assert.IsType<ThrowYamlData>(result.WorkflowData).Code);
                    var trace = await second.GetRequiredService<IWorkflowClient>().GetTraceAsync(id);
                    Assert.Single(trace, e => e.EntryType == TraceEntryType.CancellationHandlerEntered);
                    Assert.Single(trace, e => e.EntryType == TraceEntryType.CancellationHandlerCompleted);
                }
            }
        }

        var schema = JsonSchema.FromText(DocumentSchema.Write(typeof(WorkflowDocument)),
            new BuildOptions { SchemaRegistry = new SchemaRegistry() });

        Assert.True(schema
            .Evaluate(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(WorkflowJsonSerializer.Write(document)))
            .IsValid);
    }

    [Theory]
    [InlineData("{kind: cancellationScope, steps: []}", "CANCEL001", "onCancelled")]
    [InlineData("{kind: cancellationScope, onCancelled: []}", "CANCEL001", "steps")]
    [InlineData("{kind: cancellationScope, steps: [], onCancelled: null}", "CANCEL001", "onCancelled")]
    [InlineData("{kind: cancellationScope, steps: [], onCancelled: [], unknown: true}", "unknown", "unknown")]
    public void MissingInvalidAndUnknownMembersHavePositionedDiagnostics(string step, string code, string member)
    {
        var error = Assert.Throws<WorkflowSourceException>(() =>
            WorkflowSourceTranslator.Translate(Header + "- " + step, "cancel.workflow.yaml"));

        Assert.Contains(code, error.Message);
        Assert.Contains(member, error.Message);
        Assert.Equal(3, error.Line);
        Assert.True(error.Column > 1);
    }

    [Fact]
    public void BothCancellationCallbacksRetainLexicalFaultAndRethrowBinding()
    {
        var yaml = Header + "- kind: try\n  steps: [" + Raised +
            "]\n  catches:\n    - exception: IxIFlow.Generators.Tests.ThrowYamlError\n      faultType: IxIFlow.Generators.Tests.ThrowYamlFault\n      steps:\n        - kind: cancellationScope\n          steps: [{kind: assign, to: {path: workflow.Code}, value: {path: fault.Code}}]\n          onCancelled: [{kind: assign, to: {path: workflow.Result}, value: {path: fault.Code}}, {kind: rethrow}]\n" +
            Factory;

        var generated = GeneratorRegressionTests.Build(yaml);

        var fluent = new WorkflowBuilder<ThrowYamlData, object>([], "CancellationYaml")
            .Try(t => t.Throw<ThrowYamlError>(c => new("original") { Code = 73 }))
            .Catch<ThrowYamlError, ThrowYamlFault>(c => c.CancellationScope(b => b.Assign(x => x.WorkflowData.Code, x => x.Fault.Code),
                h => h.Assign(x => x.WorkflowData.Result, x => x.Fault.Code).Rethrow())).Build();

        Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(generated));
    }

    [Theory, InlineData("cancel"), InlineData("failure"), InlineData("retry")]
    public async Task SagaBodyKeepsItsCompensableFacetAndHandlerUsesOrdinaryCleanup(string cause)
    {
        var tail = cause == "cancel"
            ? "{kind: waitFor, event: IxIFlow.Generators.Tests.ThrowYamlGo, key: body}"
            : "{kind: activity, activity: IxIFlow.Generators.Tests.CancellationSagaFlaky}";

        var policy = cause == "retry"
            ? "then: retry, retry: {maximumAttempts: 1, initialInterval: '00:00:00', maximumInterval: '00:00:00', backoffCoefficient: 1}"
            : "then: continue";

        var yaml = Header + $$$$"""
                                - kind: saga
                                  steps:
                                    - kind: cancellationScope
                                      steps:
                                        - kind: activity
                                          activity: IxIFlow.Generators.Tests.CancellationSagaMark
                                          input: [{target: Text, from: {kind: constant, value: A}}]
                                          compensation: [{activity: IxIFlow.Generators.Tests.CancellationSagaMark, input: [{target: Text, from: {kind: constant, value: undo-A}}]}]
                                        - kind: activity
                                          activity: IxIFlow.Generators.Tests.CancellationSagaMark
                                          input: [{target: Text, from: {kind: constant, value: B}}]
                                          compensation: [{activity: IxIFlow.Generators.Tests.CancellationSagaMark, input: [{target: Text, from: {kind: constant, value: undo-B}}]}]
                                        - kind: activity
                                          activity: IxIFlow.Generators.Tests.CancellationSagaMark
                                          input: [{target: Text, from: {kind: constant, value: C}}]
                                          compensation: [{activity: IxIFlow.Generators.Tests.CancellationSagaMark, input: [{target: Text, from: {kind: constant, value: undo-C}}]}]
                                        - {{{{tail}}}}
                                      onCancelled:
                                        - {kind: activity, activity: IxIFlow.Generators.Tests.CancellationSagaMark, input: [{target: Text, from: {kind: constant, value: H}}]}
                                        - {kind: waitFor, event: IxIFlow.Generators.Tests.ThrowYamlGo, key: cleanup}
                                  onError: [{exception: IxIFlow.Generators.Tests.ThrowYamlError, compensate: all, {{{{policy}}}}}]
                                """;

        var generated = GeneratorRegressionTests.Build(yaml);

        var fluent = new WorkflowBuilder<ThrowYamlData, object>([], "CancellationYaml").Saga(s => s.CancellationScope(b =>
            {
                var steps = b.Step<CancellationSagaMark>(a => a.Input(x => x.Text).From(c => "A")
                        .CompensateWith<CancellationSagaMark>(u => u.Input(x => x.Text).From(c => "undo-A")))
                    .Step<CancellationSagaMark>(a => a.Input(x => x.Text).From(c => "B")
                        .CompensateWith<CancellationSagaMark>(u => u.Input(x => x.Text).From(c => "undo-B")))
                    .Step<CancellationSagaMark>(a => a.Input(x => x.Text).From(c => "C")
                        .CompensateWith<CancellationSagaMark>(u => u.Input(x => x.Text).From(c => "undo-C")));

                if (cause == "cancel")
                    steps.WaitFor<ThrowYamlGo>("body");
                else
                    steps.Step<CancellationSagaFlaky>();
            }, h => h.Step<CancellationSagaMark>(a => a.Input(x => x.Text).From(c => "H")).WaitFor<ThrowYamlGo>("cleanup")))
            .OnError<ThrowYamlError>(e =>
            {
                var undo = e.Compensate();

                if (cause == "retry")
                    undo.ThenRetry(new RetryPolicy(TimeSpan.Zero, TimeSpan.Zero, 1, 1));
                else
                    undo.ThenContinue();
            }).Build();

        Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(generated));

        foreach (var definition in new[] { fluent, generated })
        {
            var store = new InMemoryWorkflowStateRepository();
            var catalog = new WorkflowCatalog();
            var effects = new CancellationSagaEffects();
            await catalog.RegisterWorkflowAsync(definition);

            ServiceProvider Open() => new ServiceCollection().AddIxIFlow(o => o.VerifyCheckpointParts = true).Services
                .AddSingleton<IWorkflowStateRepository>(store).AddSingleton<IWorkflowCatalog>(catalog).AddSingleton(effects)
                .BuildServiceProvider();

            string id;

            await using (var first = Open())
            {
                var client = first.GetRequiredService<IWorkflowClient>();
                var idle = await client.RunAsync(definition, new ThrowYamlData());
                id = idle.InstanceId;

                if (cause == "cancel")
                    idle = await client.CancelAsync(id, new() { ReasonCode = "test" });

                Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
                Assert.Equal(new[] { "A", "B", "C", "H" }, effects.Events);

                var saved = IxIFlow.Core.Runtime.ExecutionCheckpoint.Read(
                    IxIFlow.Core.Runtime.StoredCheckpoint.WholeState((await store.GetWorkflowInstanceAsync(id))!));

                var saga = Assert.Single(saved.Continuations[0].Stack, f => f.SagaState != null).SagaState!;
                Assert.Equal(3, saga.CompletedSteps.Count);

                var handlerIds = definition.StepTable.Where(s => s.CatalogId.Contains("/cancelled/", StringComparison.Ordinal))
                    .Select(s => s.CatalogId).ToHashSet();

                Assert.DoesNotContain(saga.CompletedSteps, s => handlerIds.Contains(s.StepId));
            }

            await using var second = Open();
            var done = await second.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "cleanup", new ThrowYamlGo());
            Assert.Equal(cause == "cancel" ? WorkflowExecutionStatus.Cancelled : WorkflowExecutionStatus.Success, done.Status);

            Assert.Equal(cause == "retry"
                ? ["A", "B", "C", "H", "undo-C", "undo-B", "undo-A", "A", "B", "C"]
                : new[] { "A", "B", "C", "H", "undo-C", "undo-B", "undo-A" }, effects.Events);

            Assert.Equal(cause == "retry" ? 2 : cause == "failure" ? 1 : 0, effects.Calls);

            Assert.Single(await second.GetRequiredService<IWorkflowClient>().GetTraceAsync(id),
                e => e.EntryType == TraceEntryType.CancellationHandlerEntered);
        }
    }
}

public sealed class CancellationSagaEffects
{
    public List<string> Events { get; } = [];
    public int Calls;
}

public sealed class CancellationSagaMark(CancellationSagaEffects effects) : IAsyncActivity
{
    public string Text { get; set; } = "";

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        effects.Events.Add(Text);

        return Task.CompletedTask;
    }
}

public sealed class CancellationSagaFlaky(CancellationSagaEffects effects) : IAsyncActivity
{
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        if (++effects.Calls == 1)
            throw new ThrowYamlError("original") { Code = 73 };

        return Task.CompletedTask;
    }
}