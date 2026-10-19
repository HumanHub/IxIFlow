using IxIFlow.Tests.Infrastructure;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "authoring")]
public sealed class DefinitionValidationTests
{
    [Theory]
    [InlineData("build")]
    [InlineData("validation")]
    [InlineData("registration")]
    [InlineData("start with options")]
    [InlineData("start with defaults")]
    public async Task HandBuiltStepsWithoutRuntimeAreInvalidAtTheirSource(string entry)
    {
        var definition = new WorkflowDefinition
        {
            Version = 1,
            Name = "Hand built", WorkflowDataType = typeof(RegistrationData),
            Steps = [new WorkflowStep
            {
                Id = "hand-built", StepType = WorkflowStepType.Activity,
                ActivityType = typeof(RegistrationActivity), WorkflowDataType = typeof(RegistrationData),
                SourceLocation = "hand-built.yaml:17"
            }]
        };
        Assert.NotEmpty(new WorkflowScopeCatalog(definition).StructureHash);
        using var provider = new ServiceCollection().AddLogging().AddIxIFlow().Services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IWorkflowCatalog>();
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() =>
        {
            switch (entry)
            {
                case "build":
                    new WorkflowBuilder<RegistrationData, object>(definition.Steps, definition.Name).Build();
                    return Task.CompletedTask;
                case "validation":
                    WorkflowDefinitionValidator.Validate(definition);
                    return Task.CompletedTask;
                case "registration":
                    return registry.RegisterWorkflowAsync(definition);
                default:
                    return provider.GetRequiredService<IWorkflowClient>().RunAsync(definition, new RegistrationData(),
                        entry == "start with options" ? new WorkflowOptions() : null);
            }
        });
        Assert.Null(await registry.GetWorkflowDefinitionAsync(definition.Name, definition.Version));
        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
        Assert.StartsWith("hand-built.yaml:17:", error.Message);
        Assert.Contains("no runtime delegates", error.Message);
    }

    [Fact]
    public void CatalogsAndExecutionReuseDefinitionRulesWithoutRevisitingPurity()
    {
        var visits = new CountingCondition();
        var step = new WorkflowStep
        {
            Id = "condition", SourceLocation = "purity.yaml:4",
            StepType = WorkflowStepType.Conditional, WorkflowDataType = typeof(RegistrationData),
            Runtime = WorkflowStepDelegates.Create<RegistrationData>(),
            ConditionExpression = System.Linq.Expressions.Expression.Lambda<Func<bool>>(visits)
        };
        var definition = new WorkflowDefinition { Name = "Hand-built fixture", Version = 1, WorkflowDataType = typeof(RegistrationData), Steps = [step] };
        Assert.NotEmpty(new WorkflowScopeCatalog(definition).StructureHash);
        var unvalidated = Assert.Throws<WorkflowEngineException>(() => WorkflowDefinitionValidator.ForExecution(definition));
        Assert.Equal(WorkflowErrorKind.InvalidDefinition, unvalidated.Kind);
        Assert.StartsWith("purity.yaml:4:", unvalidated.Message);
        Assert.Equal(0, visits.Visits);
        System.Threading.Tasks.Parallel.For(0, 20, _ => WorkflowDefinitionValidator.Validate(definition));
        var scopes = WorkflowDefinitionValidator.ForExecution(definition);
        var count = visits.Visits;
        Assert.Equal(1, count);
        System.Threading.Tasks.Parallel.For(0, 20, _ =>
        {
            WorkflowDefinitionValidator.Validate(definition);
            _ = new WorkflowScopeCatalog(definition).StructureHash.Length;
            Assert.Same(scopes, WorkflowDefinitionValidator.ForExecution(definition));
            new WorkflowCatalog([definition]);
        });
        Assert.Equal(count, visits.Visits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChildResolutionRequiresValidationEvenWhenRegistrationIsSkipped(bool starting)
    {
        using var provider = new ServiceCollection().AddLogging().AddIxIFlow().Services.BuildServiceProvider();
        var built = Workflow.Create<RegistrationData>("Child source").Step<RegistrationActivity>().Build();
        var definition = new WorkflowDefinition
        {
            Version = 1,
            Name = "Unchecked child", WorkflowDataType = typeof(RegistrationData), Steps = built.Steps
        };
        var children = new ChildWorkflows(provider, new UncheckedRegistry(definition), TimeProvider.System);
        var invocation = new WorkflowStep { WorkflowName = definition.Name, WorkflowVersion = definition.Version };
        Assert.NotEmpty(new WorkflowScopeCatalog(definition).StructureHash);

        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => children.ResolveAsync(invocation, starting));

        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
        Assert.Contains("has not been validated", error.Message);
        WorkflowDefinitionValidator.Validate(definition);
        Assert.Same(definition, await children.ResolveAsync(invocation, starting));
    }

    [Fact]
    public async Task RecoveryRefusesAnUnvalidatedRegistryDefinitionWithoutRunningRules()
    {
        using var provider = new ServiceCollection().AddLogging().AddIxIFlow().Services.BuildServiceProvider();
        var visits = new CountingCondition();
        var definition = new WorkflowDefinition
        {
            Version = 1,
            Name = "Unchecked recovery", WorkflowDataType = typeof(RegistrationData),
            Steps = [new WorkflowStep
            {
                Id = "condition", SourceLocation = "recovery.yaml:9", StepType = WorkflowStepType.Conditional,
                WorkflowDataType = typeof(RegistrationData), Runtime = WorkflowStepDelegates.Create<RegistrationData>(),
                ConditionExpression = System.Linq.Expressions.Expression.Lambda<Func<bool>>(visits)
            }]
        };
        var checkpoint = new ExecutionCheckpoint
        {
            StructureHash = new WorkflowScopeCatalog(definition).StructureHash,
            Continuations = [new ContinuationState { Stack = [new ScopePosition { ScopeId = "root" }] }]
        };
        var repository = new InMemoryWorkflowStateRepository();
        var instance = new WorkflowInstance
        {
            InstanceId = "unchecked-recovery", WorkflowName = definition.Name, WorkflowVersion = definition.Version,
            Status = WorkflowStatus.Running, ExecutionStateJson = System.Text.Json.JsonSerializer.Serialize(checkpoint)
        };
        await repository.SeedAsync(instance);
        var operations = new WorkflowHost.InstanceOperations(provider, provider.GetRequiredService<IActivityExecutor>(),
            repository, new UncheckedRegistry(definition), new InProcessInstanceGate());

        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => operations.RecoverAsync(instance.InstanceId,
            CancellationToken.None));

        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
        Assert.StartsWith("recovery.yaml:9:", error.Message);
        Assert.Contains("has not been validated", error.Message);
        Assert.Equal(0, visits.Visits);
        Assert.Equal(instance.ExecutionStateJson, (await repository.GetWorkflowInstanceAsync(instance.InstanceId))!.ExecutionStateJson);
    }

    // Models a custom registry that returns definitions without invoking the engine validator.
    private sealed class UncheckedRegistry(WorkflowDefinition definition) : IWorkflowCatalog
    {
        private readonly WorkflowCatalog _catalog = UncheckedCatalog(definition);
        private static WorkflowCatalog UncheckedCatalog(WorkflowDefinition definition)
        {
            var store = new InMemoryWorkflowStateRepository();
            store.RegisterWorkflowVersionAsync(new(definition.Name, definition.Version,
                new WorkflowScopeCatalog(definition).StructureHash)).GetAwaiter().GetResult();
            return new WorkflowCatalog(store);
        }
        public Task<WorkflowDraft?> GetDefinitionAsync(string name, int version) => _catalog.GetDefinitionAsync(name, version);
        public Task<WorkflowDraft> SaveDefinitionAsync(WorkflowDraft definition, long expectedRevision) => _catalog.SaveDefinitionAsync(definition, expectedRevision);
        public Task RegisterWorkflowVersionAsync(WorkflowVersion version, IArtifactStore? artifacts = null) => _catalog.RegisterWorkflowVersionAsync(version, artifacts);
        public Task<WorkflowVersion?> GetWorkflowVersionAsync(string name, int version) => _catalog.GetWorkflowVersionAsync(name, version);
        public Task<IReadOnlyList<WorkflowVersion>> GetWorkflowVersionsAsync(string? name = null) => _catalog.GetWorkflowVersionsAsync(name);
        public Task SetWorkflowVersionStateAsync(string name, int version, WorkflowVersionState state, IArtifactStore? artifacts = null) => _catalog.SetWorkflowVersionStateAsync(name, version, state, artifacts);
        public Task<int> PurgeArtifactsAsync(IArtifactStore artifacts, TimeSpan retention, int batchSize, CancellationToken cancellationToken = default) => _catalog.PurgeArtifactsAsync(artifacts, retention, batchSize, cancellationToken);
        public Task<WorkflowVersion?> GetLatestWorkflowVersionAsync(string name) => _catalog.GetLatestWorkflowVersionAsync(name);
        public Task<IReadOnlyList<WorkflowDefinition>> GetWorkflowDefinitionsAsync() =>
            Task.FromResult<IReadOnlyList<WorkflowDefinition>>([definition]);
        public Task RegisterWorkflowAsync(WorkflowDefinition value) => Task.CompletedTask;
        public Task<WorkflowDefinition?> GetWorkflowDefinitionAsync(string name, int version) => Task.FromResult<WorkflowDefinition?>(definition);
        // Computed without validation, as the rest of this registry is.
        public Task<string?> GetWorkflowStructureHashAsync(string name, int version) =>
            Task.FromResult<string?>(new WorkflowScopeCatalog(definition).StructureHash);
        public Task RetireWorkflowVersionAsync(string name, int version) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ExecutorRejectsADefinitionThatOnlyComputedAStructureHash()
    {
        using var provider = new ServiceCollection().AddLogging().AddIxIFlow().Services.BuildServiceProvider();
        var built = Workflow.Create<RegistrationData>("Validated source").Step<RegistrationActivity>().Build();
        var definition = new WorkflowDefinition
        {
            Version = 1,
            Name = "Unvalidated copy", WorkflowDataType = typeof(RegistrationData), Steps = built.Steps
        };
        var data = new RegistrationData();
        var checkpoint = new ExecutionCheckpoint { StructureHash = new WorkflowScopeCatalog(definition).StructureHash };
        var executor = new WorkflowExecutor(provider, provider.GetRequiredService<IActivityExecutor>(), TimeProvider.System);

        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => executor.RunAsync(definition,
            new WorkflowInstance(), checkpoint, data, null!, CancellationToken.None));

        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
        Assert.Contains("has not been validated", error.Message);
        WorkflowDefinitionValidator.Validate(definition);
        Assert.NotNull(WorkflowDefinitionValidator.ForExecution(definition));
    }

    public sealed class RegistrationData;
    public sealed class RegistrationActivity : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class CountingCondition : System.Linq.Expressions.Expression
    {
        public int Visits { get; private set; }
        public override System.Linq.Expressions.ExpressionType NodeType => System.Linq.Expressions.ExpressionType.Extension;
        public override Type Type => typeof(bool);
        public override bool CanReduce => true;
        public override System.Linq.Expressions.Expression Reduce() => System.Linq.Expressions.Expression.Constant(true);
        protected override System.Linq.Expressions.Expression VisitChildren(System.Linq.Expressions.ExpressionVisitor visitor)
        {
            if (visitor.GetType().DeclaringType == typeof(ExpressionRules)) Visits++;
            return Reduce();
        }
    }

}
