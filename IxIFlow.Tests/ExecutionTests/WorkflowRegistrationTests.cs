using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

public sealed class WorkflowRegistrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CustomNameIsPreservedWhenLookingUpRegisteredWorkflow(bool explicitDataType)
    {
        var services = new ServiceCollection().AddLogging().AddIxIFlow();
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
            var started = await first.GetRequiredService<IWorkflowEngine>()
                .ExecuteWorkflowAsync(definition, new RegistrationData());
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
            instanceId = started.InstanceId;
        }

        using var second = CreateProvider();
        var resumed = await second.GetRequiredService<IWorkflowEngine>()
            .ResumeWorkflowAsync(instanceId, "approval", new RegistrationEvent());

        Assert.True(resumed.Status == WorkflowExecutionStatus.Success, resumed.ErrorMessage);
    }

    [Fact]
    public async Task DeactivatingAWorkflowVersionKeepsExistingWaitsResumable()
    {
        var services = new ServiceCollection().AddLogging().AddIxIFlow();
        services.RegisterWorkflow<NamedWorkflow>("Customer approval");
        using var provider = services.BuildServiceProvider();
        var definition = provider.GetWorkflowDefinition<NamedWorkflow>();
        var engine = provider.GetRequiredService<IWorkflowEngine>();
        var started = await engine.ExecuteWorkflowAsync(definition, new RegistrationData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        var registry = provider.GetRequiredService<IWorkflowVersionRegistry>();
        await registry.DeactivateWorkflowVersionAsync(definition.Name, definition.Version);
        var deactivated = await registry.GetWorkflowDefinitionAsync(definition.Name,
            definition.Version);
        Assert.NotNull(deactivated);
        Assert.Equal(definition.Id, deactivated.Id);
        Assert.Equal(definition.Steps.Count, deactivated.Steps.Count);

        var existingStart = await engine.ExecuteWorkflowAsync(definition,
            new RegistrationData(), new WorkflowOptions { InstanceId = started.InstanceId });
        Assert.Equal(WorkflowExecutionStatus.Suspended, existingStart.Status);

        var resumed = await engine.ResumeWorkflowAsync(started.InstanceId, "approval",
            new RegistrationEvent());
        Assert.True(resumed.Status == WorkflowExecutionStatus.Success, resumed.ErrorMessage);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.ExecuteWorkflowAsync(definition, new RegistrationData()));
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
        using (var first = CreateProvider())
        {
            var started = await first.GetRequiredService<IWorkflowEngine>()
                .ExecuteWorkflowAsync(parent, new ParentData());
            Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
            instanceId = started.InstanceId;
            if (!restartBeforeResume)
            {
                var resumedWithoutRestart = await first.GetRequiredService<IWorkflowEngine>()
                    .ResumeWorkflowAsync(instanceId, "approval", new RegistrationEvent());
                AssertCompleted(resumedWithoutRestart);
            }
        }

        if (restartBeforeResume)
        {
            using var second = CreateProvider();
            await second.GetRequiredService<IWorkflowVersionRegistry>()
                .RegisterWorkflowAsync(parent);
            var resumed = await second.GetRequiredService<IWorkflowEngine>()
                .ResumeWorkflowAsync(instanceId, "approval", new RegistrationEvent());
            AssertCompleted(resumed);
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
        using var provider = new ServiceCollection().AddLogging().AddIxIFlow()
            .BuildServiceProvider();
        var parent = IxIFlow.Builders.Workflow.Create<ParentData>("Unregistered class parent")
            .Step<RegistrationActivity>()
            .Invoke<NamedWorkflow, RegistrationData>(step => step
                .Output(data => data.Completed).To(ctx => ctx.WorkflowData.ChildCompleted))
            .Build();
        var engine = provider.GetRequiredService<IWorkflowEngine>();

        var started = await engine.ExecuteWorkflowAsync(parent, new ParentData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        var resumed = await engine.ResumeWorkflowAsync(started.InstanceId, "approval",
            new RegistrationEvent());
        AssertCompleted(resumed);
        var repository = provider.GetRequiredService<IWorkflowStateRepository>();
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
