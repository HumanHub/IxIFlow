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
    public async Task CustomNameIsPreservedWhenLookingUpRegisteredWorkflow(bool explicitDataType)
    {
        var services = new ServiceCollection().AddLogging().AddIxIFlow().Services;
        if (explicitDataType)
            services.RegisterWorkflow<NamedWorkflow, RegistrationData>();
        else
            services.RegisterWorkflow<NamedWorkflow>();

        using var provider = services.BuildServiceProvider();
        var registered = Assert.Single(provider.GetServices<WorkflowDefinition>());

        Assert.Equal("Customer approval", registered.Name);
        Assert.Equal(3, registered.Version);
        var registry = provider.GetRequiredService<IWorkflowCatalog>();
        Assert.Same(registered, await registry.GetWorkflowDefinitionAsync("Customer approval", 3));
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
            services.RegisterWorkflow<NamedWorkflow>();
            return services.BuildServiceProvider();
        }

        string instanceId;
        using (var first = CreateProvider())
        {
            var definition = Assert.IsType<WorkflowDefinition>(await first.GetRequiredService<IWorkflowCatalog>()
                .GetWorkflowDefinitionAsync("Customer approval", 3));
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
        services.RegisterWorkflow<NamedWorkflow>();
        using var provider = services.BuildServiceProvider();
        var definition = Assert.IsType<WorkflowDefinition>(await provider.GetRequiredService<IWorkflowCatalog>()
                .GetWorkflowDefinitionAsync("Customer approval", 3));
        var engine = provider.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(definition, new RegistrationData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        var registry = provider.GetRequiredService<IWorkflowCatalog>();
        await registry.RetireWorkflowVersionAsync(definition.Name, definition.Version);
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
                services.RegisterWorkflow<NamedWorkflow, RegistrationData>();
            else
                services.RegisterWorkflow<NamedWorkflow>();
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
            await second.GetRequiredService<IWorkflowCatalog>()
                .RegisterWorkflowAsync(parent);
            var engine = second.GetRequiredService<IWorkflowClient>();
            EventAssert.Consumed(await engine.ResumeAsync(childId, "approval", new RegistrationEvent()));
            AssertCompleted(await engine.AwaitAsync(instanceId, WorkflowAwaitCondition.Finished)
                .WaitAsync(ChildRuns.Patience));
        }

        var parentInstance = await repository.GetWorkflowInstanceAsync(instanceId);
        Assert.Equal(WorkflowStatus.Completed, parentInstance?.Status);
        var children = await Task.WhenAll((await repository.QueryInstancesAsync(new InstanceQuery(WorkflowName: "Customer approval", Limit: int.MaxValue))).Items.Select(async row => (await repository.GetWorkflowInstanceAsync(row.InstanceId))!));
        var child = Assert.Single(children);
        Assert.Equal(WorkflowStatus.Completed, child.Status);
        Assert.Equal(3, child.WorkflowVersion);
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<RegistrationData>(
            child.WorkflowDataJson)!.Completed);
        using var verification = CreateProvider();
        var registeredStructureHash = new WorkflowScopeCatalog(
            Assert.IsType<WorkflowDefinition>(await verification.GetRequiredService<IWorkflowCatalog>()
                .GetWorkflowDefinitionAsync("Customer approval", 3))).StructureHash;
        Assert.Equal(registeredStructureHash,
            ExecutionCheckpoint.Read(child.ExecutionStateJson).StructureHash);
    }

    private static void AssertCompleted(WorkflowExecutionResult resumed)
    {
        Assert.True(resumed.Status == WorkflowExecutionStatus.Success, resumed.ErrorMessage);
        Assert.True(Assert.IsType<ParentData>(resumed.WorkflowData).ChildCompleted);
    }

    [Fact]
    public async Task RegisteredClassInvocationUsesTheWorkflowClassVersion()
    {
        var services = new ServiceCollection().AddLogging().AddIxIFlow().Services;
        services.RegisterWorkflow<NamedWorkflow>();
        using var provider = services.BuildServiceProvider();
        var parent = IxIFlow.Builders.Workflow.Create<ParentData>("Registered class parent")
            .Step<RegistrationActivity>()
            .Invoke<NamedWorkflow, RegistrationData>(step => step
                .Output(data => data.Completed).To(ctx => ctx.WorkflowData.ChildCompleted))
            .Build();
        var engine = provider.GetRequiredService<IWorkflowClient>();

        var started = await engine.RunAsync(parent, new ParentData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        var repository = provider.GetRequiredService<IWorkflowStateRepository>();
        var childId = await repository.WaitingInstanceAsync("Customer approval", "approval");
        EventAssert.Consumed(await engine.ResumeAsync(childId, "approval", new RegistrationEvent()));
        AssertCompleted(await engine.FinishedAsync(started));
        var child = Assert.Single(await Task.WhenAll((await repository.QueryInstancesAsync(new InstanceQuery(WorkflowName: "Customer approval", Limit: int.MaxValue))).Items.Select(async row => (await repository.GetWorkflowInstanceAsync(row.InstanceId))!)));
        Assert.Equal(3, child.WorkflowVersion);
    }

    [Fact]
    public async Task UnregisteredClassInvocationIsRefusedWithoutConstructingTheWorkflow()
    {
        var constructions = 0;
        var services = new ServiceCollection().AddLogging().AddIxIFlow().Services;
        services.AddTransient<NamedWorkflow>(_ =>
        {
            constructions++;
            return new NamedWorkflow();
        });
        using var provider = services.BuildServiceProvider();
        var parent = IxIFlow.Builders.Workflow.Create<ParentData>("Missing registration parent")
            .Step<RegistrationActivity>().Invoke<NamedWorkflow, RegistrationData>().Build();

        var error = await EngineAssert.FailsAsync(WorkflowErrorKind.ArtifactUnavailable,
            () => provider.GetRequiredService<IWorkflowClient>().RunAsync(parent, new ParentData()));

        Assert.Contains("is not registered", error.Message);
        Assert.Equal(0, constructions);
        Assert.Empty(await Task.WhenAll((await provider.GetRequiredService<IWorkflowStateRepository>().QueryInstancesAsync(new InstanceQuery(WorkflowName: nameof(NamedWorkflow), Limit: int.MaxValue))).Items.Select(async row => (await provider.GetRequiredService<IWorkflowStateRepository>().GetWorkflowInstanceAsync(row.InstanceId))!)));
    }

    [Fact]
    public async Task RegisteredClassMissingFromTheRegistryIsRefused()
    {
        var services = new ServiceCollection().AddLogging().AddIxIFlow().Services;
        services.RegisterWorkflow<NamedWorkflow>();
        services.AddSingleton<IWorkflowCatalog>(new WorkflowCatalog());
        using var provider = services.BuildServiceProvider();
        var parent = IxIFlow.Builders.Workflow.Create<ParentData>("Missing catalog parent")
            .Step<RegistrationActivity>().Invoke<NamedWorkflow, RegistrationData>().Build();

        var error = await EngineAssert.FailsAsync(WorkflowErrorKind.ArtifactUnavailable,
            () => provider.GetRequiredService<IWorkflowClient>().RunAsync(parent, new ParentData()));

        Assert.Contains("Workflow definition not found: Customer approval v3", error.Message);
        Assert.Empty(await Task.WhenAll((await provider.GetRequiredService<IWorkflowStateRepository>().QueryInstancesAsync(new InstanceQuery(WorkflowName: "Customer approval", Limit: int.MaxValue))).Items.Select(async row => (await provider.GetRequiredService<IWorkflowStateRepository>().GetWorkflowInstanceAsync(row.InstanceId))!)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetiredChildAllowsExistingWaitsAndRefusesNewInvocations(bool invokeByName)
    {
        var services = new ServiceCollection().AddLogging().AddIxIFlow().Services;
        services.RegisterWorkflow<NamedWorkflow>();
        using var provider = services.BuildServiceProvider();
        WorkflowDefinition Parent(int version)
        {
            var builder = IxIFlow.Builders.Workflow.Create<ParentData>("Retired child parent", version)
                .Step<RegistrationActivity>();
            return invokeByName
                ? builder.Invoke<RegistrationData>("Customer approval", 3, step => step
                    .Output(data => data.Completed).To(ctx => ctx.WorkflowData.ChildCompleted)).Build()
                : builder.Invoke<NamedWorkflow, RegistrationData>(step => step
                    .Output(data => data.Completed).To(ctx => ctx.WorkflowData.ChildCompleted)).Build();
        }
        var parent = Parent(1);
        var engine = provider.GetRequiredService<IWorkflowClient>();
        var started = await engine.RunAsync(parent, new ParentData());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        var repository = provider.GetRequiredService<IWorkflowStateRepository>();
        var childId = await repository.WaitingInstanceAsync("Customer approval", "approval");
        var catalog = provider.GetRequiredService<IWorkflowCatalog>();
        await catalog.RetireWorkflowVersionAsync(parent.Name, parent.Version);
        var retirement = await Assert.ThrowsAsync<WorkflowEngineException>(() =>
            catalog.RetireWorkflowVersionAsync("Customer approval", 3));
        Assert.Equal(WorkflowErrorKind.InvalidState, retirement.Kind);
        Assert.Contains("unfinished parent", retirement.Message);

        EventAssert.Consumed(await engine.ResumeAsync(childId, "approval", new RegistrationEvent()));
        AssertCompleted(await engine.FinishedAsync(started));
        await catalog.RetireWorkflowVersionAsync("Customer approval", 3);
        var error = await EngineAssert.FailsAsync(WorkflowErrorKind.ArtifactUnavailable,
            () => engine.RunAsync(Parent(2), new ParentData()));
        Assert.Contains("is deactivated", error.Message);
        Assert.Single(await Task.WhenAll((await repository.QueryInstancesAsync(new InstanceQuery(WorkflowName: "Customer approval", Limit: int.MaxValue))).Items.Select(async row => (await repository.GetWorkflowInstanceAsync(row.InstanceId))!)));
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

    [IxIFlow.Core.Workflow("Customer approval", Version = 3)]
    public sealed class NamedWorkflow : IWorkflow<RegistrationData>
    {
        public string Id => "NamedWorkflow";

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
