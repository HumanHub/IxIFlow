using System.Text.Json.Serialization;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "recovery")]
public sealed class ActivityActivationTests
{
    [Fact]
    public async Task StartingAndRestoringActivitiesInjectDependenciesIntoFreshObjects()
    {
        var store = new InMemoryWorkflowStateRepository();
        var registry = new WorkflowVersionRegistry();
        var firstProbe = new Probe("first host");
        var secondProbe = new Probe("second host");
        var definition = Workflow.Create<Data>("ActivityActivation")
            .Step<InjectedActivity>()
            .If(_ => true, then => then.WaitFor<Approval>("approval"))
            .Step<Capture>(step => step
                .Input(activity => activity.Value).From(ctx => ctx.PreviousStep.Value)
                .Output(activity => activity.Value).To(ctx => ctx.WorkflowData.Value))
            .Build();
        string id;
        await using (var first = Services(firstProbe))
        {
            var started = await first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
            id = started.InstanceId;
            Assert.Equal(1, firstProbe.Executions);
            Assert.NotEmpty(firstProbe.Constructed);
            Assert.All(firstProbe.Constructed, activity => Assert.Same(firstProbe, activity.Dependency));
        }
        await using var second = Services(secondProbe);
        var completed = await second.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "approval", new Approval());
        Assert.True(completed.Status == WorkflowExecutionStatus.Success, completed.ErrorMessage);
        Assert.Equal("first host", Assert.IsType<Data>(completed.WorkflowData).Value);
        Assert.Equal(0, secondProbe.Executions);
        Assert.NotEmpty(secondProbe.Constructed);
        Assert.All(secondProbe.Constructed, activity => Assert.Same(secondProbe, activity.Dependency));
        Assert.DoesNotContain(secondProbe.Constructed, activity => firstProbe.Constructed.Contains(activity));

        ServiceProvider Services(Probe probe) => new ServiceCollection().AddLogging().AddIxIFlow().Services
            .AddSingleton<IWorkflowStateRepository>(store)
            .AddSingleton<IWorkflowVersionRegistry>(registry)
            .AddSingleton(probe).BuildServiceProvider();
    }

    public sealed class Data { public string Value { get; set; } = ""; }
    public sealed class Approval;
    public sealed class Probe(string value)
    {
        public string Value { get; } = value;
        public int Executions { get; set; }
        public List<InjectedActivity> Constructed { get; } = [];
    }

    public sealed class InjectedActivity : IAsyncActivity
    {
        public InjectedActivity(Probe dependency)
        {
            Dependency = dependency;
            dependency.Constructed.Add(this);
        }

        [JsonIgnore] public Probe Dependency { get; }
        public string Value { get; private set; } = "";

        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            Dependency.Executions++;
            Value = Dependency.Value;
            return Task.CompletedTask;
        }
    }

    public sealed class Capture : IAsyncActivity
    {
        public string Value { get; set; } = "";
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
