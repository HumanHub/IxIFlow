using IxIFlow.Tests.Infrastructure;
using System.Text.Json;
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
        var built = Workflow.Create<RegistrationData>("Child source").Step<RegistrationActivity>().Build();
        var definition = new WorkflowDefinition
        {
            Version = 1,
            Name = "Unchecked child", WorkflowDataType = typeof(RegistrationData), Steps = built.Steps
        };
        var builder = Workflow.Create<RegistrationData>("Validation parent").Step<RegistrationActivity>();
        var parent = starting
            ? builder.Invoke<RegistrationData>(definition.Name, definition.Version).Build()
            : builder.WaitFor<RegistrationData>("invoke")
                .Invoke<RegistrationData>(definition.Name, definition.Version).Build();
        var registry = new UncheckedRegistry(definition, parent);
        using var provider = new ServiceCollection().AddLogging().AddIxIFlow().Services
            .AddSingleton<IWorkflowCatalog>(registry).BuildServiceProvider();
        var client = provider.GetRequiredService<IWorkflowClient>();
        Assert.NotEmpty(new WorkflowScopeCatalog(definition).StructureHash);

        var refused = await InvokeChild();
        Assert.Equal(WorkflowExecutionStatus.Faulted, refused.Status);
        // The client reports definition errors as faults; compare the validator's classified refusal.
        var error = Assert.Throws<WorkflowEngineException>(() => WorkflowDefinitionValidator.ForExecution(definition));

        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
        Assert.Contains("has not been validated", error.Message);
        Assert.Equal(typeof(WorkflowEngineException).FullName, refused.ErrorType);
        Assert.StartsWith(error.Message, refused.ErrorMessage);
        WorkflowDefinitionValidator.Validate(definition);
        Assert.Same(definition, await registry.GetWorkflowDefinitionAsync(definition.Name, definition.Version));
        Assert.Equal(WorkflowExecutionStatus.Success, (await client.FinishedAsync(await InvokeChild())).Status);

        async Task<WorkflowExecutionResult> InvokeChild()
        {
            var result = await client.RunAsync(parent, new RegistrationData());
            if (starting) return result;
            Assert.Equal(WorkflowExecutionStatus.Suspended, result.Status);
            return await client.ResumeAsync(result.InstanceId, "invoke", new RegistrationData());
        }
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
        await using var renewer = new ExecutionLeaseRenewer(repository);
        var operations = new WorkflowHost.InstanceOperations(provider, provider.GetRequiredService<IActivityExecutor>(),
            repository, new UncheckedRegistry(definition), new InProcessInstanceGate(), renewer);

        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => operations.RecoverAsync(instance.InstanceId,
            CancellationToken.None));

        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
        Assert.StartsWith("recovery.yaml:9:", error.Message);
        Assert.Contains("has not been validated", error.Message);
        Assert.Equal(0, visits.Visits);
        Assert.Equal(instance.ExecutionStateJson, (await repository.GetWorkflowInstanceAsync(instance.InstanceId))!.ExecutionStateJson);
    }

    // Models a custom registry that returns definitions without invoking the engine validator.
    private sealed class UncheckedRegistry(params WorkflowDefinition[] definitions) : IWorkflowCatalog
    {
        public async Task<WorkflowDefinitionLease> AcquireWorkflowDefinitionAsync(string name, int version, ArtifactDigest? digest, CancellationToken cancellationToken = default) =>
            new((await GetWorkflowDefinitionAsync(name, version))!);
        public Task<WorkflowDefinitionLease> ResolveWorkflowDefinitionAsync(string name, int version, CancellationToken cancellationToken = default) =>
            AcquireWorkflowDefinitionAsync(name, version, null);

        public long DefinitionRevision => 0; // This registry's definition set is immutable.
        private readonly WorkflowCatalog _catalog = UncheckedCatalog(definitions);
        private static WorkflowCatalog UncheckedCatalog(WorkflowDefinition[] definitions)
        {
            var store = new InMemoryWorkflowStateRepository();
            foreach (var definition in definitions)
            {
                var scopes = new WorkflowScopeCatalog(definition);
                var invocations = scopes.AllSteps.Where(pair => pair.Value.StepType == WorkflowStepType.WorkflowInvocation)
                    .Select(pair => new WorkflowVersionInvocation(pair.Value.WorkflowName!, pair.Value.WorkflowVersion!.Value,
                        $"{pair.Value.Name} [{scopes.AuthorId(pair.Value)}, {pair.Key}]", pair.Value.SourceLocation ?? pair.Key)).ToArray();
                store.RegisterWorkflowVersionAsync(new(definition.Name, definition.Version,
                    scopes.StructureHash, WorkflowDefinitionValidator.GetContract(definition).ToJson(), WorkflowDefinitionValidator.GetContract(definition).Hash,
                    Descriptor: WorkflowDescriptor.Of(definition), Dependencies: invocations.Select(invocation => new WorkflowVersionDependency(invocation.Name, invocation.Version)).Distinct().ToArray(),
                    Invocations: invocations)).GetAwaiter().GetResult();
            }
            return new WorkflowCatalog(store);
        }
        public Task<IReadOnlyList<CatalogHost>> GetWorkflowVersionHostsAsync(string name, int version, CatalogScope scope) => _catalog.GetWorkflowVersionHostsAsync(name, version, scope);
        public Task<IReadOnlyList<CatalogHost>> GetPackageHostsAsync(string id, string version, string? build, CatalogScope scope) => _catalog.GetPackageHostsAsync(id, version, build, scope);
    public Task<CatalogPage<CatalogWorkflow>> ListWorkflowsAsync(CatalogWorkflowQuery query) => _catalog.ListWorkflowsAsync(query);
    public Task<CatalogPage<CatalogWorkflowVersion>> ListWorkflowVersionsAsync(string name, CatalogVersionQuery query) => _catalog.ListWorkflowVersionsAsync(name, query);
    public Task<CatalogWorkflowVersionDetail?> GetWorkflowVersionDetailAsync(string name, int version, CatalogScope scope) => _catalog.GetWorkflowVersionDetailAsync(name, version, scope);
    public Task<CatalogPage<CatalogTemplate>> ListTemplatesAsync(CatalogTemplateQuery query) => _catalog.ListTemplatesAsync(query);
    public Task<CatalogPage<CatalogTemplate>> ListTemplateVersionsAsync(string key, CatalogVersionQuery query) => _catalog.ListTemplateVersionsAsync(key, query);
    public Task<CatalogTemplateDetail?> GetTemplateDetailAsync(string key, int version, CatalogScope scope) => _catalog.GetTemplateDetailAsync(key, version, scope);
    public Task<IReadOnlyList<CatalogStepDescriptor>> ListStepDescriptorsAsync(CatalogScope scope) => _catalog.ListStepDescriptorsAsync(scope);
    public Task<CatalogPage<CatalogActivity>> ListActivitiesAsync(CatalogActivityQuery query) => _catalog.ListActivitiesAsync(query);
    public Task<CatalogPage<CatalogActivity>> ListActivityVersionsAsync(string key, CatalogVersionQuery query) => _catalog.ListActivityVersionsAsync(key, query);
    public Task<CatalogActivityDetail?> GetActivityAsync(string key, string version, ActivityProviderSelector provider, CatalogScope scope) => _catalog.GetActivityAsync(key, version, provider, scope);
    public Task<CatalogPage<CatalogPackage>> ListPackagesAsync(CatalogPackageQuery query) => _catalog.ListPackagesAsync(query);
    public Task<CatalogPage<CatalogPackage>> ListPackageVersionsAsync(string id, CatalogVersionQuery query) => _catalog.ListPackageVersionsAsync(id, query);
    public Task<CatalogPackageDetail?> GetPackageDetailAsync(string id, string version, CatalogScope scope) => _catalog.GetPackageDetailAsync(id, version, scope);
    public Task<CatalogPage<CatalogActivity>> GetPackageActivitiesAsync(string id, string version, CatalogVersionQuery query) => _catalog.GetPackageActivitiesAsync(id, version, query);
    public Task<IReadOnlyList<WorkflowPackage>> GetPendingPackageDependentsAsync(string missingPackageId, string? afterIdentity, int limit) => _catalog.GetPendingPackageDependentsAsync(missingPackageId, afterIdentity, limit);
    public Task<IxIFlow.Templates.TemplateVersionEntry> RegisterTemplateVersionAsync(IxIFlow.Templates.TemplateVersion version, IArtifactStore artifacts, string? actor = null) => _catalog.RegisterTemplateVersionAsync(version, artifacts, actor);
    public Task<IxIFlow.Templates.TemplateVersionEntry?> GetTemplateVersionAsync(string key, int version) => _catalog.GetTemplateVersionAsync(key, version);
    public Task<IReadOnlyList<IxIFlow.Templates.TemplateVersionEntry>> GetTemplateVersionsAsync(string? key = null) => _catalog.GetTemplateVersionsAsync(key);
    public Task<IxIFlow.Templates.TemplateVersionEntry> PublishTemplateDefinitionAsync(WorkflowDraftId draftId, IxIFlow.Templates.TemplateVersion version, IArtifactStore artifacts, string? actor = null) => _catalog.PublishTemplateDefinitionAsync(draftId, version, artifacts, actor);
    public Task<PackageHolders> GetPackageHoldersAsync(string id, string version, CatalogScope scope) => _catalog.GetPackageHoldersAsync(id, version, scope);
    public Task<PackageRegistration> PublishPackageDefinitionAsync(WorkflowDraftId draftId, WorkflowPackage package, PackageActivities activities, IArtifactStore artifacts, string? actor = null) => _catalog.PublishPackageDefinitionAsync(draftId, package, activities, artifacts, actor);
    public Task<WorkflowPackage> SetPackageWithdrawnAsync(string id, string version, bool withdrawn, CatalogMutationToken expected, string? actor = null) => _catalog.SetPackageWithdrawnAsync(id, version, withdrawn, expected, actor);
    public Task<PackageRegistration> RegisterPackageAsync(WorkflowPackage package, PackageActivities activities, IArtifactStore artifacts, string? actor = null) => _catalog.RegisterPackageAsync(package, activities, artifacts, actor);
    public Task<PackageRegistration> CompletePackageExtractionAsync(string id, string version, ArtifactDigest expectedDigest, PackageActivities activities, IArtifactStore artifacts, string? actor = null) => _catalog.CompletePackageExtractionAsync(id, version, expectedDigest, activities, artifacts, actor);
    public IArtifactStore? CatalogArtifacts { get => _catalog.CatalogArtifacts; set => _catalog.CatalogArtifacts = value; }
    public Task<IReadOnlyList<WorkflowPackage>> GetPackageExtractionPageAsync(PackageExtraction extraction, string? afterIdentity, int limit) => _catalog.GetPackageExtractionPageAsync(extraction, afterIdentity, limit);
        public Task<IReadOnlyList<InstalledActivityPackage>> GetInstalledPackagesAsync(string? id = null) => _catalog.GetInstalledPackagesAsync(id);
        public Task<IReadOnlyList<WorkflowPackage>> GetPackagesAsync(string? id = null) => _catalog.GetPackagesAsync(id);
        public Task<WorkflowPackage?> GetPackageAsync(string id, string version) => _catalog.GetPackageAsync(id, version);
        public Task<IReadOnlyList<WorkflowPackage>> GetPackagePageAsync(string id, string? afterVersion, int limit) => _catalog.GetPackagePageAsync(id, afterVersion, limit);
        public Task<bool> DeleteDefinitionAsync(WorkflowDraftId expected) => _catalog.DeleteDefinitionAsync(expected);
        public Task<CatalogPage<WorkflowDraftSummary>> GetDefinitionsAsync(WorkflowDraftQuery query) => _catalog.GetDefinitionsAsync(query);
        public Task<RetirementBlockers> GetRetirementBlockersAsync(string name, int version, CatalogScope scope) => _catalog.GetRetirementBlockersAsync(name, version, scope);
        public Task<WorkflowPublication> BeginPublicationAsync(WorkflowDraftId draftId, Guid hostId, string? actor, TimeSpan staleAfter) => _catalog.BeginPublicationAsync(draftId, hostId, actor, staleAfter);
        public Task<bool> HeartbeatPublicationAsync(Guid id, Guid hostId) => _catalog.HeartbeatPublicationAsync(id, hostId);
        public Task<WorkflowPublication> CompletePublicationAsync(Guid id, Guid hostId, WorkflowPublicationOutcome outcome) => _catalog.CompletePublicationAsync(id, hostId, outcome);
        public Task<WorkflowPublication?> GetPublicationAsync(Guid id, CatalogScope scope) => _catalog.GetPublicationAsync(id, scope);
        public Task<CatalogPage<WorkflowPublication>> GetPublicationsAsync(WorkflowPublicationQuery query) => _catalog.GetPublicationsAsync(query);
        public Task<int> PurgePublicationsAsync(TimeSpan retention, int batchSize, CancellationToken cancellationToken = default) => _catalog.PurgePublicationsAsync(retention, batchSize, cancellationToken);
        public Task<WorkflowDraft?> GetDefinitionAsync(string kind, string name, int version) => _catalog.GetDefinitionAsync(kind, name, version);
        public Task<WorkflowDraft> SaveDefinitionAsync(WorkflowDraft definition, WorkflowDraftId? expected, string? actor = null) => _catalog.SaveDefinitionAsync(definition, expected, actor);
        public Task RegisterWorkflowVersionAsync(WorkflowVersion version, IArtifactStore? artifacts = null) => _catalog.RegisterWorkflowVersionAsync(version, artifacts);
        public Task<WorkflowVersion?> GetWorkflowVersionAsync(string name, int version) => _catalog.GetWorkflowVersionAsync(name, version);
        public Task<long> GetCatalogRevisionAsync() => _catalog.GetCatalogRevisionAsync();
        public Task<WorkflowCatalogChanges> GetWorkflowVersionChangesAsync(long afterRevision) => _catalog.GetWorkflowVersionChangesAsync(afterRevision);
        public Task<IReadOnlyList<WorkflowVersion>> GetWorkflowVersionsAsync(string? name = null) => _catalog.GetWorkflowVersionsAsync(name);
        public Task<WorkflowVersion> SetWorkflowVersionStateAsync(string name, int version, WorkflowVersionState state, CatalogMutationToken expected, IArtifactStore? artifacts = null, string? actor = null) => _catalog.SetWorkflowVersionStateAsync(name, version, state, expected, artifacts, actor);
        public Task<WorkflowVersion> PublishDefinitionAsync(WorkflowDraftId draftId, WorkflowVersion version, IArtifactStore artifacts, string? actor = null) => _catalog.PublishDefinitionAsync(draftId, version, artifacts, actor);
        public Task<int> PurgeArtifactsAsync(IArtifactStore artifacts, TimeSpan retention, int batchSize, CancellationToken cancellationToken = default) => _catalog.PurgeArtifactsAsync(artifacts, retention, batchSize, cancellationToken);
        public Task<WorkflowVersion?> GetLatestWorkflowVersionAsync(string name) => _catalog.GetLatestWorkflowVersionAsync(name);
        public Task<IReadOnlyList<WorkflowDefinition>> GetWorkflowDefinitionsAsync() =>
            Task.FromResult<IReadOnlyList<WorkflowDefinition>>(definitions);
        public Task RegisterWorkflowAsync(WorkflowDefinition value) => Task.CompletedTask;
        public Task<WorkflowDefinition?> GetWorkflowDefinitionAsync(string name, int version) =>
            Task.FromResult(definitions.FirstOrDefault(definition => definition.Name == name && definition.Version == version));
        // Computed without validation, as the rest of this registry is.
        public async Task<string?> GetWorkflowStructureHashAsync(string name, int version) =>
            await GetWorkflowDefinitionAsync(name, version) is { } definition
                ? new WorkflowScopeCatalog(definition).StructureHash : null;
        public Task RetireWorkflowVersionAsync(string name, int version, CatalogMutationToken expected, string? actor = null) => throw new NotSupportedException();
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
        using var uncheckedProvider = new ServiceCollection().AddLogging().AddIxIFlow().Services
            .AddSingleton<IWorkflowDefinitionSource>(new UncheckedRegistry(definition)).BuildServiceProvider();
        var executor = new WorkflowExecutor(uncheckedProvider, null!);
        var instance = new WorkflowInstance { WorkflowName = definition.Name, WorkflowVersion = definition.Version,
            WorkflowDataType = IxIFlow.Core.Runtime.WorkflowTypeIdentity.SavedName(typeof(RegistrationData)), WorkflowDataJson = StoredJson.Inline(JsonSerializer.Serialize(data)),
            ExecutionStateJson = JsonSerializer.Serialize(checkpoint) };
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => executor.ExecuteAsync(new(Guid.NewGuid(),
            instance, null, checkpoint.StructureHash, 1, 0, ExecutionLimits.Default, false, []), CancellationToken.None));

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
