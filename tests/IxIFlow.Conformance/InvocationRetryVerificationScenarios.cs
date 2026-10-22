using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using IxIFlow.ActivitySdk;
using IxIFlow.Authoring;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Dsl.Documents;
using IxIFlow.Extensions;
using IxIFlow.Generators;
using Microsoft.Extensions.DependencyInjection;
using static IxIFlow.Tests.Conformance.InvocationRetryRecoveryScenarios;

namespace IxIFlow.Tests.Conformance;

[Trait("Spec", "D-18")]
public sealed class InvocationRetryVerificationScenarios
{
    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task SteppingInvocationBeforeItsRetryDeadlineKeepsTheAttemptAndSiblingHeldAcrossReopening(string profile)
    {
        await using var fixture = await InvocationRetryProfile.Create(profile);
        var first = await fixture.Open();
        var child = new WorkflowBuilder<Data, object>([], "FutureStepRetryChild")
            .WaitFor<Signal>("fail").Step<FailOnce>().Build();
        await fixture.Catalog.RegisterWorkflowAsync(child);
        // Leave enough server-clock time to pause, reopen and step before the deadline.
        var delay = TimeSpan.FromSeconds(30);
        var parent = new WorkflowBuilder<Data, object>([], "FutureStepRetryInvocation").Parallel(paths => paths
            .Do(body => body.WaitFor<Signal>("held").Step<InvocationRetryCleanupScenarios.Noop>())
            .Do(body => body.Invoke<Data>(child.Name, 1, setup => setup.OnError(error => error.ThenRetry(
                new RetryPolicy(delay, delay, 1, 1)))).WithId("invoke").Step<InvocationRetryCleanupScenarios.Noop>())).Build();
        await fixture.Catalog.RegisterWorkflowAsync(parent);
        var id = (await first.Client.RunAsync(parent, new Data())).InstanceId;
        var original = Assert.Single(Record((await first.Store.GetWorkflowInstanceAsync(id))!).Attempts);
        await InvocationRetryProfile.Until(first.Store, original.ChildInstanceId!, saved => saved.Status == WorkflowStatus.Suspended);
        // Hold the parent before failing its child. The debugger consumes the completion and
        // schedules the retry while paused, so no automatic timer can erase the state we need.
        await first.Operations.PauseAsync(id, "before-deadline");
        await InvocationRetryProfile.Until(first.Store, id, saved => saved.Status == WorkflowStatus.Paused);
        await first.Client.ResumeAsync(original.ChildInstanceId!, "fail", new Signal());
        var waiting = ExecutionCheckpoint.Read((await first.Store.GetWorkflowInstanceAsync(id))!.ExecutionStateJson);
        var branch = waiting.Continuations.Single(c => c.InvocationRecordId != null).Id;
        await first.Operations.StepAsync(id, branch, DebugStepKind.Over, "schedule-retry");
        var parked = await InvocationRetryProfile.Until(first.Store, id, saved => saved.Status == WorkflowStatus.Paused &&
            ExecutionCheckpoint.Read(saved.ExecutionStateJson).InvocationRecords.Values.Any(record => record.RetryAfterUtc != null));
        var scheduled = Record(parked);
        await first.DisposeAsync();
        var second = await fixture.Open();
        var before = ExecutionCheckpoint.Read((await second.Store.GetWorkflowInstanceAsync(id))!.ExecutionStateJson);
        var selected = before.Continuations.Single(c => c.InvocationRecordId == scheduled.Id).Id;
        var siblings = before.Continuations.Where(c => c.Id != selected).Select(c => JsonSerializer.Serialize(c)).ToArray();
        await second.Store.AppendEventAsync(new(id, "held-event", WorkflowTypeIdentity.SavedName(typeof(Signal)), "{}", Key: "held"));
        Assert.True((await second.Store.GetPendingEventsAsync(id, 0, 0)).StoreTimeUtc < scheduled.RetryAfterUtc);
        Assert.Equal(WorkflowExecutionStatus.Paused,
            (await second.Operations.StepAsync(id, selected, DebugStepKind.Over, "before-deadline")).Status);
        var blocked = (await second.Store.GetWorkflowInstanceAsync(id))!;
        var attempt = Assert.Single(Record(blocked).Attempts);
        Assert.Equal(scheduled.Attempts[0].Id, attempt.Id);
        Assert.Equal(scheduled.RetryAfterUtc, Record(blocked).RetryAfterUtc);
        Assert.Single((await second.Store.QueryInstancesAsync(new InstanceQuery(ParentInstanceId: id))).Items);
        Assert.Equal(siblings, ExecutionCheckpoint.Read(blocked.ExecutionStateJson).Continuations
            .Where(c => c.Id != selected).Select(c => JsonSerializer.Serialize(c)).ToArray());
        Assert.Equal(EventOutcome.Pending, (await second.Store.GetEventAsync(id, "held-event"))!.Outcome);
        await fixture.Due(second.Store, id, scheduled.RetryAfterUtc!.Value);
        await second.Operations.StepAsync(id, selected, DebugStepKind.Over, "after-deadline");
        Assert.Equal(2, Record((await second.Store.GetWorkflowInstanceAsync(id))!).Attempts.Count);
        Assert.Equal(EventOutcome.Pending, (await second.Store.GetEventAsync(id, "held-event"))!.Outcome);
        await second.Operations.ContinueAsync(id, "finish");
        var replacement = Record((await second.Store.GetWorkflowInstanceAsync(id))!).Attempts[1];
        await second.Client.ResumeAsync(replacement.ChildInstanceId!, "fail", new Signal());
        await Success(second.Client, id, delay);
    }

