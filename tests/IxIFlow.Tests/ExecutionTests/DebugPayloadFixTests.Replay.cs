using System.Collections;
using System.Reflection;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

public sealed partial class DebugPayloadFixTests
{
    [Theory]
    [InlineData(false, "completed", false)]
    [InlineData(false, "completed", true)]
    [InlineData(false, "parked", false)]
    [InlineData(false, "parked", true)]
    [InlineData(false, "idle", false)]
    [InlineData(false, "idle", true)]
    [InlineData(true, "completed", false)]
    [InlineData(true, "completed", true)]
    [InlineData(true, "parked", false)]
    [InlineData(true, "parked", true)]
    [InlineData(true, "idle", false)]
    [InlineData(true, "idle", true)]
    public async Task FinalReplyLossStillResolvesTheExactCommitAfterRenewalFails(bool sqlite, string ending, bool retry)
    {
        await using var h = new Harness(sqlite);
        var store = new FinalReplyStore(h.Store, h.Clock, retry);
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddSingleton<TimeProvider>(h.Clock);
        registrations.AddSingleton<IWorkflowStateRepository>(store);
        registrations.AddIxIFlow();
        await using var services = registrations.BuildServiceProvider();
        var control = new RunControl();
        var scheduler = services.GetRequiredService<WorkflowHost>();
        control.OnScheduled(scheduler.Schedule);
        control.OnUnscheduled(scheduler.Unschedule);
        await using var watch = new CancellationWatch(store, h.Clock);
        var operations = new WorkflowHost.InstanceOperations(services, services.GetRequiredService<IActivityExecutor>(),
            store, services.GetRequiredService<IWorkflowCatalog>(), new InProcessInstanceGate(),
            timeProvider: h.Clock, watch: watch, control: control);
        store.Owner = () => ((IDictionary)typeof(WorkflowHost.InstanceOperations)
            .GetField("_assignments", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(operations)!)
            .Values.Cast<WorkflowHost.InstanceOperations.HostedRun>().Single().Owner;
        var definition = ending switch
        {
            "parked" => Workflow.Create<Data>("replay-park").Step<Noop>()
                .Saga(body => body.Step<ReplayFailure>(step => step.OnError<InvalidOperationException>(handler =>
                    handler.ThenRetry(new RetryPolicy(TimeSpan.FromHours(1), TimeSpan.FromHours(1), 1, 1))))).Build(),
            "idle" => Workflow.Create<Data>("replay-idle").Step<Noop>().Delay(TimeSpan.FromHours(1)).Build(),
            _ => Workflow.Create<Data>("replay-complete").Step<Noop>().Build()
        };
        await services.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(definition);
        var (instance, checkpoint, _) = WorkflowInstances.NewInstance(definition, new Data { Text = "kept" }, new(),
            h.Clock.GetUtcNow().UtcDateTime);
        instance.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
        await store.SeedAsync(instance);
        var result = await operations.RecoverAsync(instance.InstanceId, default).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([WorkflowCommitStatus.Applied, WorkflowCommitStatus.AlreadyApplied], store.Statuses);
        Assert.Equal(2, store.CommitIds.Count);
        Assert.Single(store.CommitIds.Distinct());
        Assert.Equal(1, store.RejectedRenewals);
        Assert.Equal(ending == "completed" ? WorkflowExecutionStatus.Success : ending == "parked"
            ? WorkflowExecutionStatus.Scheduled : WorkflowExecutionStatus.Suspended, result.Status);
        Assert.Equal("kept", Assert.IsType<Data>(result.WorkflowData).Text);
    }

    private sealed class FinalReplyStore(IWorkflowStateRepository inner, TimeProvider time, bool retry)
        : FaultInjectingRepository(inner)
    {
        public Func<WorkflowExecutionLease> Owner { get; set; } = null!;
        public List<WorkflowCommitStatus> Statuses { get; } = [];
        public List<string> CommitIds { get; } = [];
        public int RejectedRenewals;
        public override async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            var result = await base.CommitWorkflowInstanceAsync(commit);
            if (commit.Parks || commit.Instance.Status != WorkflowStatus.Running)
            {
                Statuses.Add(result.Status);
                CommitIds.Add(commit.CommitId);
                if (Statuses.Count == 1)
                {
                    var owner = Owner();
                    ((Microsoft.Extensions.Time.Testing.FakeTimeProvider)time).Advance(ExecutionLeaseSettings.Default.RenewalInterval);
                    await ((Task)typeof(WorkflowExecutionLease).GetField("_renewal",
                        BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!).WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.Throws<ExecutionOwnershipLostException>(owner.ThrowIfLost);
                    return retry ? new(WorkflowCommitStatus.Retry, 0) : throw new IOException("Lost final reply");
                }
            }
            return result;
        }
        public override async Task<bool> RenewExecutionLeaseAsync(string id, string token, TimeSpan duration)
        {
            var renewed = await base.RenewExecutionLeaseAsync(id, token, duration);
            if (!renewed) Interlocked.Increment(ref RejectedRenewals);
            return renewed;
        }
    }
    public sealed class ReplayFailure : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("retry later");
    }
}
