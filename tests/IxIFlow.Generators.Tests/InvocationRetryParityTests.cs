using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Runtime.Loader;
using IxIFlow.Analyzers;
using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Dsl.Documents;
using IxIFlow.Extensions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Generators.Tests;

[Trait("Spec", "D-18")]
public sealed class InvocationRetryParityTests
{
    private const string Header = "workflow: {name: InvocationParity, dataType: IxIFlow.Generators.Tests.RetryParityData}\ndefinitions:\n";

    [Theory]
    [InlineData(false, "retry")]
    [InlineData(true, "retry")]
    [InlineData(false, "ignore")]
    [InlineData(true, "ignore")]
    [InlineData(false, "terminate")]
    [InlineData(true, "terminate")]
    public async Task BothInvocationFormsExecuteTheSameOrderedPoliciesAndRetainTheSameAttemptFacts(bool typed, string action)
    {
        var yaml = Header + "- kind: " +
            (typed
                ? "invokeWorkflowType\n  workflowType: IxIFlow.Generators.Tests.RetryParityChild"
                : "invokeWorkflow\n  workflow: {name: RetryParityChild, version: 1}") + "\n" + """
                  id: child
                  input: [{target: Value, from: {path: workflow.Value}}]
                  output: [{source: Result, to: {path: workflow.Result}}]
                  onError:
                    - exception: System.InvalidOperationException
                      then: ACTION
                RETRY
                    - then: ignore
                """.Replace("ACTION", action).Replace("RETRY",
                action == "retry"
                    ? "      retry: {maximumAttempts: 1, initialInterval: '00:00:00', maximumInterval: '00:00:00', backoffCoefficient: 1}"
                    : "");

        var options = new WorkflowSourceOptions();
        options.WorkflowDataTypes.Add(typeof(RetryParityChild).FullName!, typeof(RetryParityData).FullName!);
        options.Workflows.Add("RetryParityChild@1", new(typeof(RetryParityData).FullName!, 1));
        var generated = Build(WorkflowSourceTranslator.Translate(yaml, "invocation-retry.yaml", options));
        var builder = new WorkflowBuilder<RetryParityData, object>([], "InvocationParity");

        Action<IWorkflowInvocationSetupBuilder<RetryParityData, RetryParityData, object>> setup = setup => setup
            .Input(data => data.Value).From(ctx => ctx.WorkflowData.Value)
            .Output(data => data.Result).To(ctx => ctx.WorkflowData.Result)
            .OnError<InvalidOperationException>(error =>
            {
                if (action == "retry")
                    error.ThenRetry(new RetryPolicy(TimeSpan.Zero, TimeSpan.Zero, 1, 1));
                else if (action == "ignore")
                    error.ThenIgnore();
                else
                    error.ThenTerminate();
            }).OnError(error => error.ThenIgnore());

        var fluent = (typed
            ? builder.Invoke<RetryParityChild, RetryParityData>(setup)
            : builder.Invoke<RetryParityData>("RetryParityChild", 1, setup)).WithId("child").Build();

        Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(generated));
        var first = await Execute(fluent);
        var second = await Execute(generated);
        Assert.Equal(first, second);
        Assert.Equal(action == "retry" ? 2 : 1, first.Length);
        Assert.Contains(action == "retry" ? "Returned" : action == "ignore" ? "Ignore" : "Propagate", first[^1]);
    }

    [Theory]
    [InlineData("36500.00:00:00", 1, true)]
    [InlineData("36500.00:00:00.001", 1, false)]
    [InlineData("36501.00:00:00", 1, false)]
    [InlineData("36501.00:00:00", 0, true)]
    public void PositionedYamlAndFluentBuildAgreeOnReachableDurationLimits(string interval, int retries, bool valid)
    {
        var yaml = Header + $$$$"""
                                - kind: invokeWorkflowType
                                  workflowType: IxIFlow.Generators.Tests.RetryParityChild
                                  dataType: IxIFlow.Generators.Tests.RetryParityData
                                  input: [{target: Value, from: {kind: constant, value: 1}}]
                                  onError:
                                    - then: retry
                                      retry: {maximumAttempts: {{{{retries}}}}, initialInterval: '{{{{interval}}}}', maximumInterval: '{{{{interval}}}}', backoffCoefficient: 1}
                                """;

        if (valid)
            WorkflowSourceTranslator.Translate(yaml, "range.yaml");
        else
        {
            var error = Assert.Throws<WorkflowSourceException>(() => WorkflowSourceTranslator.Translate(yaml, "range.yaml"));
            Assert.Contains("parking duration", error.Message);
            Assert.True(error.Line >= 6);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnalyzerStillChecksRequiredMappingsAcrossPolicyCallbacks(bool mapped)
    {
        var compilation = GeneratorRegressionTests.Compilation("""
                                                               using System;
                                                               using System.ComponentModel.DataAnnotations;
                                                               using IxIFlow.ActivitySdk;
                                                               using IxIFlow.Builders;
                                                               using IxIFlow.Core;
                                                               public sealed class Caller { public int Result { get; set; } }
                                                               public sealed class Child {
                                                                   [WorkflowInput, Required] public int Value { get; set; }
                                                                   [WorkflowInput, Required] public int Other { get; set; }
                                                               }
                                                               public static class Flow {
                                                                   public static object Build() => new WorkflowBuilder<Caller, object>([], "AnalyzerInvocationRetry")
                                                                       .Invoke<Child>("Child", 1, setup => setup.OnError<InvalidOperationException>(error => error.ThenRetry(1))
                                                                           .Input(data => data.Value).OnError(error => error.ThenIgnore()).From(_ => 1)
                                                               MAPPING
                                                                           .OnError(error => error.ThenIgnore())).Build();
                                                               }
                                                               """.Replace("MAPPING",
            mapped ? ".Input(data => data.Other).From(_ => 1)" : ""));

        Assert.Empty(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));

        var diagnostics = await compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new MappingAnalyzer()))
            .GetAnalyzerDiagnosticsAsync();

        if (mapped)
            Assert.Empty(diagnostics);
        else
            Assert.Contains(diagnostics, diagnostic => diagnostic.GetMessage().Contains("Child.Other"));
    }

    [Fact]
    public void InvocationRetrySchemaMatchesTheDocumentContract()
    {
        var generated = DocumentSchema.Write(typeof(WorkflowDocument));
        var path = Path.Combine(WorkflowSchemaFiles.RepositoryRoot(), "src/IxIFlow/schemas/workflow.schema.json");

        if (Environment.GetEnvironmentVariable("IXIFLOW_WRITE_SCHEMA") == "1")
            File.WriteAllText(path, generated);

        Assert.Equal(generated, File.ReadAllText(path).Replace("\r\n", "\n"));
    }

    [Theory]
    [InlineData("exception")]
    [InlineData("order")]
    [InlineData("action")]
    [InlineData("budget")]
    [InlineData("initial")]
    [InlineData("maximum")]
    [InlineData("coefficient")]
    public void EveryInvocationPolicyDimensionChangesTheExecutableHash(string dimension)
    {
        Assert.NotEqual(WorkflowDefinitionValidator.Inspect(Definition("")), WorkflowDefinitionValidator.Inspect(Definition(dimension)));

        static WorkflowDefinition Definition(string change) => new WorkflowBuilder<RetryParityData, object>([], "PolicyHash")
            .Invoke<RetryParityChild, RetryParityData>(setup =>
            {
                setup.Input(data => data.Value).From(ctx => ctx.WorkflowData.Value);

                if (change == "order")
                    setup.OnError(error => error.ThenIgnore());

                void Configure(IStepErrorHandler error)
                {
                    if (change == "action")
                        error.ThenTerminate();
                    else
                        error.ThenRetry(new RetryPolicy(TimeSpan.FromSeconds(change == "initial" ? 2 : 1),
                            TimeSpan.FromSeconds(change == "maximum" ? 20 : 10), change == "budget" ? 3 : 2,
                            change == "coefficient" ? 3 : 2));
                }

                if (change == "exception")
                    setup.OnError<ArgumentException>(error => Configure(error));
                else
                    setup.OnError<InvalidOperationException>(error => Configure(error));

                if (change != "order")
                    setup.OnError(error => error.ThenIgnore());
            }).WithId("child").Build();
    }

    [Fact]
    public void InvalidInvocationExceptionTypesProducePositionedCompilerDiagnostics()
    {
        var source = WorkflowSourceTranslator.Translate(Header + """
                                                                 - kind: invokeWorkflowType
                                                                   workflowType: IxIFlow.Generators.Tests.RetryParityChild
                                                                   dataType: IxIFlow.Generators.Tests.RetryParityData
                                                                   input: [{target: Value, from: {kind: constant, value: 1}}]
                                                                   onError: [{exception: System.String, then: ignore}]
                                                                 """, "invalid-exception.yaml");

        var error = Assert.Single(GeneratorRegressionTests.Compilation(source).GetDiagnostics(),
            diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        Assert.Equal("CS0311", error.Id);
        Assert.Equal("invalid-exception.yaml", error.Location.GetMappedLineSpan().Path);
    }

    private static WorkflowDefinition Build(string source)
    {
        using var stream = new MemoryStream();
        var emitted = GeneratorRegressionTests.Compilation(source).Emit(stream);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        stream.Position = 0;
        var assembly = AssemblyLoadContext.Default.LoadFromStream(stream);

        return (WorkflowDefinition)assembly.GetTypes().Single(type => type.GetMethod("Build") != null).GetMethod("Build")!.Invoke(null,
            null)!;
    }

    private static async Task<string[]> Execute(WorkflowDefinition definition)
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddIxIFlow(options => options.VerifyCheckpointParts = true);
        collection.AddSingleton<RetryParityLedger>();
        collection.RegisterWorkflow<RetryParityChild>();
        await using var services = collection.BuildServiceProvider();
        var client = services.GetRequiredService<IWorkflowClient>();
        var result = await client.RunAsync(definition, new RetryParityData { Value = 7 });

        if (result.Status is WorkflowExecutionStatus.Running or WorkflowExecutionStatus.Suspended)
            result = await client.AwaitAsync(result.InstanceId, WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(10));

        var saved = (await services.GetRequiredService<IWorkflowStateRepository>().GetWorkflowInstanceAsync(result.InstanceId))!;
        Assert.Empty(ExecutionCheckpoint.Read(saved.ExecutionStateJson).InvocationRecords);
        Assert.DoesNotContain(saved.SavedParts!.Keys, key => key.StartsWith("v/", StringComparison.Ordinal));

        var replay = TraceReplay.Load(definition,
            await services.GetRequiredService<IWorkflowStateRepository>().GetTraceAsync(result.InstanceId));

        while (replay.StepForward())
        {
        }

        var attempts = replay.Current.Invocations.OrderBy(attempt => attempt.AttemptNumber).ToArray();
        Assert.Equal(attempts.Length, attempts.Select(attempt => attempt.ChildInstanceId).Distinct().Count());
        Assert.Equal(attempts.Length, attempts.Select(attempt => attempt.PolicyAttemptId).Distinct().Count());

        return attempts.Select(attempt =>
            $"{attempt.AttemptNumber}/{attempt.ChildStatus}/{attempt.Disposition}/{attempt.PolicyIndex}/{attempt.Action}").ToArray();
    }
}

public sealed class RetryParityData
{
    [WorkflowInput, Required] public int Value { get; set; }
    [WorkflowOutput] public int Result { get; set; }
}

public sealed class RetryParityLedger
{
    public int Count;
}

[Workflow("RetryParityChild", Version = 1)]
public sealed class RetryParityChild : IWorkflow<RetryParityData>
{
    public void Build(IWorkflowBuilder<RetryParityData> builder) => builder.Step<RetryParityActivity>(setup => setup
        .Input(activity => activity.Value).From(ctx => ctx.WorkflowData.Value)
        .Output(activity => activity.Result).To(ctx => ctx.WorkflowData.Result));
}

public sealed class RetryParityActivity(RetryParityLedger ledger) : IAsyncActivity
{
    public int Value { get; set; }
    public int Result { get; set; }

    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref ledger.Count) == 1)
            throw new InvalidOperationException("failed child");

        Result = Value * 2;

        return Task.CompletedTask;
    }
}