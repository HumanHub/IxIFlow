using System.Text.Json.Serialization;
using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using Microsoft.Extensions.DependencyInjection;
using static IxIFlow.Tests.Conformance.InvocationRetryRecoveryScenarios;

namespace IxIFlow.Tests.Conformance;

[Trait("Spec", "D-18")]
public sealed class InvocationRetryAllProfileScenarios
{
    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ExhaustionAfterReopeningUsesTheSavedBudgetAndPropagatesWithoutFallingThroughToIgnore(string profile)
    {
        await using var fixture = await InvocationRetryProfile.Create(profile);
        fixture.Ledger.Failures = int.MaxValue;
        var first = await fixture.Open(services => services.AddSingleton(HostSettings.Default with { Name = "exhaustion-first" }), holdRetries: true);
        var child = Workflow.Create<ChildData>("ExhaustedInvocationChild").Step<ChildEffect>().Build();
        await fixture.Catalog.RegisterWorkflowAsync(child);
        var parent = new WorkflowBuilder<ParentData, object>([], "ExhaustedInvocationParent")
            .Try(body => body.Invoke<ChildData>(child.Name, 1, setup => setup
                    .OnError<Exception>(error => error.ThenRetry(new RetryPolicy(fixture.Delay, fixture.Delay, 1, 1)))
                    .OnError<InvalidOperationException>(error => error.ThenIgnore())
                    .Output(data => data.Result).To(ctx => ctx.WorkflowData.Result))
                .Step<Continued>())
            .Catch<InvalidOperationException, MessageFault>(body => body.Step<Caught>(setup => setup
                .Input(activity => activity.Message).From(ctx => ctx.Fault.Message))).Build();
        await fixture.Catalog.RegisterWorkflowAsync(parent);
        var id = await first.Client.StartAsync(parent, new ParentData());
        var parked = await first.Parked(id);
        var scheduled = Record(parked);
        var original = Assert.Single(scheduled.Attempts);
        Assert.Equal(1, scheduled.ScheduledRetries);
        Assert.Equal("Retry", original.Disposition);
        Assert.Equal("child failure 1", original.Failure!.Message);
        Assert.Single((await first.Store.QueryInstancesAsync(new InstanceQuery(ParentInstanceId: id))).Items);
        await first.DisposeAsync();

        var second = await fixture.Open(services => services.AddSingleton(HostSettings.Default with { Name = "exhaustion-second" }));
        var reopened = Record((await second.Store.GetWorkflowInstanceAsync(id))!);
        Assert.Equal(scheduled.Id, reopened.Id);
        Assert.Equal(original.Id, Assert.Single(reopened.Attempts).Id);
        Assert.Equal(1, reopened.ScheduledRetries);
        Assert.Equal(scheduled.RetryAfterUtc, reopened.RetryAfterUtc);
        await fixture.Due(second.Store, id, InvocationRetryProfile.RetryDeadline(parked));
        await second.Client.RecoverAsync(id);
        var result = await InvocationRetryProfile.Finished(second.Client, id, fixture.Delay);
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var data = Assert.IsType<ParentData>(result.WorkflowData);
        Assert.Equal(1, data.Catches);
        Assert.Equal("child failure 2", data.CaughtMessage);
        Assert.False(data.Continued);
        Assert.Equal("untouched", data.Result);
        var saved = (await second.Store.GetWorkflowInstanceAsync(id))!;
        Assert.Equal(WorkflowStatus.Completed, saved.Status);
        var final = Record(saved);
        Assert.Equal(scheduled.Id, final.Id);
        Assert.Equal(original.Id, final.Attempts[0].Id);
        Assert.Equal("Exhausted", final.Disposition);
        Assert.Equal(1, final.ScheduledRetries);
        Assert.Null(final.RetryAfterUtc);
        Assert.Equal(2, final.Attempts.Count);
        Assert.Equal(2, final.Attempts.Select(attempt => attempt.Id).Distinct().Count());
        Assert.Equal(2, final.Attempts.Select(attempt => attempt.ChildInstanceId).Distinct().Count());
        Assert.NotEqual(final.Attempts[0].Host, final.Attempts[1].Host);
        Assert.Equal(new[] { "Retry", "Exhausted" }, final.Attempts.Select(attempt => attempt.Disposition));
        Assert.All(final.Attempts, attempt =>
        {
            Assert.Equal(0, attempt.SelectedPolicyIndex);
            Assert.Equal(StepErrorAction.Retry, attempt.Action);
            Assert.Equal(WorkflowStatus.Failed, attempt.ChildStatus);
            Assert.Equal(WorkflowTypeIdentity.SavedName(typeof(InvalidOperationException)), attempt.Failure!.Type);
            Assert.Equal($"child failure {attempt.Number}", attempt.Failure.Message);
        });
        var children = (await second.Store.QueryInstancesAsync(new InstanceQuery(ParentInstanceId: id))).Items;
        Assert.Equal(2, children.Count);
        Assert.All(children, instance => Assert.Equal(WorkflowStatus.Failed, instance.Status));
        Assert.Equal(final.Attempts.Select(attempt => attempt.ChildInstanceId).Order(), children.Select(instance => instance.InstanceId).Order());
        Assert.Equal(2, fixture.Ledger.Calls);
        Assert.Single((await second.Client.GetTraceAsync(id)).Select(entry => entry.Payload).OfType<RetryTrace>());
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task OutputMappingFailureAfterSuccessfulChildBypassesInvocationPoliciesAndReachesTheEnclosingCatch(string profile)
    {
        await using var fixture = await InvocationRetryProfile.Create(profile);
        fixture.Ledger.Failures = 0;
        var host = await fixture.Open();
        var child = Workflow.Create<ChildData>("OutputFailureInvocationChild").Step<ChildEffect>().Build();
        await fixture.Catalog.RegisterWorkflowAsync(child);
        var parent = new WorkflowBuilder<ParentData, object>([], "OutputFailureInvocationParent")
            .Try(body => body.Invoke<ChildData>(child.Name, 1, setup => setup
                    .OnError<Exception>(error => error.ThenRetry(new RetryPolicy(fixture.Delay, fixture.Delay, 3, 1)))
                    .OnError<InvalidOperationException>(error => error.ThenIgnore())
                    .Output(data => data.Result).To(ctx => ctx.WorkflowData.ThrowingResult))
                .Step<Continued>())
            .Catch<InvalidOperationException, MessageFault>(body => body.Step<Caught>(setup => setup
                .Input(activity => activity.Message).From(ctx => ctx.Fault.Message))).Build();
        var id = await host.Client.StartAsync(parent, new ParentData());
        var result = await InvocationRetryProfile.Finished(host.Client, id);
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var data = Assert.IsType<ParentData>(result.WorkflowData);
        Assert.Equal(1, data.Catches);
        Assert.Equal("output mapping failed", data.CaughtMessage);
        Assert.False(data.Continued);
        Assert.Equal("untouched", data.Result);
        var saved = (await host.Store.GetWorkflowInstanceAsync(id))!;
        Assert.Equal(WorkflowStatus.Completed, saved.Status);
        var record = Record(saved);
        Assert.Equal("OutputFailed", record.Disposition);
        Assert.Equal(0, record.ScheduledRetries);
        Assert.Null(record.RetryAfterUtc);
        var attempt = Assert.Single(record.Attempts);
        Assert.Equal("OutputFailed", attempt.Disposition);
        Assert.Equal(WorkflowStatus.Completed, attempt.ChildStatus);
        Assert.Equal("output mapping failed", attempt.Failure!.Message);
        Assert.Equal(WorkflowTypeIdentity.SavedName(typeof(InvalidOperationException)), attempt.Failure.Type);
        Assert.Null(attempt.SelectedPolicyIndex);
        Assert.Null(attempt.Action);
        var completedChild = Assert.Single((await host.Store.QueryInstancesAsync(new InstanceQuery(ParentInstanceId: id))).Items);
        Assert.Equal(attempt.ChildInstanceId, completedChild.InstanceId);
        Assert.Equal(WorkflowStatus.Completed, completedChild.Status);
        Assert.Equal("child result", Assert.IsType<ChildData>((await host.Client.AwaitAsync(completedChild.InstanceId,
            WorkflowAwaitCondition.Finished)).WorkflowData).Result);
        Assert.Equal(1, fixture.Ledger.Calls);
        Assert.Empty((await host.Client.GetTraceAsync(id)).Select(entry => entry.Payload).OfType<RetryTrace>());
    }

    public sealed class ParentData
    {
        public string Result { get; set; } = "untouched";
        public int Catches { get; set; }
        public string? CaughtMessage { get; set; }
        public bool Continued { get; set; }
        [JsonIgnore]
        public string? ThrowingResult { get => null; set => throw new InvalidOperationException("output mapping failed"); }
    }
    public sealed class ChildData { [WorkflowOutput] public string? Result { get; set; } }
    public sealed class MessageFault { public string Message { get; set; } = ""; }
    public sealed class ChildEffect(Ledger ledger) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref ledger.Calls);
            if (call <= ledger.Failures) throw new InvalidOperationException($"child failure {call}");
            ((ChildData)context.WorkflowData).Result = "child result";
            return Task.CompletedTask;
        }
    }
    public sealed class Caught : IAsyncActivity
    {
        public string Message { get; set; } = "";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            var data = (ParentData)context.WorkflowData;
            data.Catches++;
            data.CaughtMessage = Message;
            return Task.CompletedTask;
        }
    }
    public sealed class Continued : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { ((ParentData)context.WorkflowData).Continued = true; return Task.CompletedTask; }
    }
}