    public static IEnumerable<object[]> EndedChildProfiles => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { false, true }.Select(terminate => new object[] { profile, terminate }));

    [Theory, MemberData(nameof(EndedChildProfiles))]
    public async Task IndependentlyEndedInvocationChildrenRetryAsNewChildren(string profile, bool terminate)
    {
        await using var fixture = await InvocationRetryProfile.Create(profile);
        var host = await fixture.Open();
        var child = new WorkflowBuilder<Data, object>([], "EndedRetryChild").WaitFor<Signal>("finish").Build();
        await fixture.Catalog.RegisterWorkflowAsync(child);
        var parent = new WorkflowBuilder<Data, object>([], "EndedRetryInvocation").Invoke<Data>(child.Name, 1,
            setup => setup.OnError<ChildWorkflowEndedException>(error => error.ThenRetry(
                new RetryPolicy(fixture.Delay, fixture.Delay, 1, 1)))).Build();
        var started = await host.Client.RunAsync(parent, new Data());
        var original = Assert.Single(Record((await host.Store.GetWorkflowInstanceAsync(started.InstanceId))!).Attempts);
        await InvocationRetryProfile.Until(host.Store, original.ChildInstanceId!, saved => saved.Status == WorkflowStatus.Suspended);
        if (terminate) await host.Operations.TerminateAsync(original.ChildInstanceId!, "independent-child");
        else await host.Client.CancelAsync(original.ChildInstanceId!, new CancellationReason { ReasonCode = "independent-child" });
        var expected = terminate ? WorkflowStatus.Terminated : WorkflowStatus.Cancelled;
        await InvocationRetryProfile.Until(host.Store, original.ChildInstanceId!, saved => saved.Status == expected);
        // Attempt history survives an automatic replacement; the active retry deadline does not.
        var scheduled = await InvocationRetryProfile.Until(host.Store, started.InstanceId,
            saved => Record(saved).Attempts[0].RetryAfterUtc != null, fixture.Delay);
        var failed = Record(scheduled).Attempts[0];
        Assert.Equal(expected, failed.ChildStatus);
        Assert.Equal("Retry", failed.Disposition);
        Assert.Equal(0, failed.SelectedPolicyIndex);
        await fixture.Due(host.Store, started.InstanceId, failed.RetryAfterUtc!.Value);
        await host.Client.RecoverAsync(started.InstanceId);
        var replaced = await InvocationRetryProfile.Until(host.Store, started.InstanceId, saved => Record(saved).Attempts.Count == 2);
        var replacement = Record(replaced).Attempts[1];
        Assert.NotEqual(original.Id, replacement.Id);
        Assert.NotEqual(original.ChildInstanceId, replacement.ChildInstanceId);
        var next = await InvocationRetryProfile.Until(host.Store, replacement.ChildInstanceId!, saved => saved.Status == WorkflowStatus.Suspended);
        var old = (await host.Store.GetWorkflowInstanceAsync(original.ChildInstanceId!))!;
        Assert.Equal(expected, old.Status);
        Assert.NotEqual(old.ParentWaitId, next.ParentWaitId);
        await host.Client.ResumeAsync(replacement.ChildInstanceId!, "finish", new Signal());
        await Success(host.Client, started.InstanceId);
        Assert.Equal(expected, (await host.Store.GetWorkflowInstanceAsync(original.ChildInstanceId!))!.Status);
        Assert.Equal(1, Record((await host.Store.GetWorkflowInstanceAsync(started.InstanceId))!).ScheduledRetries);
    }

    public static IEnumerable<object[]> ParityProfiles => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { false, true }.SelectMany(typed => new[] { "retry", "ignore", "terminate" }
            .Select(action => new object[] { profile, typed, action })));

    [Theory, MemberData(nameof(ParityProfiles))]
    public async Task FluentAndYamlInvocationPoliciesAgreeOnDataPreviousDelayAndTerminalResult(string profile, bool typed, string action)
    {
        await using var fixture = await InvocationRetryProfile.Create(profile);
        var ledger = new Round2ParityLedger();
        var host = await fixture.Open(services =>
        {
            services.AddSingleton(ledger);
            services.RegisterWorkflow<Round2ParityChild>();
        });
        var child = Workflow.Create<Round2ParityData>("Round2ParityChild").Step<Round2ParityActivity>().Build();
        await fixture.Catalog.RegisterWorkflowAsync(child);
        var yaml = """
            workflow: {name: Round2ParityInvocation, dataType: Data}
            definitions:
              - kind: activity
                id: seed
                activity: Seed
              - kind: KIND
                id: child
            TARGET
                input: [{target: Value, from: {path: workflow.Value + previousStep.Value}}]
                output: [{source: Result, to: {path: workflow.Result}}]
                onError:
                  - exception: System.InvalidOperationException
                    then: ACTION
            RETRY
              - kind: activity
                id: observe
                activity: Observe
                input: [{target: Prior, from: {path: previousStep}}]
                output: [{source: Value, to: {path: workflow.Previous}}]
            """.Replace("KIND", typed ? "invokeWorkflowType" : "invokeWorkflow")
            .Replace("TARGET", typed ? "    workflowType: Child" : "    workflow: {name: Round2ParityChild, version: 1}")
            .Replace("ACTION", action).Replace("RETRY", action == "retry" ?
                "        retry: {maximumAttempts: 1, initialInterval: '00:00:02', maximumInterval: '00:00:02', backoffCoefficient: 1}" : "");
        var options = new WorkflowSourceOptions();
        options.Types["Data"] = typeof(Round2ParityData).FullName!;
        options.Types["Seed"] = typeof(Round2ParitySeed).FullName!;
        options.Types["Observe"] = typeof(Round2ParityObserve).FullName!;
        options.Types["Child"] = typeof(Round2ParityChild).FullName!;
        options.WorkflowDataTypes["Child"] = typeof(Round2ParityData).FullName!;
        options.Workflows["Round2ParityChild@1"] = new(typeof(Round2ParityData).FullName!, 1);
        var generated = WorkflowCompilation.Build(WorkflowSourceTranslator.Translate(yaml, "round2-parity.yaml", options));
        var builder = Workflow.Create<Round2ParityData>("Round2ParityInvocation")
            .Step<Round2ParitySeed>().WithId("seed");
        Action<IWorkflowInvocationSetupBuilder<Round2ParityData, Round2ParityData, Round2ParitySeed>> setup = setup => setup
            .Input(data => data.Value).From(ctx => ctx.WorkflowData.Value + ctx.PreviousStep.Value)
            .Output(data => data.Result).To(ctx => ctx.WorkflowData.Result)
            .OnError<InvalidOperationException>(error =>
            {
                if (action == "retry") error.ThenRetry(new RetryPolicy(fixture.Delay, fixture.Delay, 1, 1));
                else if (action == "ignore") error.ThenIgnore();
                else error.ThenTerminate();
            });
        var fluent = (typed ? builder.Invoke<Round2ParityChild, Round2ParityData>(setup) :
                builder.Invoke<Round2ParityData>("Round2ParityChild", 1, setup)).WithId("child")
            .Step<Round2ParityObserve>(setup => setup.Input(activity => activity.Prior)
                .From(ctx => ctx.PreviousStep)
                .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Previous)).WithId("observe").Build();
        Assert.Equal(WorkflowDefinitionValidator.Inspect(fluent), WorkflowDefinitionValidator.Inspect(generated));
        var first = await Execute(fluent);
        var second = await Execute(generated);
        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.Data, second.Data);
        Assert.Equal(first.Attempts, second.Attempts);

        async Task<(WorkflowExecutionStatus Status, string Data, string[] Attempts)> Execute(WorkflowDefinition definition)
        {
            ledger.Inputs.Clear();
            var id = await host.Client.StartAsync(definition, new Round2ParityData { Value = 3 });
            if (action == "retry")
            {
                var scheduled = await InvocationRetryProfile.Until(host.Store, id, saved =>
                    ExecutionCheckpoint.Read(saved.ExecutionStateJson).InvocationRecords.Values.Any(record =>
                        record.Attempts.FirstOrDefault()?.RetryAfterUtc != null), fixture.Delay);
                var record = Record(scheduled);
                var attempt = record.Attempts[0];
                Assert.Equal("Retry", attempt.Disposition);
                var applied = Assert.Single((await host.Client.GetTraceAsync(id)), entry => entry.EntryType == TraceEntryType.InvocationPolicyApplied);
                Assert.Equal(fixture.Delay, attempt.RetryAfterUtc!.Value - applied.Timestamp);
                await fixture.Due(host.Store, id, InvocationRetryProfile.RetryDeadline(scheduled));
                await host.Client.RecoverAsync(id);
            }
            var result = await InvocationRetryProfile.Finished(host.Client, id, action == "retry" ? fixture.Delay : TimeSpan.Zero);
            Assert.Equal(action == "terminate" ? WorkflowExecutionStatus.Faulted : WorkflowExecutionStatus.Success, result.Status);
            Assert.Equal(action == "terminate" ? WorkflowStatus.Failed : WorkflowStatus.Completed,
                (await host.Store.GetWorkflowInstanceAsync(id))!.Status);
            var data = Assert.IsType<Round2ParityData>(result.WorkflowData);
            Assert.Equal(3, data.Value);
            Assert.Equal(action == "retry" ? 20 : -1, data.Result);
            Assert.Equal(action == "retry" ? 20 : action == "ignore" ? -1 : -99, data.Previous);
            Assert.Equal(action == "retry" ? 2 : 1, ledger.Inputs.Count);
            Assert.All(ledger.Inputs, input => Assert.Equal(10, input));
            var final = Record((await host.Store.GetWorkflowInstanceAsync(id))!);
            Assert.Equal(action == "retry" ? 1 : 0, final.ScheduledRetries);
            return (result.Status, JsonSerializer.Serialize(data), final.Attempts.Select(attempt =>
                $"{attempt.Number}/{attempt.ChildStatus}/{attempt.Disposition}/{attempt.SelectedPolicyIndex}/{attempt.Action}").ToArray());
        }
    }
}

