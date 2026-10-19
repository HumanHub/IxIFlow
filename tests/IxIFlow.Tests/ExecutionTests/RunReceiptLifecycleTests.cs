using System.Collections;
using System.Reflection;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "host")]
[Trait("Spec", "A-02")]
[Trait("Spec", "B-09")]
public sealed class RunReceiptLifecycleTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Fact]
    public async Task AQuarantinedInstanceLeavesNoReceiptQueueOrAssignmentOnTheHost()
    {
        await using var host = new Harness();
        var instance = await host.SeedCrashedInstanceAsync();

        var result = await host.Operations.RecoverAsync(instance.InstanceId, default).WaitAsync(Patience);

        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, result.Status);
        Assert.Contains("quarantined", result.ErrorMessage);
        var saved = (await host.Store.GetWorkflowInstanceAsync(instance.InstanceId))!;
        Assert.Equal(WorkflowStatus.NeedsResolution, saved.Status);
        Assert.Equal(OperatorStopKind.Quarantine, ExecutionCheckpoint.Read(saved.ExecutionStateJson).OperatorStop!.Kind);
        AssertNoRunBookkeeping(host.Operations);
    }

    [Fact]
    public async Task ADeletedHighRevisionQuarantineCanStartAgainWithItsRealResultAndTrace()
    {
        await using var host = new Harness();
        var instance = await host.SeedCrashedInstanceAsync();
        var quarantined = await host.Operations.RecoverAsync(instance.InstanceId, default).WaitAsync(Patience);
        Assert.Equal(WorkflowExecutionStatus.NeedsResolution, quarantined.Status);
        var old = (await host.Store.GetWorkflowInstanceAsync(instance.InstanceId))!;
        await host.DeleteForRecreationAsync(instance.InstanceId);

        var result = await host.Operations.StartAsync(host.Definition, new Data { Value = "new incarnation" },
            new WorkflowOptions { InstanceId = instance.InstanceId }, default).WaitAsync(Patience);

        var saved = (await host.Store.GetWorkflowInstanceAsync(instance.InstanceId))!;
        Assert.Equal(WorkflowStatus.Completed, saved.Status);
        Assert.NotEqual(old.Incarnation, saved.Incarnation);
        Assert.True(old.Revision > saved.Revision);
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Null(result.ErrorMessage);
        Assert.Equal("new incarnation", Assert.IsType<Data>(result.WorkflowData).Value);
        Assert.Contains(result.TraceEntries, entry => entry.EntryType == TraceEntryType.ActivityCompleted);
        Assert.Equal((await host.Operations.GetTraceAsync(instance.InstanceId)).Select(entry => entry.TraceId),
            result.TraceEntries.Select(entry => entry.TraceId));
        AssertNoRunBookkeeping(host.Operations);
    }

    [Fact]
    public async Task AStartThatThrowsAfterItsInitialCommitLeavesNoRunBookkeeping()
    {
        await using var host = new Harness();
        host.Control.OnAccepted(_ => throw new InvalidOperationException("Acceptance failed after the commit"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.Operations.StartAsync(
            host.Definition, new Data(), new WorkflowOptions { InstanceId = "acceptance-failed" }, default)
            .WaitAsync(Patience));

        Assert.Equal("Acceptance failed after the commit", error.Message);
        var saved = (await host.Store.GetWorkflowInstanceAsync("acceptance-failed"))!;
        Assert.Equal(1, saved.Revision);
        Assert.Equal(WorkflowStatus.Running, saved.Status);
        AssertNoRunBookkeeping(host.Operations);
    }

    private static void AssertNoRunBookkeeping(WorkflowHost.InstanceOperations operations)
    {
        Assert.Empty((IDictionary)operations.GetType().GetField("_assignments", Fields)!.GetValue(operations)!);
        // A removed run must not leave its receipt or queue in another host collection.
        foreach (var field in operations.GetType().GetFields(Fields))
            if (field.FieldType.IsGenericType && field.FieldType.GetGenericArguments()
                .Any(type => type.Name is "RunReceipt" or "CommitQueue" or "HostedRun"))
                Assert.Empty((IEnumerable)field.GetValue(operations)!);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly FakeTimeProvider _time = new();
        private readonly ServiceProvider _services;
        private readonly CancellationWatch _watch;
        public FaultInjectingRepository Store { get; }
        public RunControl Control { get; } = new();
        public WorkflowHost.InstanceOperations Operations { get; }
        public WorkflowDefinition Definition { get; } = Workflow.Create<Data>("ReceiptLifecycle").Step<Noop>().Build();

        public Harness()
        {
            Store = new(_time);
            var services = new ServiceCollection().AddLogging();
            services.AddSingleton<TimeProvider>(_time);
            services.AddSingleton<IWorkflowStateRepository>(Store);
            services.AddIxIFlow();
            services.AddTransient<Noop>();
            _services = services.BuildServiceProvider();
            _watch = new(Store, _time);
            Operations = new(_services, _services.GetRequiredService<IActivityExecutor>(), Store,
                _services.GetRequiredService<IWorkflowVersionRegistry>(), new InProcessInstanceGate(),
                timeProvider: _time, watch: _watch, control: Control,
                hostSettings: HostSettings.Default with { MaxCrashes = 1 });
        }

        public async Task<WorkflowInstance> SeedCrashedInstanceAsync()
        {
            await _services.GetRequiredService<IWorkflowVersionRegistry>().RegisterWorkflowAsync(Definition);
            var (instance, checkpoint, _) = WorkflowInstances.NewInstance(Definition,
                new Data { Value = "old incarnation" }, new(), _time.GetUtcNow().UtcDateTime);
            instance.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
            for (var index = 0; index < 64; index++)
                await Store.SeedAsync(instance);
            var duration = TimeSpan.FromSeconds(1);
            Assert.NotNull(await Store.TryAcquireExecutionLeaseAsync(instance.InstanceId, "crashed", duration));
            _time.Advance(duration * 2);
            return (await Store.GetWorkflowInstanceAsync(instance.InstanceId))!;
        }

        public async Task DeleteForRecreationAsync(string instanceId)
        {
            var retention = TimeSpan.FromSeconds(1);
            await Store.DeleteWorkflowInstanceAsync(instanceId, retention);
            // Reuse is allowed after the deletion tombstone expires, on the same host and store.
            _time.Advance(retention * 2);
        }

        public async ValueTask DisposeAsync()
        {
            await _watch.DisposeAsync();
            await _services.DisposeAsync();
        }
    }

    public sealed class Data { public string Value { get; set; } = ""; }
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
