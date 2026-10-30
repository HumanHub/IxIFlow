using System.Collections.Concurrent;
using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "scopes"), Trait("Spec", "F-26")]
public sealed class ScopeReviewRegressionTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    [Theory, InlineData("root"), InlineData("branch"), InlineData("inline")]
    public async Task FirstBodyClaimPersistsScopeOutputsInItsDataOwner(string owner)
    {
        var probe = new Probe();
        var store = new FaultInjectingRepository();
        var collection = new ServiceCollection().AddSingleton(probe);
        collection.AddIxIFlow(options => options.VerifyCheckpointParts = true);
        collection.AddSingleton<IWorkflowStateRepository>(store);
        await using var services = collection.BuildServiceProvider();
        var builder = new WorkflowBuilder<Data, object>([], "ScopeClaim" + owner);

        var definition = owner switch
        {
            "branch" => builder.Parallel(p => p.Do(b => Body(b)).Do(_ =>
            {
            })).Build(),
            "inline" => builder.Use<Inline, Data>(s => s.Output(x => x.Text).To(c => c.WorkflowData.Text)).Build(),
            _ => Body(builder).Build()
        };

        store.FailCommit = _ => probe.Executed;
        var client = services.GetRequiredService<IWorkflowClient>();
        var id = Guid.NewGuid().ToString("N");
        await Assert.ThrowsAsync<WorkflowEngineException>(() => client.RunAsync(definition, new Data(), new() { InstanceId = id }));
        var saved = (await store.GetWorkflowInstanceAsync(id))!;
        var checkpoint = ExecutionCheckpoint.Read(StoredCheckpoint.WholeState(saved));
        checkpoint.RootData = WorkflowJson.Deserialize<Data>(saved.WorkflowDataJson.InlineText)!;
        var claimed = Assert.Single(checkpoint.Continuations, c => c.PendingActivity != null);
        Assert.Equal("first", Assert.IsType<Data>(checkpoint.DataOf(claimed)).Text);
        Assert.Equal(1, Assert.Single(checkpoint.Scopes).RecordedOpens);
        store.FailCommit = _ => false;
        var recovered = await client.RecoverAsync(id);
        Assert.True(recovered.Status == WorkflowExecutionStatus.Success, recovered.ErrorMessage);
        Assert.Equal("first", Assert.IsType<Data>(recovered.WorkflowData).Text);
        Assert.Equal(1, probe.Executions);
    }

    private static IWorkflowBuilder<Data, Container> Body(IWorkflowBuilder<Data, object> builder) =>
        builder.Scope<Container>(s => s.Output(x => x.Text).To(c => c.WorkflowData.Text), b => b.Step<Recoverable>());

    [Fact]
    public async Task FailedAffineOpenRetryAndSequentialScopesReuseTheSingleHostSlot()
    {
        var probe = new ScopeActivityTests.Probe { FailuresRemaining = 1 };
        var collection = new ServiceCollection().AddSingleton(probe);
        collection.AddIxIFlow(options => options.MaxScopeThreadsPerHost = 1);
        await using var services = collection.BuildServiceProvider();

        var definition = ScopeActivityTests.Start("ScopeSingleSlot")
            .Scope<ScopeActivityTests.TestScope>(s => s.Threading(ScopeThreading.ThreadAffine).Retry(new RetryPolicy(1)),
                b => b.Step<ScopeActivityTests.Read>())
            .Scope<ScopeActivityTests.TestScope>(s => s.Threading(ScopeThreading.ThreadAffine), b => b.Step<ScopeActivityTests.Read>())
            .Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new ScopeActivityTests.Data())
            .WaitAsync(Patience);

        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(3, probe.OpenThreads.Count);
        Assert.Equal(probe.OpenThreads.First(), probe.OpenThreads.ElementAt(1));
        Assert.Equal(2, probe.Events.Count(e => e == "close:Completed"));
        Assert.Equal(0, services.GetRequiredService<ScopeThreadRegistry>().Count);
    }

    [Fact]
    public async Task SynchronousAffineWorkSeesCancellationWithoutPumpingAndDrainsItsPostedCleanup()
    {
        var scheduler = new ScopeThreadRegistry(1).Create();
        var entry = new ScopeFrameState { Value = new Data(), Scheduler = scheduler };
        var values = new ScopeValues([entry]);
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var posted = false;
        using var notified = new ManualResetEventSlim();

        try
        {
            var task = values.RunAsync(token =>
            {
                Assert.Equal(cancellation.Token, token);
                var thread = Environment.CurrentManagedThreadId;
                var context = SynchronizationContext.Current!;

                using var registration = token.Register(() =>
                {
                    context.Post(_ =>
                    {
                        Assert.Equal(thread, Environment.CurrentManagedThreadId);
                        Assert.Same(entry.Value, values.Find<Data>());
                        posted = true;
                    }, null);

                    notified.Set();
                });

                started.TrySetResult();
                Assert.True(SpinWait.SpinUntil(() => token.IsCancellationRequested, Patience));
                Assert.True(notified.Wait(Patience));

                return Task.FromResult(true);
            }, cancellation.Token);

            await started.Task.WaitAsync(Patience);
            await cancellation.CancelAsync();
            await task.WaitAsync(Patience);
            Assert.True(posted);
            Assert.False(values.Operation.Active);
        }
        finally
        {
            await scheduler.StopAsync();
        }
    }

    [Fact]
    public async Task CancelledQueuedAffineWorkNeverEntersAuthorCode()
    {
        var scheduler = new ScopeThreadRegistry(1).Create();
        using var gate = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var blocker = scheduler.RunAsync(() =>
        {
            started.TrySetResult();
            gate.Wait();

            return Task.FromResult(true);
        });

        try
        {
            await started.Task.WaitAsync(Patience);
            var entered = false;

            var task = new ScopeValues([new() { Scheduler = scheduler }]).RunAsync(_ =>
            {
                entered = true;

                return Task.FromResult(true);
            }, cancellation.Token);

            await cancellation.CancelAsync();
            gate.Set();
            await blocker;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.False(entered);
        }
        finally
        {
            gate.Set();
            await scheduler.StopAsync();
        }
    }

    [Fact]
    public async Task ReopenFailureCrossingAJoinCompensatesBeforeTheOutsideCatch()
    {
        var probe = new ScopeActivityTests.Probe();
        await using var services = ScopeActivityTests.Services(probe, verify: true);

        var definition = ScopeActivityTests.Start("ScopeSagaJoin").Try(outside => outside.Scope<ScopeActivityTests.TestScope>(null,
                body => body.Saga(saga => saga
                        .Step<ScopeActivityTests.Copy>(s =>
                            s.CompensateWith<ScopeActivityTests.Mark>(c => c.Input(x => x.Text).From(c => "undo-first")))
                        .Step<ScopeActivityTests.Copy>(s =>
                            s.CompensateWith<ScopeActivityTests.Mark>(c => c.Input(x => x.Text).From(c => "undo-second"))))
                    .OnError<InvalidOperationException>(h =>
                        h.Step<ScopeActivityTests.Mark>(s => s.Input(x => x.Text).From(c => "handler")))))
            .Catch<InvalidOperationException>(b => b.Step<ScopeActivityTests.Mark>(s => s.Input(x => x.Text).From(c => "outside"))).Build();

        // The runtime supports a join nested in saga state; the saga fluent surface
        // exposes only compensatable activities, so compose that control node directly.
        var saga = definition.Steps[0].SequenceSteps[0].ScopeBodySteps[0];

        saga.SequenceSteps.Add(ScopeActivityTests.Start("join").Parallel(p => p
            .Do(b => b.WaitFor<ScopeActivityTests.Signal>("go").Step<ScopeActivityTests.Read>())
            .Do(b => b.WaitFor<ScopeActivityTests.Signal>("held"))).Build().Steps[0]);

        var client = services.GetRequiredService<IWorkflowClient>();
        var idle = await client.RunAsync(definition, new ScopeActivityTests.Data());
        Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
        probe.FailuresRemaining = 1;
        var done = await client.ResumeAsync(idle.InstanceId, "go", new ScopeActivityTests.Signal()).WaitAsync(Patience);
        Assert.True(done.Status == WorkflowExecutionStatus.Success, done.ErrorMessage);
        Assert.DoesNotContain("handler", probe.Events);
        Assert.Equal(new[] { "undo-second", "undo-first", "outside" }, probe.Events.Where(e => e.StartsWith("undo-") || e == "outside"));
    }

    [Fact]
    public async Task FailingBranchSuppliesItsOriginalErrorToCloseAfterDeferredUnwind()
    {
        var probe = new Probe();
        var collection = new ServiceCollection().AddSingleton(probe);
        collection.AddIxIFlow(options => options.VerifyCheckpointParts = true);
        await using var services = collection.BuildServiceProvider();

        var definition = new WorkflowBuilder<Data, object>([], "ScopeBranchError").Try(b => b.Parallel(p => p
                .Do(x => x.Scope<Container>(null, body => body.Step<Fail>()))
                .Do(x => x.WaitFor<ScopeActivityTests.Signal>("held"))))
            .Catch<InvalidOperationException>(_ =>
            {
            }).Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data()).WaitAsync(Patience);
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        var closing = Assert.Single(probe.Closes);
        Assert.Equal(ScopeCloseReason.Failed, closing.Reason);
        Assert.Same(probe.Error, closing.Error);
    }

    public sealed class Data
    {
        public string Text { get; set; } = "";
    }

    public sealed class Probe
    {
        public bool Executed;
        public int Executions;
        public Exception Error { get; } = new InvalidOperationException("original branch fault");
        public ConcurrentQueue<ScopeClosing> Closes { get; } = new();
    }

    public sealed class Value;

    public sealed class Container(Probe probe) : IScopeActivity<Value>
    {
        [ActivityOutput] public string Text { get; set; } = "";

        public Task<Value> OpenAsync(ScopeOpening opening, IScopeContext context, CancellationToken token)
        {
            Text = opening.Reopen ? "reopened" : "first";

            return Task.FromResult(new Value());
        }

        public Task CloseAsync(Value value, ScopeClosing closing, IScopeContext context, CancellationToken token)
        {
            probe.Closes.Enqueue(closing);

            return Task.CompletedTask;
        }
    }

    public sealed class Recoverable(Probe probe) : IRecoverableActivity<int>
    {
        public int CaptureRecoveryState(IActivityContext context) => 1;

        public Task ExecuteAsync(IActivityContext context, CancellationToken token)
        {
            probe.Executions++;
            probe.Executed = true;

            return Task.CompletedTask;
        }

        public Task<ActivityRecoveryResult<int>> RecoverAsync(int state, IActivityContext context, CancellationToken token) =>
            Task.FromResult<ActivityRecoveryResult<int>>(new ActivityRecoveryResult<int>.Completed());
    }

    public sealed class Fail(Probe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken token) => throw probe.Error;
    }

    [Workflow("ScopeClaimInline")]
    public sealed class Inline : IWorkflow<Data>
    {
        public void Build(IWorkflowBuilder<Data> builder) => builder.Step<ScopeActivityTests.Copy>()
            .Scope<Container>(s => s.Output(x => x.Text).To(c => c.WorkflowData.Text), b => b.Step<Recoverable>());
    }
}