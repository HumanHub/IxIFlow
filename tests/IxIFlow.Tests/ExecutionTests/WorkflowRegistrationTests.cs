using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "registration")]
public sealed class WorkflowRegistrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CustomNameIsPreservedWhenLookingUpRegisteredWorkflow(bool explicitDataType)
    {
        var services = new ServiceCollection().AddLogging().AddIxIFlow().Services;
        if (explicitDataType)
            services.RegisterWorkflow<NamedWorkflow, RegistrationData>("Customer approval");
        else
            services.RegisterWorkflow<NamedWorkflow>("Customer approval");

        using var provider = services.BuildServiceProvider();
        var registered = Assert.Single(provider.GetServices<WorkflowDefinition>());

        Assert.Equal("Customer approval", registered.Name);
        Assert.Equal(3, registered.Version);
        Assert.Same(registered, provider.GetWorkflowDefinition<NamedWorkflow>());
        Assert.Same(registered,
            provider.GetWorkflowDefinition<NamedWorkflow, RegistrationData>());
    }

    [Fact]
    public async Task RegisteredWorkflowResumesInANewProviderWithoutAnotherStart()
    {
        var repository = new InMemoryWorkflowStateRepository();

        ServiceProvider CreateProvider()
        {
            var services = new ServiceCollection().AddLogging();
            services.AddSingleton<IWorkflowStateRepository>(repository);
            services.AddIxIFlow();
            services.RegisterWorkflow<NamedWorkflow>("Customer approval");
            return services.BuildServiceProvider();
        }

        string instanceId;
        using (var first = CreateProvider())
        {
            var definition = first.GetWorkflowDefinition<NamedWorkflow>();
            var started = await first.GetRequiredService<IWorkflowClient>()
                .RunAsync(definition, new RegistrationData());
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
            instanceId = started.InstanceId;
        }

        using var second = CreateProvider();
        var resumed = await second.GetRequiredService<IWorkflowClient>()
            .ResumeAsync(instanceId, "approval", new RegistrationEvent());

        Assert.True(resumed.Status == WorkflowExecutionStatus.Success, resumed.ErrorMessage);
    }

    [Fact]
    public async Task DeactivatingAWorkflowVersionKeepsExistingWaitsResumable()
    {
        var services = new ServiceCollection().AddLogging().AddIxIFlow().Services;
        services.RegisterWorkflow<NamedWorkflow>("Customer approval");
        using var provider = services.BuildServiceProvider();
        var definition = provider.GetWorkflowDefinition<NamedWorkflow>();
        var engine = provider.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(definition, new RegistrationData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        var registry = provider.GetRequiredService<IWorkflowVersionRegistry>();
        await registry.DeactivateWorkflowVersionAsync(definition.Name, definition.Version);
        var deactivated = await registry.GetWorkflowDefinitionAsync(definition.Name,
            definition.Version);
        Assert.NotNull(deactivated);
        Assert.Equal(definition.Id, deactivated.Id);
        Assert.Equal(definition.Steps.Count, deactivated.Steps.Count);

        var existingStart = await engine.RunAsync(definition,
            new RegistrationData(), new WorkflowOptions { InstanceId = started.InstanceId });
        Assert.Equal(WorkflowExecutionStatus.Suspended, existingStart.Status);

        var resumed = await engine.ResumeAsync(started.InstanceId, "approval",
            new RegistrationEvent());
        Assert.True(resumed.Status == WorkflowExecutionStatus.Success, resumed.ErrorMessage);

        await EngineAssert.FailsAsync(WorkflowErrorKind.ArtifactUnavailable, () =>
            engine.RunAsync(definition, new RegistrationData()));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task ChildInvocationByClassAndNameUsesTheRegisteredDefinition(
        bool explicitDataType, bool invokeByName, bool restartBeforeResume)
    {
        var repository = new InMemoryWorkflowStateRepository();
        ServiceProvider CreateProvider()
        {
            var services = new ServiceCollection().AddLogging();
            services.AddSingleton<IWorkflowStateRepository>(repository);
            services.AddIxIFlow();
            if (explicitDataType)
                services.RegisterWorkflow<NamedWorkflow, RegistrationData>("Customer approval");
            else
                services.RegisterWorkflow<NamedWorkflow>("Customer approval");
            return services.BuildServiceProvider();
        }

        var parentBuilder = IxIFlow.Builders.Workflow.Create<ParentData>(
                $"Parent-{explicitDataType}-{invokeByName}")
            .Step<RegistrationActivity>();
        var parent = invokeByName
            ? parentBuilder.Invoke<RegistrationData>("Customer approval", 3, step => step
                .Output(data => data.Completed).To(ctx => ctx.WorkflowData.ChildCompleted)).Build()
            : parentBuilder.Invoke<NamedWorkflow, RegistrationData>(step => step
                .Output(data => data.Completed).To(ctx => ctx.WorkflowData.ChildCompleted)).Build();
        string instanceId;
        string childId;
        using (var first = CreateProvider())
        {
            var started = await first.GetRequiredService<IWorkflowClient>()
                .RunAsync(parent, new ParentData());
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
            instanceId = started.InstanceId;
            // The child takes its own approval (D-17).
            childId = await repository.WaitingInstanceAsync("Customer approval", "approval");
            if (!restartBeforeResume)
            {
                var engine = first.GetRequiredService<IWorkflowClient>();
                EventAssert.Consumed(await engine.ResumeAsync(childId, "approval", new RegistrationEvent()));
                AssertCompleted(await engine.FinishedAsync(started));
            }
        }

        if (restartBeforeResume)
        {
            using var second = CreateProvider();
            await second.GetRequiredService<IWorkflowVersionRegistry>()
                .RegisterWorkflowAsync(parent);
            var engine = second.GetRequiredService<IWorkflowClient>();
            EventAssert.Consumed(await engine.ResumeAsync(childId, "approval", new RegistrationEvent()));
            AssertCompleted(await engine.AwaitAsync(instanceId, WorkflowAwaitCondition.Finished)
                .WaitAsync(ChildRuns.Patience));
        }

        var parentInstance = await repository.GetWorkflowInstanceAsync(instanceId);
        Assert.Equal(WorkflowStatus.Completed, parentInstance?.Status);
        var children = await repository.GetWorkflowInstancesByNameAsync("Customer approval");
        var child = Assert.Single(children);
        Assert.Equal(WorkflowStatus.Completed, child.Status);
        Assert.Equal(3, child.WorkflowVersion);
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<RegistrationData>(
            child.WorkflowDataJson)!.Completed);
        using var verification = CreateProvider();
        var registeredFingerprint = new WorkflowScopeCatalog(
            verification.GetWorkflowDefinition<NamedWorkflow>()).Fingerprint;
        Assert.Equal(registeredFingerprint,
            ExecutionCheckpoint.Read(child.ExecutionStateJson).DefinitionFingerprint);
    }

    private static void AssertCompleted(WorkflowExecutionResult resumed)
    {
        Assert.True(resumed.Status == WorkflowExecutionStatus.Success, resumed.ErrorMessage);
        Assert.True(Assert.IsType<ParentData>(resumed.WorkflowData).ChildCompleted);
    }

    [Fact]
    public async Task UnregisteredClassInvocationUsesTheWorkflowClassVersion()
    {
        using var provider = new ServiceCollection().AddLogging().AddIxIFlow().Services
            .BuildServiceProvider();
        var parent = IxIFlow.Builders.Workflow.Create<ParentData>("Unregistered class parent")
            .Step<RegistrationActivity>()
            .Invoke<NamedWorkflow, RegistrationData>(step => step
                .Output(data => data.Completed).To(ctx => ctx.WorkflowData.ChildCompleted))
            .Build();
        var engine = provider.GetRequiredService<IWorkflowClient>();

        var started = await engine.RunAsync(parent, new ParentData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        var repository = provider.GetRequiredService<IWorkflowStateRepository>();
        var childId = await repository.WaitingInstanceAsync(nameof(NamedWorkflow), "approval");
        EventAssert.Consumed(await engine.ResumeAsync(childId, "approval", new RegistrationEvent()));
        AssertCompleted(await engine.FinishedAsync(started));
        var child = Assert.Single(await repository.GetWorkflowInstancesByNameAsync(
            nameof(NamedWorkflow)));
        Assert.Equal(3, child.WorkflowVersion);
    }

    public sealed class RegistrationData
    {
        public bool Completed { get; set; }
    }
    public sealed class ParentData
    {
        public bool ChildCompleted { get; set; }
    }
    public sealed class RegistrationEvent;

    public sealed class NamedWorkflow : IWorkflow<RegistrationData>
    {
        public string Id => "NamedWorkflow";
        public int Version => 3;

        public void Build(IWorkflowBuilder<RegistrationData> builder) =>
            builder.Step<RegistrationActivity>()
                .WaitFor<RegistrationEvent>("approval")
                .Step<CompleteActivity>(step => step
                    .Output(activity => activity.Completed).To(ctx => ctx.WorkflowData.Completed));
    }

    public sealed class RegistrationActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public sealed class CompleteActivity : IAsyncActivity
    {
        public bool Completed { get; private set; }

        public Task ExecuteAsync(IActivityContext context,
            CancellationToken cancellationToken = default)
        {
            Completed = true;
            return Task.CompletedTask;
        }
    }
}
