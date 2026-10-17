using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// Operator calls are recorded with who made them: a plain string, the client's default unless the call names someone
/// else, as a user interface acting for its users does (D-38). The engine records it and checks nothing.
/// </summary>
[Trait("Feature", "tracing")]
[Trait("Spec", "G-01")]
public sealed class OperatorActorTests
{
    [Theory]
    [InlineData("", "orders-service")]
    [InlineData("joe@ops", "joe@ops")]
    public async Task ACancellationIsTracedWithWhoAskedForIt(string requestedBy, string recorded)
    {
        using var services = Services(new InMemoryWorkflowStateRepository(), "orders-service");
        var client = services.GetRequiredService<IWorkflowClient>();
        var started = await client.RunAsync(Workflow.Create<Data>("CancelledByActor")
            .Step<Noop>()
            .WaitFor<Go>("go")
            .Build(), new Data());

        var cancelled = await client.CancelAsync(started.InstanceId,
            new CancellationReason { ReasonCode = "withdrawn", RequestedBy = requestedBy });

        Assert.Equal(WorkflowExecutionStatus.Cancelled, cancelled.Status);
        var entry = Assert.Single(await client.GetTraceAsync(started.InstanceId),
            item => item.EntryType == TraceEntryType.CancellationRequested);
        Assert.Equal(recorded, entry.Metadata["Actor"].ToString());
        Assert.Equal("withdrawn", entry.Metadata["ReasonCode"].ToString());
    }

    [Fact]
    public async Task AReleaseIsTracedWithWhoReleased()
    {
        var store = new InMemoryWorkflowStateRepository();
        string instanceId;
        using (var limited = Services(store, "orders-service", maxCheckpointBytes: 8_000))
        {
            var stopped = await limited.GetRequiredService<IWorkflowClient>().RunAsync(Growing(), new Data());
            Assert.Equal(WorkflowExecutionStatus.NeedsResolution, stopped.Status);
            instanceId = stopped.InstanceId;
        }
        using var raised = Services(store, "orders-service");
        await raised.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(Growing());
        var client = raised.GetRequiredService<IWorkflowClient>();

        var released = await client.ReleaseAsync(instanceId, "joe@ops");

        Assert.True(released.Status == WorkflowExecutionStatus.Success, released.ErrorMessage);
        var entry = Assert.Single(await client.GetTraceAsync(instanceId),
            item => item.EntryType == TraceEntryType.Released);
        Assert.Equal("joe@ops", entry.Metadata["Actor"].ToString());
    }

    // Without a default and without a name, nothing is invented.
    [Fact]
    public async Task ACallNamingNobodyWithoutADefaultRecordsNoActor()
    {
        using var services = Services(new InMemoryWorkflowStateRepository(), actor: null);
        var client = services.GetRequiredService<IWorkflowClient>();
        var started = await client.RunAsync(Workflow.Create<Data>("CancelledByNobody")
            .Step<Noop>()
            .WaitFor<Go>("go")
            .Build(), new Data());

        await client.CancelAsync(started.InstanceId, new CancellationReason { ReasonCode = "withdrawn" });

        var entry = Assert.Single(await client.GetTraceAsync(started.InstanceId),
            item => item.EntryType == TraceEntryType.CancellationRequested);
        Assert.False(entry.Metadata.ContainsKey("Actor"));
    }

    private static WorkflowDefinition Growing() =>
        Workflow.Create<Data>("GrowsPastItsLimit")
            .Step<Grows>(step => step.Output(activity => activity.Text).To(ctx => ctx.WorkflowData.Text))
            .Build();

    private static ServiceProvider Services(IWorkflowStateRepository store, string? actor,
        long maxCheckpointBytes = 1_000_000)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(store);
        services.AddIxIFlow(options =>
        {
            options.Actor = actor;
            options.MaxCheckpointBytes = maxCheckpointBytes;
        });
        return services.BuildServiceProvider();
    }

    public sealed class Data
    {
        public string Text { get; set; } = "";
    }

    public sealed class Go;

    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>Gives the data more text than a small checkpoint limit takes; safe to run again.</summary>
    public sealed class Grows : IRecoverableActivity<bool>
    {
        public string Text { get; private set; } = "";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Text = new string('x', 20_000);
            return Task.CompletedTask;
        }

        public bool CaptureRecoveryState(IActivityContext context) => true;

        public Task<ActivityRecoveryResult<bool>> RecoverAsync(bool state, IActivityContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ActivityRecoveryResult<bool>>(new ActivityRecoveryResult<bool>.Execute());
    }
}