public sealed class Round2ParityData
{
    [WorkflowInput, Required] public int Value { get; set; }
    [WorkflowOutput] public int Result { get; set; } = -1;
    public int Previous { get; set; } = -99;
}
public sealed class Round2ParityLedger { public List<int> Inputs { get; } = []; }
[Workflow("Round2ParityChild", Version = 1)]
public sealed class Round2ParityChild : IWorkflow<Round2ParityData>
{
    public void Build(IWorkflowBuilder<Round2ParityData> builder) => builder.Step<Round2ParityActivity>();
}
public sealed class Round2ParityActivity(Round2ParityLedger ledger) : IAsyncActivity
{
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    {
        var data = (Round2ParityData)context.WorkflowData;
        ledger.Inputs.Add(data.Value);
        if (ledger.Inputs.Count == 1) throw new InvalidOperationException("first child failed");
        data.Result = data.Value * 2;
        return Task.CompletedTask;
    }
}
public sealed class Round2ParitySeed : IAsyncActivity
{
    public int Value { get; set; } = 7;
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
public sealed class Round2ParityObserve : IAsyncActivity
{
    public Round2ParityData? Prior { get; set; }
    public int Value { get; set; }
    public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
    { Value = Prior?.Result ?? -1; return Task.CompletedTask; }
}
