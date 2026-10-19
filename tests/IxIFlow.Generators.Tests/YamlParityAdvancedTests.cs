using System.Reflection;
using System.Runtime.Loader;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Dsl.Documents;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Generators.Tests;

public sealed partial class YamlParityTests
{
    [Fact]
    public async Task MultipleCompensationsAndPreviousChainMappingsMatchFluent()
    {
        var generated = Build("""
            - kind: activity
              id: seed
              activity: ParityEcho
              input: [{target: Value, from: {kind: constant, value: 7}}]
            - kind: saga
              id: saga
              steps:
                - kind: activity
                  id: work
                  activity: ParityWork
                  compensation:
                    - activity: ParityUndo
                      input: [{target: Value, from: {path: currentStep.Value}}]
                    - activity: ParityChainUndo
                      previousActivity: ParityUndo
                      input: [{target: Extra, from: {path: workflow.Value}}]
                      previousStepInput: [{target: Seed, from: {path: previousStep.Value}}]
                      previousCompensationInput: [{target: PreviousValue, from: {path: previousCompensation.Value}}]
                      output: [{source: Result, to: {path: workflow.Result}}]
                - {kind: activity, id: fail, activity: ParityFail}
              onError: [{compensate: all, then: continue}]
            """);
        var fluent = Workflow.Create<ParityData>("Parity").Step<ParityEcho>(s => s.Input(a => a.Value).From(ctx => 7)).WithId("seed")
            .Saga(saga => saga.Step<ParityWork>(s => s
                .CompensateWith<ParityUndo>(c => c.Input(a => a.Value).From(ctx => ctx.CurrentStep.Value))
                .CompensateWith<ParityChainUndo, ParityUndo>(c => c
                    .Input(a => a.Extra).From((WorkflowContext<ParityData> ctx) => ctx.WorkflowData.Value)
                    .Input(a => a.Seed).From((ParityEcho ctx) => ctx.Value)
                    .Input(a => a.PreviousValue).From((ParityUndo ctx) => ctx.Value)
                    .Output(a => a.Result).To(ctx => ctx.WorkflowData.Result))).WithId("work")
                .Step<ParityFail>().WithId("fail"))
            .OnError(handler => handler.Compensate().ThenContinue()).WithId("saga").Build();
        var result = await Match(fluent, generated);
        Assert.Equal(8, result.Result);
        Assert.Equal(1, result.Undone);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestCompensationReadsAnsweredAndReplyAndMatchesFluent(bool answered)
    {
        var generated = Build("""
            - kind: saga
              id: saga
              steps:
                - kind: request
                  id: request
                  key: approval
                  activity: ParityRequest
                  input: [{target: Value, from: {kind: constant, value: 3}}]
                  compensation:
                    - activity: ParityUndo
                      replyType: Answer
                      input:
                        - {target: Answered, from: {path: answered}}
                        - {target: Value, from: {path: COMPENSATION_VALUE}}
                      output: [{source: Value, to: {path: workflow.Result}}]
                - kind: waitFor
                  id: reply
                  key: approval
                  event: Answer
                  timeout: '01:00:00'
                  onTimeout: [{kind: activity, id: timeout, activity: ParityFail}]
                - {kind: activity, id: fail, activity: ParityFail}
              onError: [{compensate: all, then: continue}]
            """.Replace("COMPENSATION_VALUE", answered ? "reply.Value" : "currentStep.Value", StringComparison.Ordinal));
        var fluent = Start().Saga(saga => saga.Request<ParityRequest>("approval", setup => setup
            .Input(a => a.Value).From(ctx => 3)
            .CompensateWith<ParityUndo, Answer>((IRequestCompensationActivityBuilder<ParityData, ParityUndo, ParityRequest, Answer> c) =>
            {
                c.Input(a => a.Answered).From(ctx => ctx.Answered);
                if (answered) c.Input(a => a.Value).From(ctx => ctx.Reply!.Value);
                else c.Input(a => a.Value).From(ctx => ctx.CurrentStep.Value);
                c.Output(a => a.Value).To(ctx => ctx.WorkflowData.Result);
            })).WithId("request")
            .WaitFor<Answer>("approval", configure: wait => wait.TimeoutAfter(TimeSpan.FromHours(1)).OnTimeout(body => body.Step<ParityFail>().WithId("timeout"))).WithId("reply")
            .Step<ParityFail>().WithId("fail"))
            .OnError(handler => handler.Compensate().ThenContinue()).WithId("saga").Build();
        var result = await Match(fluent, generated, answered ? async (services, started) =>
            await services.GetRequiredService<IWorkflowClient>().ReplyAsync(Assert.Single(services.GetRequiredService<ParityProbe>().Tokens), new Answer { Value = 8 }) : Advance);
        Assert.Equal(answered, result.Answered);
        Assert.Equal(answered ? 8 : 3, result.Result);
    }

    [Fact]
    public async Task SagaHandlerWaitAndTypedFaultMatchFluent()
    {
        var generated = Build("""
            - kind: saga
              id: saga
              steps: [{kind: activity, id: fail, activity: ParityFail}]
              onError:
                - exception: ParityFailure
                  faultType: ParityFault
                  steps:
                    - kind: activity
                      id: fault
                      activity: ParityEcho
                      input: [{target: Value, from: {path: fault.Code}}]
                      output: [{source: Value, to: {path: workflow.Result}}]
                    - kind: waitFor
                      id: resolved
                      key: resolve
                      event: Answer
                      resumeCondition: {path: event.Value == 1}
                  compensate: all
                  then: continue
            """);
        var fluent = Start().Saga(saga => saga.Step<ParityFail>().WithId("fail"))
            .OnError<ParityFailure, ParityFault>(handler => handler.Step<ParityEcho>(s => s
                .Input(a => a.Value).From(ctx => ctx.Fault.Code).Output(a => a.Value).To(ctx => ctx.WorkflowData.Result)).WithId("fault")
                .WaitFor<Answer>("resolve", matchesExpression: (evt, ctx) => evt.Value == 1).WithId("resolved")
                .Compensate().ThenContinue()).WithId("saga").Build();
        Assert.Equal(17, (await Match(fluent, generated, async (services, started) =>
            await services.GetRequiredService<IWorkflowClient>().ResumeAsync(started.InstanceId, "resolve", new Answer { Value = 1 }))).Result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForEachTimeoutResultExitsMatchFluent(bool timeout)
    {
        var generated = Build("""
            - kind: forEach
              id: each
              items: {path: workflow.Values}
              steps:
                - kind: waitFor
                  id: answer
                  key: answer
                  event: Answer
                  timeout: '01:00:00'
                  onTimeout: [{kind: result, id: expired, value: {kind: constant, value: -1}}]
              result: {path: previousStep.Value}
              into: {path: workflow.Results}
            """);
        var fluent = Start().ForEach(itemsExpression: ctx => ctx.WorkflowData.Values, (body, item) => body
            .WaitFor<Answer>("answer", configure: wait => wait.TimeoutAfter(TimeSpan.FromHours(1))
                .OnTimeout(path => path.Result(valueExpression: ctx => -1).WithId("expired"))).WithId("answer")
            .Result(valueExpression: ctx => ctx.PreviousStep.Value).WithId("each/result"),
            setup => setup.As("item").Into(ctx => ctx.WorkflowData.Results)).WithId("each").Build();
        var result = await Match(fluent, generated, timeout ? Advance : async (services, started) =>
            await services.GetRequiredService<IWorkflowClient>().ResumeAsync(started.InstanceId, "answer", new Answer { Value = 9 }),
            () => new() { Values = [4] });
        Assert.Equal(timeout ? -1 : 9, Assert.Single(result.Results).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuspendTimeoutAndEventMappingsMatchFluent(bool timeout)
    {
        var generated = Build("""
            - kind: suspend
              id: answer
              reason: answer
              event: Answer
              resumeCondition: {path: event.Value > 0}
              input: [{target: Value, from: {path: workflow.Value}}]
              output: [{source: Value, to: {path: workflow.Result}}]
              timeout: '01:00:00'
              onTimeout:
                - kind: activity
                  id: expired
                  activity: ParityEcho
                  input: [{target: Value, from: {kind: constant, value: -1}}]
                  output: [{source: Value, to: {path: workflow.Result}}]
            """);
        var fluent = Start().Suspend<Answer>("answer", resumeExpression: (evt, ctx) => evt.Value > 0, configure: wait => wait
            .Input(a => a.Value).From(ctx => ctx.WorkflowData.Value)
            .Output(a => a.Value).To(ctx => ctx.WorkflowData.Result).TimeoutAfter(TimeSpan.FromHours(1))
            .OnTimeout(path => path.Step<ParityEcho>(s => s.Input(a => a.Value).From(ctx => -1)
                .Output(a => a.Value).To(ctx => ctx.WorkflowData.Result)).WithId("expired"))).WithId("answer").Build();
        Assert.Equal(timeout ? -1 : 6, (await Match(fluent, generated, timeout ? Advance : async (services, started) =>
            await services.GetRequiredService<IWorkflowClient>().ResumeAsync(started.InstanceId, "answer", new Answer { Value = 6 }))).Result);
    }

    [Fact]
    public async Task CatchForEachUsesTheSameTypedSelectorsAsFluent()
    {
        var generated = Build("""
            - kind: try
              id: attempt
              steps: [{kind: activity, id: fail, activity: ParityFail}]
              catches:
                - exception: ParityFailure
                  steps:
                    - kind: forEach
                      id: each
                      items: {path: workflow.Values}
                      steps:
                        - kind: activity
                          id: write
                          activity: ParityEcho
                          input: [{target: Value, from: {path: item}}]
                          output: [{source: Value, to: {path: workflow.Result}}]
            """);
        var fluent = Start().Try(body => body.Step<ParityFail>().WithId("fail"))
            .Catch<ParityFailure>(caught => caught.ForEach(itemsExpression: ctx => ctx.WorkflowData.Values, (body, item) => body
                .Step<ParityEcho>(s => s.Input(a => a.Value).From(ctx => ctx.Item(item))
                    .Output(a => a.Value).To(ctx => ctx.WorkflowData.Result)).WithId("write"), setup => setup.As("item")).WithId("each"))
            .WithId("attempt").Build();
        Assert.Equal(6, (await Match(fluent, generated)).Result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChildCancellationPolicyMatchesPinnedFluentInvocation(bool cancel)
    {
        var yaml = Document("""
            - kind: invokeWorkflow
              id: child
              workflow: {name: Child, version: 3}
              cancelWithParent: CANCEL
              input: [{target: Value, from: {kind: constant, value: 5}}]
              output: [{source: Result, to: {path: workflow.Result}}]
            """.Replace("CANCEL", cancel ? "true" : "false", StringComparison.Ordinal));
        Assert.True(Schema.Evaluate(WorkflowYamlSchemaValueTests.Read(yaml)).IsValid);
        var options = new WorkflowSourceOptions();
        options.Workflows.Add("Child@3", new WorkflowSourceReference(typeof(FlowData).FullName!, 3));
        var compilation = GeneratorRegressionTests.Compilation(WorkflowSourceTranslator.Translate(yaml, "child-policy.yaml", options));
        var generated = Load(compilation, "Workflow_child_policy");
        var fluent = Start().Invoke<FlowData>("Child", 3, setup => setup.CancelWithParent(cancel)
            .Input(a => a.Value).From(ctx => 5).Output(a => a.Result).To(ctx => ctx.WorkflowData.Result)).WithId("child").Build();
        Assert.Equal(10, (await Match(fluent, generated)).Result);
    }

    [Fact]
    public async Task CompiledParityFixtureKeepsStableIdsLocationsAndRuns()
    {
        var first = Generated.Workflow_parity.Build();
        var second = Generated.Workflow_parity.Build();
        Assert.Equal(Shape(first), Shape(second));
        Assert.All(first.Steps, step => Assert.Contains("parity.yaml:", step.SourceLocation));
        var result = await Execute(first, Advance, null);
        Assert.Equal(17, result.Data.Result);
        Assert.Equal(1, result.Data.Work);
    }

    [Theory]
    [InlineData(ParityMode.Ready)]
    [InlineData(ParityMode.Other)]
    public async Task TypedEnumOutcomeLabelsMatchFluent(ParityMode mode)
    {
        var generated = Build("""
            - kind: saga
              id: saga
              steps:
                - kind: outcome
                  id: outcome
                  value: {path: workflow.Mode}
                  branches:
                    - value: {kind: function, function: ready}
                      steps: [{kind: activity, id: selected, activity: ParityWork}]
                  default: [{kind: activity, id: fallback, activity: ParitySecondWork}]
            code:
              ready:
                language: csharp-expression
                source: ParityMode.Ready
            """);
        var fluent = Start().Saga(saga => saga.OutcomeOn(ctx => ctx.WorkflowData.Mode, outcomes => outcomes
            .Outcome(ParityMode.Ready, body => body.Step<ParityWork>().WithId("selected"))
            .DefaultOutcome(body => body.Step<ParitySecondWork>().WithId("fallback"))).WithId("outcome")).WithId("saga").Build();
        var result = await Match(fluent, generated, data: () => new() { Mode = mode });
        Assert.Equal(mode == ParityMode.Ready ? 1 : 0, result.Work);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypeBasedChildInvocationMatchesItsFluentOverload(bool cancel)
    {
        var generated = Build("""
            - kind: invokeWorkflowType
              id: child
              workflowType: ParityChildWorkflow
              dataType: IxIFlow.Generators.Tests.FlowData
              cancelWithParent: CANCEL
              input: [{target: Value, from: {kind: constant, value: 5}}]
              output: [{source: Result, to: {path: workflow.Result}}]
            """.Replace("CANCEL", cancel ? "true" : "false", StringComparison.Ordinal));
        var fluent = Start().Invoke<ParityChildWorkflow, FlowData>(setup => setup.CancelWithParent(cancel)
            .Input(a => a.Value).From(ctx => 5).Output(a => a.Result).To(ctx => ctx.WorkflowData.Result)).WithId("child").Build();
        Assert.Equal(10, (await Match(fluent, generated)).Result);
    }

    [Fact]
    public async Task SagaRetryKeepsItsAnsweredRequestAndMatchesFluent()
    {
        var generated = Build("""
            - kind: saga
              id: saga
              steps:
                - {kind: request, id: request, activity: ParityRequest, key: approval}
                - {kind: waitFor, id: reply, event: Answer, key: approval}
                - {kind: activity, id: flaky, activity: ParityFlaky}
              onError:
                - exception: ParityFailure
                  compensate: none
                  then: retry
                  retry: {maximumAttempts: 2, initialInterval: '00:00:00', maximumInterval: '00:00:00', backoffCoefficient: 1}
            """);
        var fluent = Start().Saga(saga => saga.Request<ParityRequest>("approval").WithId("request")
            .WaitFor<Answer>("approval").WithId("reply").Step<ParityFlaky>().WithId("flaky"))
            .OnError<ParityFailure>(handler => handler.CompensateNone().ThenRetry(new RetryPolicy(TimeSpan.Zero, TimeSpan.Zero, 2, 1)))
            .WithId("saga").Build();
        await Match(fluent, generated, async (services, started) =>
        {
            var probe = services.GetRequiredService<ParityProbe>();
            var result = await services.GetRequiredService<IWorkflowClient>().ReplyAsync(Assert.Single(probe.Tokens), new Answer());
            Assert.Single(probe.Tokens);
            Assert.Equal(2, probe.Attempts);
            return result;
        });
    }

    [Theory]
    [InlineData("whileDo")]
    [InlineData("doWhile")]
    public async Task LoopsRetainTheIncomingPreviousValue(string kind)
    {
        var generated = Build("""
            - kind: activity
              id: seed
              activity: ParityEcho
              input: [{target: Value, from: {kind: constant, value: 2}}]
            - kind: LOOP
              id: loop
              condition: {path: workflow.Result < previousStep.Value}
              steps:
                - kind: activity
                  id: increment
                  activity: ParityEcho
                  input: [{target: Value, from: {path: workflow.Result + 1}}]
                  output: [{source: Value, to: {path: workflow.Result}}]
            """.Replace("LOOP", kind, StringComparison.Ordinal));
        var start = Workflow.Create<ParityData>("Parity").Step<ParityEcho>(s => s.Input(a => a.Value).From(ctx => 2)).WithId("seed");
        Action<ISequenceBuilder<ParityData, ParityEcho>> body = loop => loop.Step<ParityEcho>(s => s
            .Input(a => a.Value).From(ctx => ctx.WorkflowData.Result + 1)
            .Output(a => a.Value).To(ctx => ctx.WorkflowData.Result)).WithId("increment");
        var fluent = (kind == "whileDo" ? start.WhileDo(conditionExpression: ctx => ctx.WorkflowData.Result < ctx.PreviousStep.Value, configure: body)
            : start.DoWhile(body, conditionExpression: ctx => ctx.WorkflowData.Result < ctx.PreviousStep.Value)).WithId("loop").Build();
        Assert.Equal(2, (await Match(fluent, generated)).Result);
    }

    [Fact]
    public async Task OrderedCatchesAndAnEmptySagaHandlerMatchFluent()
    {
        var generated = Build("""
            - kind: try
              id: try
              steps:
                - kind: saga
                  id: saga
                  steps: [{kind: activity, id: fail, activity: ParityFail}]
                  onError: [{steps: []}]
              catches:
                - exception: System.ArgumentException
                  steps: [{kind: activity, id: wrong, activity: ParityWork}]
                - exception: ParityFailure
                  faultType: ParityFault
                  steps:
                    - kind: activity
                      id: match
                      activity: ParityEcho
                      input: [{target: Value, from: {path: fault.Code}}]
                      output: [{source: Value, to: {path: workflow.Result}}]
                - steps: [{kind: activity, id: fallback, activity: ParitySecondWork}]
            """);
        var fluent = Start().Try(body => body.Saga(saga => saga.Step<ParityFail>().WithId("fail")).OnError(_ => { }).WithId("saga"))
            .Catch<ArgumentException>(body => body.Step<ParityWork>().WithId("wrong"))
            .Catch<ParityFailure, ParityFault>(body => body.Step<ParityEcho>(s => s.Input(a => a.Value).From(ctx => ctx.Fault.Code)
                .Output(a => a.Value).To(ctx => ctx.WorkflowData.Result)).WithId("match"))
            .Catch(body => body.Step<ParitySecondWork>().WithId("fallback")).WithId("try").Build();
        Assert.Equal(17, (await Match(fluent, generated)).Result);
    }

    [Fact]
    public async Task ParameterlessSagaHandlerActivityKeepsTheIncomingPreviousType()
    {
        var generated = Build("""
            - kind: activity
              id: seed
              activity: ParityEcho
              input: [{target: Value, from: {kind: constant, value: 7}}]
            - kind: saga
              id: saga
              steps: [{kind: activity, id: fail, activity: ParityFail}]
              onError:
                - steps:
                    - {kind: activity, id: audit, activity: ParityWork}
                    - kind: activity
                      id: report
                      activity: ParityEcho
                      input: [{target: Value, from: {kind: constant, value: 7}}]
                      output: [{source: Value, to: {path: workflow.Result}}]
                  compensate: all
                  then: continue
            """);
        var fluent = Workflow.Create<ParityData>("Parity").Step<ParityEcho>(s => s.Input(a => a.Value).From(ctx => 7)).WithId("seed")
            .Saga(saga => saga.Step<ParityFail>().WithId("fail"))
            .OnError(handler => handler.Step<ParityWork>().WithId("audit").Step<ParityEcho>(s => s
                .Input(a => a.Value).From(ctx => 7).Output(a => a.Value).To(ctx => ctx.WorkflowData.Result)).WithId("report")
                .Compensate().ThenContinue()).WithId("saga").Build();
        Assert.Equal(7, (await Match(fluent, generated)).Result);
    }

    [Fact]
    public void ParameterlessSagaHandlerTemplateKeepsThePreviousTypeInTimeoutCompensation()
    {
        var generated = Build("""
            - kind: activity
              id: seed
              activity: ParityEcho
              input: [{target: Value, from: {kind: constant, value: 7}}]
            - kind: saga
              id: saga
              steps: [{kind: activity, id: fail, activity: ParityFail}]
              onError:
                - steps:
                    - {kind: activityRef, id: audit, ref: audit}
                    - kind: waitFor
                      id: answer
                      key: answer
                      event: Answer
                      timeout: '00:01:00'
                      onTimeout:
                        - kind: saga
                          id: timeoutSaga
                          steps:
                            - kind: activity
                              id: work
                              activity: ParityWork
                              compensation:
                                - activity: ParityUndo
                                - activity: ParityChainUndo
                                  previousActivity: ParityUndo
                                  previousStepInput: [{target: Seed, from: {path: previousStep.Value}}]
                  compensate: all
                  then: continue
            activities:
              audit: {activity: ParityWork}
            """);
        var fluent = Workflow.Create<ParityData>("Parity").Step<ParityEcho>(setup => setup.Input(a => a.Value).From(ctx => 7)).WithId("seed")
            .Saga(saga => saga.Step<ParityFail>().WithId("fail"))
            .OnError(handler => handler.Step<ParityWork>().WithId("audit")
                .WaitFor<Answer>("answer", configure: wait => wait.TimeoutAfter(TimeSpan.FromMinutes(1)).OnTimeout(path => path
                    .Saga(saga => saga.Step<ParityWork>(setup => setup
                        .CompensateWith<ParityUndo>(compensation => { })
                        .CompensateWith<ParityChainUndo, ParityUndo>(compensation => compensation
                            .Input(a => a.Seed).From((ParityEcho ctx) => ctx.Value))).WithId("work")).WithId("timeoutSaga")))
                .WithId("answer").Compensate().ThenContinue()).WithId("saga").Build();
        Assert.Equal(Shape(fluent), Shape(generated));
    }

    [Fact]
    public async Task ExplicitForEachBodyResultWithCustomIdMatchesFluent()
    {
        var generated = Build("""
            - kind: forEach
              id: each
              items: {path: workflow.Values}
              steps: [{kind: result, id: selected, value: {path: item}}]
              into: {path: workflow.Results}
            """);
        var fluent = Start().ForEach(itemsExpression: ctx => ctx.WorkflowData.Values, (body, item) => body
            .Result(valueExpression: ctx => ctx.Item(item)).WithId("selected"),
            setup => setup.As("item").Into(ctx => ctx.WorkflowData.Results)).WithId("each").Build();
        Assert.Equal(new[] { 4, 6 }, (await Match(fluent, generated)).Results.Select(result => result.Value));
    }


    [Fact]
    public void ParityReferenceTranslatesAndCompiles()
    {
        var yaml = File.ReadAllText(Path.Combine(WorkflowSchemaFiles.RepositoryRoot(), "tests/IxIFlow.Generators.Tests/Documents/parity-reference.yaml"));
        var options = new WorkflowSourceOptions();
        options.Workflows.Add("Child@3", new WorkflowSourceReference(typeof(FlowData).FullName!, 3));
        options.WorkflowDataTypes.Add(typeof(ParityChildWorkflow).FullName!, typeof(FlowData).FullName!);
        var source = WorkflowSourceTranslator.Translate(yaml, "parity-reference.yaml", options);
        Assert.Empty(GeneratorRegressionTests.Compilation(source).GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public async Task SagaRetryDefaultsMatchFluent()
    {
        var generated = Build("""
            - kind: saga
              id: saga
              steps: [{kind: activity, id: work, activity: ParityWork}]
              onError: [{id: recover, compensate: all, then: retry}]
            """);
        var fluent = Start().Saga(body => body.Step<ParityWork>().WithId("work"))
            .OnError(handler => handler.Compensate().ThenRetry(3), id: "recover").WithId("saga").Build();
        Assert.Equal("recover", Assert.Single(generated.Steps[0].CatchBlocks).Id);
        await Match(fluent, generated);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void HandlerIdsAndLocationsBelongToBlocks(bool saga, bool typed)
    {
        var yaml = Document(saga ? """
            - kind: saga
              steps: [{kind: activity, activity: ParityWork}]
              onError:
                - id: recovery
                  steps: [{kind: activity, id: audit, activity: ParityWork}]
                  compensate: all
                  then: continue
            """ : """
            - kind: try
              catches:
                - id: recovery
                  steps: [{kind: activity, id: audit, activity: ParityWork}]
            """);
        if (typed) yaml = yaml.Replace("id: recovery", "id: recovery\n      exception: IxIFlow.Generators.Tests.ParityFailure\n      faultType: IxIFlow.Generators.Tests.ParityFault", StringComparison.Ordinal);
        var definition = GeneratorRegressionTests.Build(yaml);
        var block = Assert.Single(Assert.Single(definition.Steps).CatchBlocks);
        Assert.Equal("recovery", block.Id);
        Assert.Equal("audit", Assert.Single(block.SequenceSteps).Id);
        var line = Array.FindIndex(yaml.Split('\n'), text => text.Contains("id: recovery", StringComparison.Ordinal)) + 1;
        Assert.Equal("regression.yaml:" + line, block.SourceLocation);
    }


    [Fact]
    public void GeneratorInfersWorkflowDataTypeFromItsInterface()
    {
        var yaml = Document("- kind: invokeWorkflowType\n  workflowType: ParityChildWorkflow");
        var generated = WorkflowGeneratorHarness.Generate(yaml, "inferred.yaml");
        Assert.Empty(generated.Diagnostics);
        var source = Assert.Single(generated.GeneratedTrees).ToString();
        Assert.Contains("Invoke<global::IxIFlow.Generators.Tests.ParityChildWorkflow, global::IxIFlow.Generators.Tests.FlowData>", source);
        Assert.Empty(GeneratorRegressionTests.Compilation(source).GetDiagnostics().Where(item => item.Severity == DiagnosticSeverity.Error));
        var error = Assert.Throws<WorkflowSourceException>(() => WorkflowSourceTranslator.Translate(yaml, "inferred.yaml"));
        Assert.Contains("Cannot infer", error.Message);
        Assert.Equal(4, error.Line);
    }


    [Fact]
    public async Task StepRetryDefaultsMatchFluentIntegerOverload()
    {
        var generated = Build("""
            - kind: saga
              id: saga
              steps:
                - kind: activity
                  id: work
                  activity: ParityWork
                  onError: [{then: retry}]
            """);
        var fluent = Start().Saga(body => body.Step<ParityWork>(setup => setup.OnError(error => error.ThenRetry(3)))
            .WithId("work")).WithId("saga").Build();
        await Match(fluent, generated);
    }

    private static WorkflowDefinition Load(CSharpCompilation compilation, string className)
    {
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        Assert.True(result.Success, string.Join('\n', result.Diagnostics));
        stream.Position = 0;
        var assembly = AssemblyLoadContext.Default.LoadFromStream(stream);
        try { return (WorkflowDefinition)assembly.GetTypes().Single(type => type.Name == className).GetMethod("Build")!.Invoke(null, null)!; }
        catch (TargetInvocationException error)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException!).Throw(); throw; }
    }
}

public sealed class ParityChainUndo : IAsyncActivity
{
    public int Seed { get; set; }
    public int PreviousValue { get; set; }
    public int Extra { get; set; }
    public int Result { get; set; }
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    { Result = Seed + PreviousValue + Extra; return Task.CompletedTask; }
}

public enum ParityMode { Ready, Other }

[IxIFlow.Core.Workflow("ParityChildWorkflow", Version = 3)]
public sealed class ParityChildWorkflow : IWorkflow<FlowData>
{
    public void Build(IWorkflowBuilder<FlowData> builder) => builder.Step<Multiply>(setup => setup
        .Input(a => a.Value).From(ctx => ctx.WorkflowData.Value)
        .Output(a => a.Result).To(ctx => ctx.WorkflowData.Result));
}
