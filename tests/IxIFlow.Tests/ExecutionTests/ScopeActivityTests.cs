using System.Collections.Concurrent;
using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "scopes"), Trait("Spec", "F-26")]
public sealed class ScopeActivityTests
{
    [Theory, InlineData(false), InlineData(true)]
    public async Task OpensBeforeBodyClosesAfterAndPreservesSavedOutputs(bool affine)
    {
        var probe = new Probe();
        await using var services = Services(probe);

        var definition = Start("ScopeOutputs").Scope<TestScope>(
                s => s.Threading(affine ? ScopeThreading.ThreadAffine : ScopeThreading.AnyThread),
                b => b.Step<Read>(s => s.Output(x => x.Text).To(c => c.WorkflowData.Text)))
            .Step<Copy>(s => s.Input(x => x.Text).From(c => c.PreviousStep.Text).Output(x => x.Text).To(c => c.WorkflowData.After)).Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        var data = Assert.IsType<Data>(result.WorkflowData);
        Assert.Equal("opened", data.Text);
        Assert.Equal("opened", data.After);
        Assert.Equal(new[] { "open:False:0", "read:opened", "close:Completed" }, probe.Events);
        Assert.DoesNotContain("dispose", probe.Events);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task WaitSuspendsAndReopensWithStableIdentityWithoutApplyingOutputsTwice(bool affine)
    {
        var probe = new Probe();
        await using var services = Services(probe, verify: true);

        var definition = Start("ScopeWait").Scope<TestScope>(s => s
                .Threading(affine ? ScopeThreading.ThreadAffine : ScopeThreading.AnyThread)
                .Output(x => x.Text).To(c => c.WorkflowData.Text),
            b => b.Step<Copy>(s => s.Input(x => x.Text).From(c => "body changed")
                .Output(x => x.Text).To(c => c.WorkflowData.Text)).Step<Read>().WaitFor<Signal>("approval").Step<Read>()).Build();

        var client = services.GetRequiredService<IWorkflowClient>();
        var idle = await client.RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
        Assert.Equal(new[] { "open:False:0", "read:opened", "close:Suspended" }, probe.Events);
        var inspection = await services.GetRequiredService<IWorkflowOperations>().InspectAsync(idle.InstanceId);
        Assert.NotNull(inspection);
        var result = await client.ResumeAsync(idle.InstanceId, "approval", new Signal());
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Single(probe.Invocations.Distinct());
        Assert.Contains("open:True:1", probe.Events);
        Assert.Equal("close:Completed", probe.Events.Last());
        Assert.Equal("body changed", Assert.IsType<Data>(result.WorkflowData).Text);

        if (affine)
            Assert.Equal(2, probe.OpenThreadObjects.Distinct().Count());
    }

    [Fact]
    public async Task NearestScopeShadowsAndNestedCloseCanReadItsOuterValue()
    {
        var probe = new Probe();
        await using var services = Services(probe);

        var definition = Start("NestedScope").Scope<TestScope>(s => s.Input(x => x.Text).From(c => "outer"),
            b => b.Scope<TestScope>(s => s.Input(x => x.Text).From(c => "inner"), x => x.Step<Read>()).Step<Read>()).Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);

        Assert.Equal(new[] { "read:inner", "outer:outer", "read:outer" },
            probe.Events.Where(e => e.StartsWith("read:") || e.StartsWith("outer:")));
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task FailureAndCancellationUnwindBeforeOutsideCleanup(bool cancel)
    {
        var probe = new Probe();
        await using var services = Services(probe);

        var definition = Start("ScopeUnwind").Try(b => b.Scope<TestScope>(null, x =>
            {
                if (cancel)
                    x.WaitFor<Signal>("hold");
                else
                    x.Step<Fail>();
            })).Catch<InvalidOperationException>(b => b.Step<Mark>(s => s.Input(x => x.Text).From(c => "catch")))
            .Finally(b => b.Step<Mark>(s => s.Input(x => x.Text).From(c => "finally"))).Build();

        var client = services.GetRequiredService<IWorkflowClient>();
        var result = await client.RunAsync(definition, new Data());

        if (cancel)
            result = await client.CancelAsync(result.InstanceId, new CancellationReason { ReasonCode = "test" });

        Assert.Equal(cancel ? WorkflowExecutionStatus.Cancelled : WorkflowExecutionStatus.Success, result.Status);
        Assert.Contains(cancel ? "close:Suspended" : "close:Failed", probe.Events);

        if (!cancel)
            Assert.True(Array.IndexOf(probe.Events.ToArray(), "close:Failed") < Array.IndexOf(probe.Events.ToArray(), "catch"));

        Assert.Equal("finally", probe.Events.Last());
    }

    [Fact]
    public async Task ScopeAroundTryRemainsInForceDuringFinally()
    {
        var probe = new Probe();
        await using var services = Services(probe);

        var definition = Start("ScopeFinally").Scope<TestScope>(null, b => b.Try(x => x.Step<Fail>())
            .Catch<InvalidOperationException>(_ =>
            {
            }).Finally(x => x.Step<Read>())).Build();

        Assert.Equal(WorkflowExecutionStatus.Success,
            (await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data())).Status);

        Assert.Equal(new[] { "open:False:0", "read:opened", "close:Completed" }, probe.Events);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task ParallelBranchesInheritTheSameValueAndSerializedModeHonorsCap(bool affine)
    {
        var probe = new Probe();
        await using var services = Services(probe, cap: 4);

        var definition = Start("ScopeFork").Scope<TestScope>(
            s => s.Threading(affine ? ScopeThreading.ThreadAffine : ScopeThreading.AnyThread),
            b => b.Parallel(p => p.Do(x => x.Step<Read>()).Do(x => x.Step<Read>()))).Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(2, probe.Events.Count(e => e == "read:opened"));
        Assert.Equal(1, probe.Maximum);

        if (affine)
            Assert.Single(probe.CallbackThreads.Distinct());
    }

    [Fact]
    public async Task SharedScopesAdmitConcurrentAsyncOperations()
    {
        var probe = new Probe { SharedBarrier = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var services = Services(probe);

        var definition = Start("SharedScope").Scope<SharedScope>(null,
            b => b.Parallel(p => p.Do(x => x.Step<Read>()).Do(x => x.Step<Read>()))).Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data())
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(2, probe.Maximum);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task ForEachAndSwitchBodiesSeeTheEnclosingScope(bool parallel)
    {
        var probe = new Probe();
        await using var services = Services(probe, verify: true);

        var definition = Start("ScopeItems").Scope<TestScope>(null, b => b
            .ForEach(c => c.WorkflowData.Items, (x, item) => x.Switch(c => c.Item(item), cases => cases
                .Case(1, y => y.Step<Read>()).Default(y => y.Step<Read>())), options =>
            {
                if (parallel)
                    options.Parallel();
            })).Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Equal(2, probe.Events.Count(e => e == "read:opened"));
    }

    [Fact]
    public async Task InlineRequirementInheritsAndIndependentInvocationIsRefused()
    {
        var probe = new Probe();
        await using var services = Services(probe);
        var definition = Start("ScopeInline").Scope<TestScope>(null, b => b.Use<Requiring, Data>()).Build();
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Contains("read:opened", probe.Events);
        Assert.Throws<WorkflowEngineException>(() => Start("ScopeInvoke").Invoke<Requiring, Data>().Build());
        var root = Workflow.Create<Data>("RequiringRoot").RequiresScope<Resource>().Step<Read>().Build();

        var rejected =
            await Assert.ThrowsAsync<WorkflowEngineException>(() =>
                services.GetRequiredService<IWorkflowClient>().RunAsync(root, new Data()));

        Assert.Equal(WorkflowErrorKind.InvalidRequest, rejected.Kind);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task ScopeEnclosesGraphAndGraphBodyScopeClosesBeforeNextNode(bool inner)
    {
        var probe = new Probe();
        await using var services = Services(probe);
        var start = Start("ScopeGraph");

        var definition = inner
            ? start.Flowchart(f => f.Start("a").Node("a", b => b.Scope<TestScope>(null, x => x.Step<Read>()), e => e.Always("b"))
                .Node("b", b => b.Step<Mark>(s => s.Input(x => x.Text).From(c => "node-b")))).Build()
            : start.Scope<TestScope>(null, b => b.Flowchart(f => f.Start("a").Node("a", x => x.Step<Read>()))).Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);

        if (inner)
            Assert.True(Array.IndexOf(probe.Events.ToArray(), "close:Completed") < Array.IndexOf(probe.Events.ToArray(), "node-b"));
    }

    [Fact]
    public async Task StateMachineReopensOnlyWhenAuthorWorkRuns()
    {
        var probe = new Probe();
        await using var services = Services(probe);

        var definition = Start("ScopeMachine").Scope<TestScope>(null, b => b.StateMachine(m => m.Start("a")
            .State("a", s => s.Entry(x => x.Step<Read>()).On<Signal>("go", t => t.When(c => true, "end"))))).Build();

        var client = services.GetRequiredService<IWorkflowClient>();
        var idle = await client.RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
        var done = await client.ResumeAsync(idle.InstanceId, "go", new Signal());
        Assert.True(done.Status == WorkflowExecutionStatus.Success, done.ErrorMessage);
        Assert.Single(probe.Invocations);
    }

    [Fact]
    public async Task MissingDynamicLookupFailsTheStepAndOptionalLookupReturnsNull()
    {
        var probe = new Probe();
        await using var services = Services(probe);

        var definition = Start("MissingScope").Try(b => b.Step<Optional>().Step<Read>())
            .Catch<InvalidOperationException>(b => b.Step<Mark>(s => s.Input(x => x.Text).From(c => "caught"))).Build();

        Assert.Equal(WorkflowExecutionStatus.Success,
            (await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data())).Status);

        Assert.Contains("caught", probe.Events);
    }

    [Theory, InlineData(false), InlineData(true)]
    public async Task OpenAndCompletedCloseFailuresAreCatchable(bool close)
    {
        var probe = new Probe { FailOpen = !close, FailClose = close };
        await using var services = Services(probe);

        var definition = Start("ScopeLifecycleFailure").Try(b => b.Scope<TestScope>(null, x => x.Step<Read>()))
            .Catch<InvalidOperationException>(b => b.Step<Mark>(s => s.Input(x => x.Text).From(c => "caught"))).Build();

        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.True(result.Status == WorkflowExecutionStatus.Success, result.ErrorMessage);
        Assert.Contains("caught", probe.Events);

        if (close)
            Assert.Contains("dispose", probe.Events);
    }

    internal static WorkflowBuilder<Data, object> Start(string name) => new([], name);

    internal static ServiceProvider Services(Probe probe, int cap = 100, bool verify = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton(probe);

        services.AddIxIFlow(options =>
        {
            options.MaxActivitiesPerRun = cap;
            options.VerifyCheckpointParts = verify;
        });

        return services.BuildServiceProvider();
    }

    public sealed class Data
    {
        public int Zero { get; set; }
        public string Text { get; set; } = "";
        public string After { get; set; } = "";
        public List<int> Items { get; set; } = [1, 2];
    }

    public sealed class Signal;

    public sealed class Probe
    {
        public ConcurrentQueue<string> Events { get; } = new();
        public ConcurrentQueue<string> Invocations { get; } = new();
        public ConcurrentQueue<int> OpenThreads { get; } = new();
        public ConcurrentQueue<Thread> OpenThreadObjects { get; } = new();
        public ConcurrentQueue<int> CallbackThreads { get; } = new();
        public TaskCompletionSource? SharedBarrier { get; init; }
        public bool FailOpen { get; set; }
        public int FailuresRemaining;
        public bool FailClose { get; set; }
        public int Active;
        public int Maximum;
    }

    public sealed class Resource(string name, Probe probe) : IDisposable
    {
        public string Name { get; } = name;
        public void Dispose() => probe.Events.Enqueue("dispose");
    }

    public class TestScope(Probe probe) : IScopeActivity<Resource>
    {
        public string Text { get; set; } = "";

        public async Task<Resource> OpenAsync(ScopeOpening opening, IScopeContext context, CancellationToken cancellationToken)
        {
            probe.Events.Enqueue($"open:{opening.Reopen}:{opening.RecordedOpens}");
            probe.Invocations.Enqueue(context.InvocationId);
            probe.OpenThreads.Enqueue(Environment.CurrentManagedThreadId);
            probe.OpenThreadObjects.Enqueue(Thread.CurrentThread);
            probe.CallbackThreads.Enqueue(Environment.CurrentManagedThreadId);
            await Task.Yield();
            probe.CallbackThreads.Enqueue(Environment.CurrentManagedThreadId);

            if (probe.FailOpen || probe.FailuresRemaining > 0 && Interlocked.Decrement(ref probe.FailuresRemaining) >= 0)
                throw new InvalidOperationException("open failed");

            Text = Text.Length == 0 ? "opened" : Text;

            return new(Text, probe);
        }

        public async Task CloseAsync(Resource value, ScopeClosing closing, IScopeContext context, CancellationToken cancellationToken)
        {
            probe.CallbackThreads.Enqueue(Environment.CurrentManagedThreadId);
            await Task.Yield();
            probe.CallbackThreads.Enqueue(Environment.CurrentManagedThreadId);

            if (context.ScopeOrDefault<Resource>() is { } outer)
                probe.Events.Enqueue("outer:" + outer.Name);

            probe.Events.Enqueue("close:" + closing.Reason);
            Text = "closed";

            if (probe.FailClose)
                throw new InvalidOperationException("close failed");
        }
    }

    [ScopeConcurrency(ScopeConcurrency.Shared)]
    public sealed class SharedScope(Probe probe) : TestScope(probe);

    public sealed class Read(Probe probe) : IAsyncActivity
    {
        public string Text { get; set; } = "";

        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref probe.Active);
            InterlockedExtensions.Max(ref probe.Maximum, active);

            try
            {
                probe.CallbackThreads.Enqueue(Environment.CurrentManagedThreadId);

                if (probe.SharedBarrier != null)
                {
                    if (active == 2)
                        probe.SharedBarrier.TrySetResult();

                    await probe.SharedBarrier.Task.WaitAsync(cancellationToken);
                }

                await Task.Yield();
                probe.CallbackThreads.Enqueue(Environment.CurrentManagedThreadId);
                Text = context.Scope<Resource>().Name;
                probe.Events.Enqueue("read:" + Text);
            }
            finally
            {
                Interlocked.Decrement(ref probe.Active);
            }
        }
    }

    public sealed class Optional : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken)
        {
            Assert.Null(context.ScopeOrDefault<Resource>());

            return Task.CompletedTask;
        }
    }

    public sealed class Copy : IAsyncActivity
    {
        public string Text { get; set; } = "";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed class Mark(Probe probe) : IAsyncActivity
    {
        public string Text { get; set; } = "";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken)
        {
            probe.Events.Enqueue(Text);

            return Task.CompletedTask;
        }
    }

    public sealed class Fail : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("body failed");
    }

    [RequiresScope<Resource>]
    public sealed class RequiredRead(Probe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken)
        {
            probe.Events.Enqueue("read:" + context.Scope<Resource>().Name);

            return Task.CompletedTask;
        }
    }

    [Workflow("ScopeRequiring")]
    public sealed class Requiring : IWorkflow<Data>
    {
        public void Build(IWorkflowBuilder<Data> builder) => builder.RequiresScope<Resource>().Step<RequiredRead>();
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int target, int value)
        {
            int current;

            do
            {
                current = Volatile.Read(ref target);

                if (current >= value)
                    return;
            } while (Interlocked.CompareExchange(ref target, value, current) != current);
        }
    }
}