using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Dsl.Documents;
using IxIFlow.Extensions;
using IxIFlow.Generators.Tests.Generated;
using Json.Schema;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace IxIFlow.Generators.Tests;

[Trait("Spec", "F-25")]
public sealed partial class YamlParityTests
{
    private const string Header = "workflow: {name: Parity, dataType: ParityData}\ndefinitions:\n";
    private static readonly JsonSchema Schema = JsonSchema.FromFile(WorkflowSchemaFiles.SchemaPath, new BuildOptions { SchemaRegistry = new SchemaRegistry() });

    [Theory]
    [InlineData("all")]
    [InlineData("any")]
    [InlineData("conditionally")]
    public async Task ParallelJoinsAndBranchOutputsMatchFluent(string join)
    {
        var generated = Build($$$$"""
            - kind: parallel
              id: fork
              join: {{{{join}}}}
            {{{{(join == "conditionally" ? "  condition: {path: workflow.Result == 7}" : "")}}}}
              branches:
                - steps:
                    - kind: waitFor
                      id: first
                      key: first
                      event: Answer
                      output: [{source: Value, to: {path: workflow.Result}}]
                - steps:
                    - kind: waitFor
                      id: second
                      key: second
                      event: Answer
                      output: [{source: Value, to: {path: workflow.Value}}]
            """);
        var fluent = Start().Parallel(parallel =>
        {
            parallel.Do(branch => branch.WaitFor<Answer>("first", configure: wait => wait
                .Output(answer => answer.Value).To(ctx => ctx.WorkflowData.Result)).WithId("first"));
            parallel.Do(branch => branch.WaitFor<Answer>("second", configure: wait => wait
                .Output(answer => answer.Value).To(ctx => ctx.WorkflowData.Value)).WithId("second"));
            if (join == "any") parallel.WaitAny();
            if (join == "conditionally") parallel.WaitConditionally(completeWhenExpression: data => data.Result == 7);
        }).WithId("fork").Build();
        await Match(fluent, generated, async (services, result) =>
        {
            var client = services.GetRequiredService<IWorkflowClient>();
            result = await client.ResumeAsync(result.InstanceId, "first", new Answer { Value = 7 });
            if (join == "all") result = await client.ResumeAsync(result.InstanceId, "second", new Answer { Value = 9 });
            Assert.Equal(7, Assert.IsType<ParityData>(result.WorkflowData).Result);
            Assert.Equal(join == "all" ? 9 : 0, Assert.IsType<ParityData>(result.WorkflowData).Value);
            return result;
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JoinCombinerOverridesMatchFluent(bool forEach)
    {
        var generated = Build(forEach ? """
            - kind: forEach
              id: fork
              items: {path: workflow.Values}
              parallel:
                combine: [{property: {path: workflow.Result}, propertyType: int, combiner: 'IxIFlow.Core.Sum<int>'}]
              steps:
                - kind: activity
                  id: write
                  activity: ParityEcho
                  input: [{target: Value, from: {path: item}}]
                  output: [{source: Value, to: {path: workflow.Result}}]
            """ : """
            - kind: parallel
              id: fork
              combine: [{property: {path: workflow.Result}, propertyType: int, combiner: 'IxIFlow.Core.Sum<int>'}]
              branches:
                - steps:
                    - kind: activity
                      id: first
                      activity: ParityEcho
                      input: [{target: Value, from: {kind: constant, value: 4}}]
                      output: [{source: Value, to: {path: workflow.Result}}]
                - steps:
                    - kind: activity
                      id: second
                      activity: ParityEcho
                      input: [{target: Value, from: {kind: constant, value: 6}}]
                      output: [{source: Value, to: {path: workflow.Result}}]
            """);
        var fluent = forEach
            ? Start().ForEach(itemsExpression: ctx => ctx.WorkflowData.Values, (body, item) => body
                .Step<ParityEcho>(s => s.Input(a => a.Value).From(ctx => ctx.Item(item))
                    .Output(a => a.Value).To(ctx => ctx.WorkflowData.Result)).WithId("write"),
                setup => setup.As("item").Parallel().Combine<int, Sum<int>>(data => data.Result)).WithId("fork").Build()
            : Start().Parallel(parallel => parallel
                .Do(body => body.Step<ParityEcho>(s => s.Input(a => a.Value).From(ctx => 4)
                    .Output(a => a.Value).To(ctx => ctx.WorkflowData.Result)).WithId("first"))
                .Do(body => body.Step<ParityEcho>(s => s.Input(a => a.Value).From(ctx => 6)
                    .Output(a => a.Value).To(ctx => ctx.WorkflowData.Result)).WithId("second"))
                .Combine<int, Sum<int>>(data => data.Result)).WithId("fork").Build();
        var result = await Match(fluent, generated);
        Assert.Equal(10, result.Result);
    }

    [Theory]
    [InlineData("whileDo", 0, 3)]
    [InlineData("whileDo", 4, 4)]
    [InlineData("doWhile", 0, 3)]
    [InlineData("doWhile", 4, 5)]
    public async Task LoopConditionsAndPreviousValuesMatchFluent(string kind, int initial, int expected)
    {
        var generated = Build($$$$"""
            - kind: {{{{kind}}}}
              id: loop
              condition: {path: workflow.Result < 3}
              steps:
                - kind: activity
                  id: increment
                  activity: ParityEcho
                  input: [{target: Value, from: {path: workflow.Result + 1}}]
                  output: [{source: Value, to: {path: workflow.Result}}]
            """);
        Action<ISequenceBuilder<ParityData, object>> body = body => body.Step<ParityEcho>(s => s
            .Input(a => a.Value).From(ctx => ctx.WorkflowData.Result + 1)
            .Output(a => a.Value).To(ctx => ctx.WorkflowData.Result)).WithId("increment");
        var fluent = (kind == "whileDo"
            ? Start().WhileDo(conditionExpression: ctx => ctx.WorkflowData.Result < 3, configure: body)
            : Start().DoWhile(body, conditionExpression: ctx => ctx.WorkflowData.Result < 3)).WithId("loop").Build();
        var result = await Match(fluent, generated, data: () => new() { Result = initial, Values = [4, 6] });
        Assert.Equal(expected, result.Result);
    }

    [Theory]
    [InlineData("typed")]
    [InlineData("fault")]
    [InlineData("all")]
    public async Task TypedCatchesFaultsAndFinallyMatchFluent(string kind)
    {
        var generated = Build($$$$"""
            - kind: try
              id: attempt
              steps: [{kind: activity, id: fail, activity: ParityFail}]
              catches:
                - {{{{(kind == "all" ? "steps:" : "exception: ParityFailure")}}}}
            {{{{(kind == "fault" ? "      faultType: ParityFault" : "")}}}}
            {{{{(kind == "all" ? "" : "      steps:")}}}}
                    - kind: activity
                      id: handled
                      activity: ParityEcho
                      input: [{target: Value, from: {{{{(kind == "fault" ? "{path: fault.Code}" : "{kind: constant, value: 17}")}}}}}]
                      output: [{source: Value, to: {path: workflow.Result}}]
              finally:
                - kind: activity
                  id: cleanup
                  activity: ParityEcho
                  input: [{target: Text, from: {kind: constant, value: cleaned}}]
                  output: [{source: Text, to: {path: workflow.Note}}]
            """);
        var attempt = Start().Try(body => body.Step<ParityFail>().WithId("fail"));
        ICatchBuilder<ParityData, object> caught = kind switch
        {
            "fault" => attempt.Catch<ParityFailure, ParityFault>(body => body.Step<ParityEcho>(s => s
                .Input(a => a.Value).From(ctx => ctx.Fault.Code)
                .Output(a => a.Value).To(ctx => ctx.WorkflowData.Result)).WithId("handled")),
            "typed" => attempt.Catch<ParityFailure>(body => body.Step<ParityEcho>(s => s
                .Input(a => a.Value).From(ctx => 17)
                .Output(a => a.Value).To(ctx => ctx.WorkflowData.Result)).WithId("handled")),
            _ => attempt.Catch(body => body.Step<ParityEcho>(s => s.Input(a => a.Value).From(ctx => 17)
                .Output(a => a.Value).To(ctx => ctx.WorkflowData.Result)).WithId("handled"))
        };
        var fluent = caught.Finally(body => body.Step<ParityEcho>(s => s
            .Input(a => a.Text).From(ctx => "cleaned").Output(a => a.Text).To(ctx => ctx.WorkflowData.Note))
            .WithId("cleanup")).WithId("attempt").Build();
        var result = await Match(fluent, generated);
        Assert.Equal(17, result.Result);
        Assert.Equal("cleaned", result.Note);
    }

    [Fact]
    public async Task NestedTryAndFinallyOnlyMatchFluent()
    {
        var generated = Build("""
            - kind: try
              id: outer
              steps:
                - kind: try
                  id: inner
                  steps: [{kind: activity, id: fail, activity: ParityFail}]
                  finally: [{kind: activity, id: cleanup, activity: ParityEcho}]
              catches:
                - exception: ParityFailure
                  steps:
                    - kind: try
                      id: nestedCatch
                      steps:
                        - kind: activity
                          id: handled
                          activity: ParityEcho
                          input: [{target: Value, from: {kind: constant, value: 5}}]
                          output: [{source: Value, to: {path: workflow.Result}}]
                      finally: []
            """);
        var fluent = Start().Try(outer => outer.Try(inner => inner.Step<ParityFail>().WithId("fail"))
            .Finally(body => body.Step<ParityEcho>().WithId("cleanup")).WithId("inner"))
            .Catch<ParityFailure>(body => body.Try(nested => nested.Step<ParityEcho>(s => s
                .Input(a => a.Value).From(ctx => 5).Output(a => a.Value).To(ctx => ctx.WorkflowData.Result)).WithId("handled"))
                .Finally(_ => { }).WithId("nestedCatch")).WithId("outer").Build();
        Assert.Equal(5, (await Match(fluent, generated)).Result);
    }

    [Theory]
    [InlineData("all", "retry")]
    [InlineData("none", "retry")]
    [InlineData("upTo", "retry")]
    [InlineData("all", "continue")]
    [InlineData("none", "continue")]
    [InlineData("upTo", "continue")]
    [InlineData("all", "terminate")]
    [InlineData("all", "raise")]
    [InlineData("none", "raise")]
    [InlineData("upTo", "raise")]
    public async Task SagaUndoAndContinuationChoicesMatchFluent(string undo, string then)
    {
        var generated = Build($$$$"""
            - kind: try
              id: outer
              steps:
                - kind: saga
                  id: saga
                  steps:
                    - kind: activity
                      id: work
                      activity: ParityWork
                      compensation: [{activity: ParityUndo, input: [{target: Value, from: {path: currentStep.Value}}]}]
                    - kind: activity
                      id: second
                      activity: ParitySecondWork
                      compensation: [{activity: ParityUndo, input: [{target: Value, from: {path: currentStep.Value}}]}]
                    - {kind: activity, id: flaky, activity: ParityFlaky}
                  onError:
                    - exception: ParityFailure
                      faultType: ParityFault
                      steps:
                        - kind: activity
                          id: handler
                          activity: ParityEcho
                          input: [{target: Value, from: {path: fault.Code}}]
                          output: [{source: Value, to: {path: workflow.Result}}]
            {{{{(undo == "upTo" ? "          compensateUpTo: ParitySecondWork" : "          compensate: " + undo)}}}}
            {{{{(then == "raise" ? "" : "          then: " + then)}}}}
            {{{{(then == "retry" ? "          retry: {maximumAttempts: 2, initialInterval: '00:00:00', maximumInterval: '00:00:00', backoffCoefficient: 1}" : "")}}}}
              catches:
                - steps: [{kind: activity, id: caught, activity: ParityEcho}]
            """);
        var fluent = Start().Try(body => body.Saga(saga => saga
            .Step<ParityWork>(s => s.CompensateWith<ParityUndo>(c => c.Input(a => a.Value).From(ctx => ctx.CurrentStep.Value))).WithId("work")
            .Step<ParitySecondWork>(s => s.CompensateWith<ParityUndo>(c => c.Input(a => a.Value).From(ctx => ctx.CurrentStep.Value))).WithId("second")
            .Step<ParityFlaky>().WithId("flaky"))
            .OnError<ParityFailure, ParityFault>(handler =>
            {
                var last = handler.Step<ParityEcho>(s => s.Input(a => a.Value).From(ctx => ctx.Fault.Code)
                    .Output(a => a.Value).To(ctx => ctx.WorkflowData.Result)).WithId("handler");
                var continuation = undo switch
                {
                    "all" => last.Compensate(), "none" => last.CompensateNone(),
                    _ => last.CompensateUpTo<ParitySecondWork>()
                };
                if (then == "retry") continuation.ThenRetry(new RetryPolicy(TimeSpan.Zero, TimeSpan.Zero, 2, 1));
                if (then == "continue") continuation.ThenContinue();
                if (then == "terminate") continuation.ThenTerminate();
            }).WithId("saga"))
            .Catch(body => body.Step<ParityEcho>().WithId("caught")).WithId("outer").Build();
        var result = await Match(fluent, generated);
        Assert.Equal(17, result.Result);
        Assert.Equal(then == "retry" && undo == "all" ? 2 : 1, result.Work);
        Assert.Equal(then == "retry" && undo != "none" ? 2 : 1, result.Second);
    }

    [Theory]
    [InlineData("retry")]
    [InlineData("terminate")]
    [InlineData("ignore")]
    public async Task SagaStepErrorPoliciesMatchFluent(string action)
    {
        var generated = Build($$$$"""
            - kind: saga
              id: saga
              steps:
                - kind: activity
                  id: flaky
                  activity: ParityFlaky
                  onError:
                    - exception: ParityFailure
                      then: {{{{action}}}}
            {{{{(action == "retry" ? "          retry: {maximumAttempts: 2, initialInterval: '00:00:00', maximumInterval: '00:00:00', backoffCoefficient: 1}" : "")}}}}
                - kind: activity
                  id: after-step
                  activity: ParityPolicyPath
                  input: [{target: Marker, from: {kind: constant, value: after-step}}]
              onError:
                - steps:
                    - kind: activity
                      id: handler
                      activity: ParityPolicyPath
                      input: [{target: Marker, from: {kind: constant, value: saga-handler}}]
                  compensate: all
                  then: continue
                  ignoreCompensationErrors: true
            - kind: activity
              id: after-saga
              activity: ParityPolicyPath
              input: [{target: Marker, from: {kind: constant, value: after-saga}}]
            """);
        var fluent = Start().Saga(saga => saga.Step<ParityFlaky>(step => step.OnError<ParityFailure>(handler =>
        {
            if (action == "retry") handler.ThenRetry(new RetryPolicy(TimeSpan.Zero, TimeSpan.Zero, 2, 1));
            if (action == "terminate") handler.ThenTerminate();
            if (action == "ignore") handler.ThenIgnore();
        })).WithId("flaky")
            .Step<ParityPolicyPath>(step => step.Input(activity => activity.Marker).From(ctx => "after-step")).WithId("after-step"))
            .OnError(handler => handler
                .Step<ParityPolicyPath>(step => step.Input(activity => activity.Marker).From(ctx => "saga-handler")).WithId("handler")
                .Compensate().ThenContinue(true)).WithId("saga")
            .Step<ParityPolicyPath>(step => step.Input(activity => activity.Marker).From(ctx => "after-saga")).WithId("after-saga").Build();
        await Match(fluent, generated, verifyProbe: probe =>
        {
            Assert.Equal(action == "retry" ? 2 : 1, probe.Attempts);
            Assert.Equal(action switch
            {
                "retry" => new[] { "flaky-succeeded", "after-step", "after-saga" },
                "ignore" => new[] { "after-step", "after-saga" },
                _ => new[] { "saga-handler", "after-saga" }
            }, probe.Paths);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SagaOutcomeLabelsAndDefaultMatchFluent(bool selected)
    {
        var generated = Build("""
            - kind: saga
              id: saga
              steps:
                - kind: outcome
                  id: outcome
                  value: {path: workflow.Selected}
                  branches:
                    - value: {kind: constant, value: true}
                      steps: [{kind: activity, id: chosen, activity: ParityWork}]
                  default: [{kind: activity, id: default, activity: ParitySecondWork}]
            """);
        var fluent = Start().Saga(saga => saga.OutcomeOn(ctx => ctx.WorkflowData.Selected, outcomes => outcomes
            .Outcome(true, body => body.Step<ParityWork>().WithId("chosen"))
            .DefaultOutcome(body => body.Step<ParitySecondWork>().WithId("default"))).WithId("outcome"))
            .WithId("saga").Build();
        var result = await Match(fluent, generated, data: () => new() { Selected = selected });
        Assert.Equal(selected ? 1 : 0, result.Work);
        Assert.Equal(selected ? 0 : 1, result.Second);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayRetainsPreviousStepAndMatchesFluent(bool durable)
    {
        var duration = durable ? TimeSpan.FromHours(1) : TimeSpan.FromTicks(1);
        var generated = Build($$$$"""
            - kind: activity
              id: before
              activity: ParityEcho
              input: [{target: Value, from: {kind: constant, value: 9}}]
            - {kind: delay, id: pause, duration: '{{{{duration:c}}}}'}
            - kind: activity
              id: after
              activity: ParityEcho
              input: [{target: Value, from: {path: previousStep.Value}}]
              output: [{source: Value, to: {path: workflow.Result}}]
            """);
        var fluent = Workflow.Create<ParityData>("Parity").Step<ParityEcho>(s => s.Input(a => a.Value).From(ctx => 9)).WithId("before")
            .Delay(duration).WithId("pause").Step<ParityEcho>(s => s.Input(a => a.Value).From(ctx => ctx.PreviousStep.Value)
                .Output(a => a.Value).To(ctx => ctx.WorkflowData.Result)).WithId("after").Build();
        var result = await Match(fluent, generated, Advance);
        Assert.Equal(9, result.Result);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RequestSubjectsReplyOutputsAndTimeoutPathsMatchFluent(bool saga, bool timeout)
    {
        var steps = """
            - kind: request
              id: request
              key: approval
              activity: ParityRequest
              subject: {path: workflow.Subject}
              input: [{target: Value, from: {path: workflow.Value}}]
              output: [{source: Value, to: {path: workflow.Result}}]
            - kind: waitFor
              id: reply
              key: approval
              event: Answer
              resumeCondition: {path: event.Value > 0}
              input: [{target: Value, from: {path: previousStep.Value}}]
              output: [{source: Value, to: {path: workflow.Result}}]
              timeout: '01:00:00'
              onTimeout:
                - kind: activity
                  id: expired
                  activity: ParityEcho
                  input: [{target: Value, from: {kind: constant, value: -1}}]
                  output: [{source: Value, to: {path: workflow.Result}}]
            """;
        var generated = Build(saga ? "- kind: saga\n  id: saga\n  steps:\n" + Indent(steps, 4) : steps);
        Action<ISuspendSetupBuilder<ParityData, Answer, ParityRequest>> wait = setup => setup
            .Input(a => a.Value).From(ctx => ctx.PreviousStep.Value)
            .Output(a => a.Value).To(ctx => ctx.WorkflowData.Result).TimeoutAfter(TimeSpan.FromHours(1))
            .OnTimeout(body => body.Step<ParityEcho>(s => s.Input(a => a.Value).From(ctx => -1)
                .Output(a => a.Value).To(ctx => ctx.WorkflowData.Result)).WithId("expired"));
        var fluent = saga ? Start().Saga(body => body.Request<ParityRequest>("approval", s => s.Input(a => a.Value).From(ctx => ctx.WorkflowData.Value)
                .Output(a => a.Value).To(ctx => ctx.WorkflowData.Result), subjectExpression: ctx => ctx.WorkflowData.Subject).WithId("request")
                .WaitFor<Answer>("approval", matchesExpression: (evt, ctx) => evt.Value > 0, configure: wait).WithId("reply")).WithId("saga").Build()
            : Workflow.Create<ParityData>("Parity").Request<ParityRequest>("approval", s => s.Input(a => a.Value).From(ctx => ctx.WorkflowData.Value)
                .Output(a => a.Value).To(ctx => ctx.WorkflowData.Result), subjectExpression: ctx => ctx.WorkflowData.Subject).WithId("request")
                .WaitFor<Answer>("approval", matchesExpression: (evt, ctx) => evt.Value > 0, configure: wait).WithId("reply").Build();
        var result = await Match(fluent, generated, timeout ? Advance : async (services, result) =>
        {
            var token = Assert.Single(services.GetRequiredService<ParityProbe>().Tokens);
            Assert.True(ReplyToken.TryRead(token, out _, out _, out var digest));
            Assert.Equal(ReplyToken.Digest("order-1"), digest);
            var client = services.GetRequiredService<IWorkflowClient>();
            var rejected = await client.ReplyAsync(token, new Answer { Value = 0 });
            Assert.Equal(WorkflowExecutionStatus.Suspended, rejected.Status);
            return await client.ReplyAsync(token, new Answer { Value = 8 });
        });
        Assert.Equal(timeout ? -1 : 8, result.Result);
    }

    internal static string Document(string body) => Regex.Replace(Header + body, @"\b(Parity[A-Z]\w*|Answer)\b",
        match => "IxIFlow.Generators.Tests." + match.Value);
    internal static string Indent(string text, int spaces) => string.Join('\n', text.Split('\n').Select(line => new string(' ', spaces) + line));
    private static WorkflowBuilder<ParityData, object> Start() => new([], "Parity");

    internal static WorkflowDefinition Build(string body)
    {
        var yaml = Document(body);
        Assert.True(Schema.Evaluate(WorkflowYamlSchemaValueTests.Read(yaml)).IsValid, yaml);
        var diagnostics = WorkflowGeneratorHarness.Generate(yaml, "parity.yaml").Diagnostics;
        Assert.Empty(diagnostics);
        return GeneratorRegressionTests.Build(yaml);
    }

    private static async Task<ParityData> Match(WorkflowDefinition fluent, WorkflowDefinition generated,
        Func<ServiceProvider, WorkflowExecutionResult, Task<WorkflowExecutionResult>>? idle = null,
        Func<ParityData>? data = null, Action<ParityProbe>? verifyProbe = null)
    {
        Assert.Equal(Shape(fluent), Shape(generated));
        var first = await Execute(fluent, idle, data, verifyProbe);
        var second = await Execute(generated, idle, data, verifyProbe);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(JsonSerializer.Serialize(first.Data), JsonSerializer.Serialize(second.Data));
        return second.Data;
    }

    private static async Task<(ParityData Data, string Fingerprint)> Execute(WorkflowDefinition definition,
        Func<ServiceProvider, WorkflowExecutionResult, Task<WorkflowExecutionResult>>? idle, Func<ParityData>? data,
        Action<ParityProbe>? verifyProbe = null)
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddSingleton<TimeProvider>(new ParityClock());
        collection.AddSingleton<ParityProbe>();
        collection.AddTransient<ParityRequest>();
        collection.AddTransient<ParityFlaky>();
        collection.AddTransient<ParityPolicyPath>();
        collection.AddIxIFlow();
        collection.RegisterWorkflow<ParityChildWorkflow>();
        await using var services = collection.BuildServiceProvider();
        await services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(Workflow_child.Build());
        var client = services.GetRequiredService<IWorkflowClient>();
        var result = await client.RunAsync(definition, data?.Invoke() ?? new ParityData { Values = [4, 6] });
        if (idle != null) result = await idle(services, result);
        if (result.Status is WorkflowExecutionStatus.Running or WorkflowExecutionStatus.Suspended)
            result = await client.AwaitAsync(result.InstanceId, WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(10));
        verifyProbe?.Invoke(services.GetRequiredService<ParityProbe>());
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        var saved = await services.GetRequiredService<IWorkflowStateRepository>().GetWorkflowInstanceAsync(result.InstanceId);
        using var checkpoint = JsonDocument.Parse(saved!.ExecutionStateJson);
        return (Assert.IsType<ParityData>(result.WorkflowData), checkpoint.RootElement.GetProperty("DefinitionFingerprint").GetString()!);
    }

    private static async Task<WorkflowExecutionResult> Advance(ServiceProvider services, WorkflowExecutionResult result)
    {
        Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
        ((ParityClock)services.GetRequiredService<TimeProvider>()).Advance(TimeSpan.FromHours(2));
        using var scanner = new WorkflowRecoveryService(services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<IWorkflowStateRepository>(), NullLogger<WorkflowRecoveryService>.Instance,
            services.GetRequiredService<TimeProvider>());
        await scanner.RecoverOnceAsync(CancellationToken.None);
        return await services.GetRequiredService<IWorkflowClient>().AwaitAsync(result.InstanceId, WorkflowAwaitCondition.Finished)
            .WaitAsync(TimeSpan.FromSeconds(10));
    }

    // Extends the existing equivalence shape without changing any pre-existing test or assertion.
    private static string Shape(WorkflowDefinition definition) => JsonSerializer.Serialize(new
    {
        Base = GeneratedWorkflowTests.Shape(definition), Extra = definition.Steps.Select(Extra)
    });

    private static object Extra(WorkflowStep step) => new
    {
        Id = step.StepType == WorkflowStepType.CatchBlock ? null : step.Id, step.Name, step.StepType, step.Order, step.LoopType,
        Exception = step.ExceptionType?.FullName, Fault = step.FaultType?.FullName,
        WorkflowType = step.WorkflowType?.FullName, step.RequestName, Reply = step.ReplyType?.FullName, step.ReplyToRequest, step.CancelWithParent,
        Combiners = step.ParallelCombiners.Select(pair => new { pair.Key, Type = pair.Value.FullName }),
        Condition = Signature(step.ConditionExpression),
        Join = Signature(step.ParallelCompletionExpression), Subject = Signature(step.RequestSubjectExpression),
        Policy = step.StepMetadata.GetValueOrDefault("SagaErrorConfig") is SagaErrorConfiguration policy ? new
        {
            policy.CompensationStrategy, policy.ContinuationAction, Target = policy.CompensationTargetType?.FullName,
            policy.IgnoreCompensationErrors, Retry = Policy(policy.RetryPolicy)
        } : null,
        Errors = (step.StepMetadata.GetValueOrDefault("StepErrorHandlers") as IEnumerable<StepErrorHandlerInfo> ?? [])
            .Select(handler => new { Exception = handler.ExceptionType.FullName, handler.HandlerAction, Retry = Policy(handler.RetryPolicy) }),
        Compensation = (step.StepMetadata.GetValueOrDefault("CompensationActivities") as IEnumerable<CompensationActivityInfo> ?? [])
            .Select(undo => new { Activity = undo.ActivityType.FullName, Reply = undo.ReplyType?.FullName,
                Previous = undo.PreviousChainActivityType?.FullName,
                Inputs = Mappings(undo.InputMappings), Outputs = Mappings(undo.OutputMappings) }),
        Loop = step.LoopBodySteps.Select(Extra), Catch = step.CatchBlocks.Select(Extra), Finally = step.FinallySteps.Select(Extra),
        Outcomes = step.OutcomeBranches.Select(branch => new { branch.Value, Steps = branch.Steps.Select(Extra) }),
        Sequence = step.SequenceSteps.Select(Extra), Then = step.ThenSteps.Select(Extra), Else = step.ElseSteps.Select(Extra),
        Parallel = step.ParallelBranches.Select(branch => branch.Select(Extra)), Each = step.ForEachBodySteps.Select(Extra),
        Timeout = step.TimeoutSteps.Select(Extra), Inputs = Mappings(step.InputMappings), Outputs = Mappings(step.OutputMappings)
    };
    private static string? Signature(System.Linq.Expressions.LambdaExpression? expression) => expression == null ? null : WorkflowExpressionSignature.Of(expression);
    private static object Mappings(IEnumerable<PropertyMapping> mappings) => mappings.Select(mapping => new
        { mapping.TargetProperty, mapping.CompensationSource, Source = mapping.SourceType?.FullName, Target = mapping.TargetType?.FullName, Expression = Signature(mapping.Expression) });
    private static object? Policy(RetryPolicy? policy) => policy == null ? null : new
        { policy.MaximumAttempts, policy.InitialInterval, policy.MaximumInterval, policy.BackoffCoefficient };
}

public sealed class ParityData : FlowData
{
    public bool Selected { get; set; }
    public ParityMode Mode { get; set; }
    public string Subject { get; set; } = "order-1";
    public int Work { get; set; }
    public int Second { get; set; }
    public int Undone { get; set; }
    public bool Answered { get; set; }
}
public sealed class ParityEcho : IAsyncActivity
{
    public int Value { get; set; }
    public string? Text { get; set; } = "";
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
public sealed class ParityFailure : Exception
{
    public ParityFailure() : base("failure") { }
    public int Code => 17;
}
public sealed class ParityFault { public int Code { get; set; } }
public sealed class ParityFail : IAsyncActivity
{
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => throw new ParityFailure();
}
public sealed class ParityWork : IAsyncActivity
{
    public int Value { get; set; } = 1;
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    { ((ParityData)context.WorkflowData).Work++; return Task.CompletedTask; }
}
public sealed class ParitySecondWork : IAsyncActivity
{
    public int Value { get; set; } = 2;
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    { ((ParityData)context.WorkflowData).Second++; return Task.CompletedTask; }
}
public sealed class ParityUndo : IAsyncActivity
{
    public int Value { get; set; }
    public bool Answered { get; set; }
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    { var data = (ParityData)context.WorkflowData; data.Undone += Value; data.Answered = Answered; return Task.CompletedTask; }
}
public sealed class ParityProbe
{
    public int Attempts;
    public List<string> Tokens { get; } = [];
    public List<string> Paths { get; } = [];
}
public sealed class ParityFlaky(ParityProbe probe) : IAsyncActivity
{
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Increment(ref probe.Attempts) == 1)
            throw new ParityFailure();
        probe.Paths.Add("flaky-succeeded");
        return Task.CompletedTask;
    }
}
public sealed class ParityPolicyPath(ParityProbe probe) : IAsyncActivity
{
    public string Marker { get; set; } = "";
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        probe.Paths.Add(Marker);
        return Task.CompletedTask;
    }
}
public sealed class ParityRequest(ParityProbe probe) : IRequestActivity<Answer>
{
    public int Value { get; set; }
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    { probe.Tokens.Add(context.Reply!.Token); return Task.CompletedTask; }
}
public sealed class ParityClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan duration) => _now += duration;
}
