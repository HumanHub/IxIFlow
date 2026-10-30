using System.Reflection;
using System.Runtime.ExceptionServices;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Debugging;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "control-flow"), Trait("Spec", "W-01")]
public sealed class FlowchartEventHandoffTests
{
    [Theory, InlineData(false), InlineData(true)]
    public async Task DebuggerWakeThatTakesAnEventDoesNotLetItsCallerRunTheNextTimeout(bool interrupted)
    {
        var time = new FakeTimeProvider();
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddIxIFlow(options => options.Debugging = true);
        registrations.AddSingleton<TimeProvider>(time);
        registrations.AddSingleton<IWorkflowStateRepository>(new InMemoryWorkflowStateRepository(time));
        registrations.AddBreakpointConditions();
        await using var services = registrations.BuildServiceProvider();

        await FlowchartEventHandoffScenario.VerifyAsync(services, _ =>
        {
            time.Advance(TimeSpan.FromMilliseconds(101));

            return Task.CompletedTask;
        }, interrupted);
    }

    [Fact]
    public async Task EventReceiptNewerThanTheFirstSnapshotReturnsFreshData()
    {
        var store = new InMemoryWorkflowStateRepository();
        var wrapped = DispatchProxy.Create<IWorkflowStateRepository, SnapshotProbe>();
        var probe = (SnapshotProbe)wrapped;
        probe.Store = store;
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddIxIFlow();
        registrations.AddSingleton(wrapped);
        await using var services = registrations.BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<WorkflowHost.InstanceOperations>();

        var definition = new WorkflowBuilder<Data, object>([], "FlowchartEventSnapshot")
            .WaitFor<Data>("go", configure: w => w.Output(e => e.Value).To(c => c.WorkflowData.Value))
            .WaitFor<Data>("answer").Build();

        var idle = await runner.StartAsync(definition, new Data(), new(), default);
        var old = (await store.GetWorkflowInstanceAsync(idle.InstanceId))!;
        await runner.DeliverAsync(idle.InstanceId, "go", new Data { Value = 42 }, "go-event");
        Assert.Equal(WorkflowExecutionStatus.Suspended, (await runner.RecoverAsync(idle.InstanceId, default)).Status);
        probe.Stale = old;
        var result = await runner.TakeEventAsync(idle.InstanceId, "go-event", default);
        Assert.Equal(1, probe.StaleReads);
        Assert.Equal(EventOutcome.Consumed, result.Event!.Outcome);
        Assert.Equal(42, Assert.IsType<Data>(result.WorkflowData).Value);
    }

    public sealed class Data
    {
        public int Value { get; set; }
    }

    public class SnapshotProbe : DispatchProxy
    {
        public IWorkflowStateRepository Store = null!;
        public WorkflowInstance? Stale;
        public int StaleReads;

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IWorkflowStateRepository.GetWorkflowInstanceAsync) &&
                Interlocked.Exchange(ref Stale, null) is { } stale)
            {
                StaleReads++;

                return Task.FromResult<WorkflowInstance?>(stale);
            }

            try
            {
                return method.Invoke(Store, args);
            }
            catch (TargetInvocationException error)
            {
                ExceptionDispatchInfo.Capture(error.InnerException!).Throw();

                throw;
            }
        }
    }
}