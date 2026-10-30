using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
[Trait("Spec", "G-05")]
public sealed class Slice4AllStopTests
{
    [Fact]
    public async Task ACompletedSiblingCannotDispatchItsNextActivityUntilContinue()
    {
        var probe = new Probe();
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow(options => options.Debugging = true);
        services.AddSingleton(probe);
        services.AddSingleton<IBreakpointConditionCompiler>(new ReleaseSiblingAtBreakpoint(probe));
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IWorkflowClient>();
        var operations = provider.GetRequiredService<IWorkflowOperations>();

        var definition = new WorkflowBuilder<Data, object>([], "AllStopSibling")
            .WaitFor<string>("go")
            .Parallel(parallel => parallel
                .Do(sibling => sibling.Step<Block>().Step<Count>())
                .Do(target => target.Step<AwaitSibling>().Step<Noop>().WithId("target")))
            .Build();

        var waiting = await client.RunAsync(definition, new Data());
        await operations.SetBreakpointsAsync(waiting.InstanceId, [new("stop", "target") { Condition = "true" }], "tester");
        var paused = await client.ResumeAsync(waiting.InstanceId, "go", "ready").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowExecutionStatus.Paused, paused.Status);
        Assert.True(probe.Release.Task.IsCompletedSuccessfully);
        Assert.Equal(0, probe.Count);
        var completed = await operations.ContinueAsync(waiting.InstanceId, "tester").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowExecutionStatus.Success, completed.Status);
        Assert.Equal(1, probe.Count);
    }

    public sealed class Data;

    public sealed class Probe
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Count;
    }

    public sealed class Block(Probe probe) : IAsyncActivity
    {
        public async Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.Entered.TrySetResult();
            await probe.Release.Task.WaitAsync(cancellationToken);
        }
    }

    public sealed class AwaitSibling(Probe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) =>
            probe.Entered.Task.WaitAsync(cancellationToken);
    }

    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public sealed class Count(Probe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref probe.Count);

            return Task.CompletedTask;
        }
    }

    private sealed class ReleaseSiblingAtBreakpoint(Probe probe) : IBreakpointConditionCompiler
    {
        public BreakpointConditionCompilation Compile(BreakpointConditionRequest request) => new(
            (_, _, _, _, _) =>
            {
                Assert.True(probe.Entered.Task.IsCompletedSuccessfully);
                probe.Release.TrySetResult();

                return true;
            }, []);
    }
}