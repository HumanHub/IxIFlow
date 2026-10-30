using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Dsl.Documents;
using IxIFlow.Extensions;
using Json.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Generators.Tests;

[Trait("Feature", "authoring")]
[Trait("Spec", "W-05")]
public sealed class ThrowYamlTests
{
    private const string Header = "workflow: {name: ThrowYaml, dataType: IxIFlow.Generators.Tests.ThrowYamlData}\ndefinitions:\n";
    private const string ErrorType = "IxIFlow.Generators.Tests.ThrowYamlError";
    private const string FaultType = "IxIFlow.Generators.Tests.ThrowYamlFault";
    private const string Mark = "IxIFlow.Generators.Tests.ThrowYamlEcho";

    private const string Factory =
        "code: {raise: {language: csharp-expression, source: 'new IxIFlow.Generators.Tests.ThrowYamlError(\"declined\") { Code = workflow.Code }'}}\n";

    private const string Raised = "{kind: throw, id: raise, name: Decline, exception: " + ErrorType +
        ", value: {kind: function, function: raise}}";

    private const string ReadFault = "{kind: activity, activity: " + Mark +
        ", input: [{target: Value, from: {path: fault.Code}}], output: [{source: Value, to: {path: workflow.Result}}]}";

    [Fact]
    public async Task ThrowInitializerAndLexicalRethrowMatchFluentThroughBothReaders()
    {
        var yaml = Header + "- kind: try\n  steps:\n    - kind: try\n      steps: [" + Raised + "]\n      catches:\n        - exception: " +
            ErrorType + "\n          faultType: " + FaultType +
            "\n          steps: [{kind: rethrow, id: again}]\n  catches:\n    - exception: " + ErrorType + "\n      faultType: " +
            FaultType + "\n      steps: [" + ReadFault + "]\n" + Factory;

        var fluent = new WorkflowBuilder<ThrowYamlData, object>([], "ThrowYaml").Try(t => t
                .Try(b => b.Throw<ThrowYamlError>(ctx => new("declined") { Code = ctx.WorkflowData.Code }).WithId("raise").Named("Decline"))
                .Catch<ThrowYamlError, ThrowYamlFault>(c => c.Rethrow().WithId("again")))
            .Catch<ThrowYamlError, ThrowYamlFault>(c => c.Step<ThrowYamlEcho>(s => s.Input(x => x.Value).From(ctx => ctx.Fault.Code)
                .Output(x => x.Value).To(ctx => ctx.WorkflowData.Result))).Build();

        foreach (var source in Readers(yaml))
        {
            var generated = GeneratorRegressionTests.Build(source);
            Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(generated));

            Assert.Equal(fluent.StepTable.Where(e => e.Kind == WorkflowStepType.Rethrow).Select(e => e.RethrowOwnerId),
                generated.StepTable.Where(e => e.Kind == WorkflowStepType.Rethrow).Select(e => e.RethrowOwnerId));

            foreach (var definition in new[] { fluent, generated })
            {
                await using var services = new ServiceCollection().AddLogging().AddIxIFlow().Services.BuildServiceProvider();
                var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new ThrowYamlData { Code = 7 });
                Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
                Assert.Equal(7, Assert.IsType<ThrowYamlData>(result.WorkflowData).Result);
            }
        }

        var schema = JsonSchema.FromText(DocumentSchema.Write(typeof(WorkflowDocument)),
            new BuildOptions { SchemaRegistry = new SchemaRegistry() });

        Assert.True(schema
            .Evaluate(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(
                WorkflowJsonSerializer.Write(WorkflowYamlSerializer.Read(yaml)))).IsValid);
    }

    [Theory]
    [InlineData("{kind: rethrow}", "RETHROW001")]
    [InlineData("{kind: rethrow, value: {kind: constant, value: 1}}", "value")]
    [InlineData("{kind: throw, value: {kind: constant, value: null}}", "THROW001")]
    [InlineData("{kind: throw, exception: System.Exception}", "THROW002")]
    public void ThrowYamlDiagnosticsPointToTheAuthoredMember(string step, string message)
    {
        var error = Assert.Throws<WorkflowSourceException>(() =>
            WorkflowSourceTranslator.Translate(Header + "- " + step, "throw.workflow.yaml"));

        Assert.Contains(message, error.Message);
        Assert.Contains("throw.workflow.yaml", error.Message);
    }

    [Fact]
    public void ThrowFactoriesUseLexicalFaultRootsAndRethrowIsRejectedInSagaHandler()
    {
        var yaml = Header + "- kind: try\n  steps: [" + Raised + "]\n  catches:\n    - exception: " + ErrorType + "\n      faultType: " +
            FaultType +
            "\n      steps: [{kind: switch, value: {path: fault.Code}, cases: [{value: {kind: constant, value: 7}, steps: [{kind: throw, exception: " +
            ErrorType + ", value: {kind: function, function: nested}}]}]}]\n" + Factory +
            "  nested: {language: csharp-expression, source: 'new IxIFlow.Generators.Tests.ThrowYamlError(\"nested\") { Code = fault.Code + 1 }'}\n";

        // Use the code map form because both factories are referenced from nested lexical contexts.
        yaml = yaml.Replace(Factory,
            "code:\n  raise: {language: csharp-expression, source: 'new IxIFlow.Generators.Tests.ThrowYamlError(\"declined\") { Code = workflow.Code }'}\n");

        foreach (var source in Readers(yaml))
            Assert.NotNull(GeneratorRegressionTests.Build(source));

        var saga = Header + "- kind: saga\n  steps: [" + Raised.Replace(", name: Decline", "") + "]\n  onError: [{exception: " + ErrorType +
            ", steps: [{kind: rethrow}]}]\n" + Factory;

        Assert.Contains("RETHROW001",
            Assert.Throws<WorkflowSourceException>(() => WorkflowSourceTranslator.Translate(saga, "saga.yaml")).Message);
    }

    [Fact]
    public async Task ThrowPublicSampleFluentAndYamlHaveTheSameContractAndResult()
    {
        var fluent = IxIFlow.Samples.ThrowAndRethrow.Definition();
        var generated = IxIFlow.Samples.Generated.Workflow_throw_and_rethrow_workflow.Build();
        Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(generated));

        foreach (var definition in new[] { fluent, generated })
        {
            await using var services = new ServiceCollection().AddLogging().AddIxIFlow().Services.BuildServiceProvider();
            var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new IxIFlow.Samples.DeclineData());
            Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
            Assert.Equal(402, Assert.IsType<IxIFlow.Samples.DeclineData>(result.WorkflowData).RecordedCode);
        }
    }

    private static IEnumerable<string> Readers(string yaml)
    {
        yield return yaml;

        var document = WorkflowYamlSerializer.Read(yaml);

        yield return WorkflowYamlSerializer.Write(document);
        yield return WorkflowYamlSerializer.Write(WorkflowJsonSerializer.Read(WorkflowJsonSerializer.Write(document)));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [Trait("Spec", "D-18")]
    public async Task ThrowInitializerReachesInvokingParentAfterRestartAndExhaustedPolicies(bool yaml, int retries)
    {
        var policy = retries == 0
            ? ""
            : "\n          onError: [{exception: " + ErrorType +
            ", then: retry, retry: {maximumAttempts: 1, initialInterval: '00:00:00', maximumInterval: '00:00:00', backoffCoefficient: 1}}]";

        var source = Header +
            "- kind: try\n  steps:\n    - kind: try\n      steps:\n        - kind: invokeWorkflowType\n          workflowType: IxIFlow.Generators.Tests.ThrowYamlChild\n          dataType: IxIFlow.Generators.Tests.ThrowYamlData" +
            policy +
            "\n      catches:\n        - exception: " + ErrorType + "\n          faultType: " + FaultType +
            "\n          steps: [{kind: waitFor, event: IxIFlow.Generators.Tests.ThrowYamlGo, key: caught}, {kind: rethrow}]\n  catches:\n    - exception: " +
            ErrorType + "\n      faultType: " + FaultType + "\n      steps: [" + ReadFault + "]\n";

        var generated = GeneratorRegressionTests.Build(source);

        var fluent = new WorkflowBuilder<ThrowYamlData, object>([], "ThrowYaml").Try(o => o.Try(t => t
                    .Invoke<ThrowYamlChild, ThrowYamlData>(s =>
                    {
                        if (retries > 0)
                            s.OnError<ThrowYamlError>(e => e.ThenRetry(new RetryPolicy(TimeSpan.Zero, TimeSpan.Zero, 1, 1)));
                    }))
                .Catch<ThrowYamlError, ThrowYamlFault>(c => c.WaitFor<ThrowYamlGo>("caught").Rethrow()))
            .Catch<ThrowYamlError, ThrowYamlFault>(c => c.Step<ThrowYamlEcho>(s => s.Input(x => x.Value).From(ctx => ctx.Fault.Code)
                .Output(x => x.Value).To(ctx => ctx.WorkflowData.Result))).Build();

        Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(generated));
        var definition = yaml ? generated : fluent;
        var store = new InMemoryWorkflowStateRepository();
        var catalog = new WorkflowCatalog();

        await catalog.RegisterWorkflowAsync(
            ServiceCollectionExtensions.BuildWorkflowDefinition<ThrowYamlChild, ThrowYamlData>(new ThrowYamlChild()));

        await catalog.RegisterWorkflowAsync(definition);
        string id;

        ServiceProvider Open() => new ServiceCollection().AddLogging().AddIxIFlow().Services
            .AddSingleton<IWorkflowStateRepository>(store).AddSingleton<IWorkflowCatalog>(catalog)
            .RegisterWorkflow<ThrowYamlChild>().BuildServiceProvider();

        await using (var first = Open())
        {
            var result = await first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new ThrowYamlData());
            Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
            id = result.InstanceId;
            await ChildWaiting(first.GetRequiredService<IWorkflowClient>(), 1);
        }

        await using (var second = Open())
        {
            var client = second.GetRequiredService<IWorkflowClient>();

            for (var attempt = 1; attempt <= retries + 1; attempt++)
            {
                var child = await ChildWaiting(client, attempt);
                Assert.Equal(WorkflowExecutionStatus.Faulted, (await client.ResumeAsync(child, "raise", new ThrowYamlGo())).Status);
            }

            await Until(async () => (await client.GetOpenWaitsAsync(new(InstanceId: id))).Any(w => w.Key == "caught"));
        }

        await using var third = Open();
        var finished = await third.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "caught", new ThrowYamlGo());
        Assert.Equal(WorkflowExecutionStatus.Success, finished.Status);
        Assert.Equal(73, Assert.IsType<ThrowYamlData>(finished.WorkflowData).Result);
        var checkpoint = ExecutionCheckpoint.Read(StoredCheckpoint.WholeState((await store.GetWorkflowInstanceAsync(id))!));
        Assert.Empty(checkpoint.InvocationRecords);
        var storedTrace = await store.GetTraceAsync(id);
        var replay = TraceReplay.Load(definition, storedTrace);

        while (replay.StepForward())
        {
        }

        var attempts = replay.Current.Invocations.OrderBy(attempt => attempt.AttemptNumber).ToArray();
        Assert.Equal(retries + 1, attempts.Length);
        Assert.Equal(retries == 0 ? "Propagate" : "Exhausted", attempts[^1].Disposition);
        var trace = await third.GetRequiredService<IWorkflowClient>().GetTraceAsync(id);
        Assert.Equal(retries + 1, trace.Count(e => e.EntryType == TraceEntryType.InvocationPolicyApplied));

        async Task<string> ChildWaiting(IWorkflowClient client, int attempt)
        {
            string? child = null;

            await Until(async () =>
            {
                var rows = (await store.QueryInstancesAsync(new InstanceQuery(ParentInstanceId: id))).Items;

                if (rows.Count != attempt)
                    return false;

                child = rows.SingleOrDefault(r => r.Status == WorkflowStatus.Suspended)?.InstanceId;

                if (child == null)
                    return false;

                return (await client.GetOpenWaitsAsync(new(InstanceId: child))).Any(w => w.Key == "raise");
            });

            return child!;
        }
    }

    private static async Task Until(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        while (!await condition())
            await Task.Delay(10, timeout.Token);
    }
}

public sealed class ThrowYamlData
{
    public int Code { get; set; }
    public int Result { get; set; }
}

public sealed class ThrowYamlError(string message) : Exception(message)
{
    public int Code { get; init; }
}

public sealed class ThrowYamlFault
{
    public int Code { get; set; }
}

public sealed class ThrowYamlEcho : IAsyncActivity
{
    [ActivityInputOutput] public int Value { get; set; }
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class ThrowYamlGo;

[Workflow("ThrowYamlChild", Version = 1)]
public sealed class ThrowYamlChild : IxIFlow.Builders.Interfaces.IWorkflow<ThrowYamlData>
{
    public void Build(IxIFlow.Builders.Interfaces.IWorkflowBuilder<ThrowYamlData> builder) => builder
        .Step<ThrowYamlEcho>().WaitFor<ThrowYamlGo>("raise").Throw<ThrowYamlError>(ctx => new("declined") { Code = 73 });
}