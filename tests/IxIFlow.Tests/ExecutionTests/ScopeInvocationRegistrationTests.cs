using IxIFlow.Authoring;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "scopes"), Trait("Spec", "F-26")]
public sealed class ScopeInvocationRegistrationTests
{
    [Fact]
    public void VisibleTypedAndExplicitContractCalleesAreRefusedAtBuild()
    {
        var typed = Assert.Throws<WorkflowEngineException>(() => new WorkflowBuilder<Data, object>([], "Caller")
            .Invoke<TypedChild, Data>(sourcePath: "caller.cs", sourceLine: 17).Build());
        Assert.Equal(WorkflowErrorKind.InvalidDefinition, typed.Kind);
        Assert.Contains("caller.cs:17", typed.Message);
        var contract = WorkflowDefinitionValidator.GetContract(RequiringChild());
        var pinned = Assert.Throws<WorkflowEngineException>(() => new WorkflowBuilder<Data, object>([], "Caller")
            .Invoke<Data>("RequiringChild", 1, contract).Build());
        Assert.Equal(WorkflowErrorKind.InvalidDefinition, pinned.Kind);
        Assert.NotEqual(WorkflowContract.OfType(typeof(Data)).Hash, contract.Hash);
        Assert.Equal(contract.Hash, WorkflowContract.Parse(contract.ToJson()).Hash);
    }

    [Theory]
    [InlineData(false, false, false), InlineData(false, false, true)]
    [InlineData(false, true, false), InlineData(false, true, true)]
    [InlineData(true, false, false), InlineData(true, false, true)]
    [InlineData(true, true, false), InlineData(true, true, true)]
    public Task RegistrationRefusesNamedAndDiOnlyInvocationsInEitherOrder(bool sqlite, bool di, bool callerFirst) =>
        WithStore(sqlite, async store =>
        {
            using var catalog = new WorkflowCatalog(store);
            var caller = Caller(di);
            var child = ServiceCollectionExtensions.BuildWorkflowDefinition<DiChild, Data>(new(new Probe()));
            var first = callerFirst ? caller : child;
            var second = callerFirst ? child : caller;
            await catalog.RegisterWorkflowAsync(first);
            var revision = await store.GetCatalogRevisionAsync();
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => catalog.RegisterWorkflowAsync(second));
            AssertRefusal(error);
            Assert.Null(await catalog.GetWorkflowDefinitionAsync(second.Name, 1));
            Assert.Null(await store.GetWorkflowVersionAsync(second.Name, 1));
            Assert.Equal(revision, await store.GetCatalogRevisionAsync());
            Assert.Empty((await store.QueryInstancesAsync(new())).Items);
        });

    [Theory, InlineData(false), InlineData(true)]
    public async Task HostRefusesDiRegistrationBeforeAnyParentEffectOrInstance(bool callerFirst)
    {
        var probe = new Probe();
        var store = new InMemoryWorkflowStateRepository();
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow();
        services.AddSingleton<IWorkflowStateRepository>(store);
        services.AddSingleton(probe);
        if (callerFirst) services.RegisterWorkflow<DiParent, Data>();
        services.RegisterWorkflow<DiChild, Data>();
        if (!callerFirst) services.RegisterWorkflow<DiParent, Data>();
        await using var provider = services.BuildServiceProvider();
        var error = Assert.Throws<WorkflowEngineException>(() => provider.GetRequiredService<IWorkflowClient>());
        AssertRefusal(error);
        Assert.Equal(0, probe.Effects);
        Assert.Empty((await store.QueryInstancesAsync(new())).Items);
    }

    [Theory, InlineData(false), InlineData(true)]
    public Task PublisherRefusesRequiringCalleeAtTheInvocationPosition(bool sqlite) => WithStore(sqlite, async store =>
    {
        using var catalog = new WorkflowCatalog(store);
        await catalog.RegisterWorkflowAsync(RequiringChild());
        var draft = await store.SaveDefinitionAsync(new("Caller", 1, "workflow", Yaml), null);
        var result = await new WorkflowPublisher(catalog, store, Options()).PublishAsync(draft.Id);
        Assert.False(result.Success);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal("InvalidDefinition", error.Code);
        Assert.Contains("Caller", error.Message);
        Assert.Contains("invoke-child", error.Message);
        Assert.Contains("RequiringChild", error.Message);
        Assert.Equal("workflow.yaml", error.Path);
        Assert.Equal(3, error.Line);
        Assert.True(error.Column > 0);
        Assert.Null(await store.GetWorkflowVersionAsync("Caller", 1));
        Assert.Equal(draft, await store.GetDefinitionAsync("workflow", "Caller", 1));
        Assert.Empty((await store.QueryInstancesAsync(new())).Items);
    });

    [Theory]
    [InlineData(false, false), InlineData(false, true)]
    [InlineData(true, false), InlineData(true, true)]
    public Task PublishedCallerRegistrationChecksBothOrdersAcrossCatalogs(bool sqlite, bool callerFirst) => WithStore(sqlite, async store =>
    {
        // Publish a real bundle, then register it into a different catalog where its
        // formerly scope-free callee's identity now resolves to a requiring contract.
        var source = new InMemoryWorkflowStateRepository();
        using var original = new WorkflowCatalog(source);
        await original.RegisterWorkflowAsync(Workflow.Create<Data>("RequiringChild").Build());
        var draft = await source.SaveDefinitionAsync(new("Caller", 1, "workflow", Yaml), null);
        var published = await new WorkflowPublisher(original, source, Options()).PublishAsync(draft.Id);
        Assert.True(published.Success, string.Join("; ", published.Diagnostics));
        Assert.Contains("invoke-child", Assert.Single(published.Version!.Invocations!).Step);
        using var callers = new WorkflowCatalog(store);
        using var callees = new WorkflowCatalog(store);
        if (callerFirst) await callers.RegisterWorkflowVersionAsync(published.Version, source);
        else await callees.RegisterWorkflowAsync(RequiringChild());
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => callerFirst
            ? callees.RegisterWorkflowAsync(RequiringChild())
            : callers.RegisterWorkflowVersionAsync(published.Version, source));
        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
        Assert.Contains("workflow.yaml:", error.Message);
        Assert.Contains("Caller", error.Message);
        Assert.Contains("invoke-child", error.Message);
        Assert.Contains("RequiringChild", error.Message);
        Assert.Empty((await store.QueryInstancesAsync(new())).Items);
    });

    [Fact]
    public async Task PublisherRefusesFromCatalogContractBeforeLoadingCalleeArtifacts()
    {
        var store = new InMemoryWorkflowStateRepository();
        var contract = WorkflowDefinitionValidator.GetContract(RequiringChild());
        // Catalog metadata is sufficient for refusal; no executable DTO or bundle
        // is needed to know that this child requires an enclosing scope.
        await store.RegisterWorkflowVersionAsync(new("RequiringChild", 1, "structure", contract.ToJson(), contract.Hash, Invocations: []));
        var draft = await store.SaveDefinitionAsync(new("Caller", 1, "workflow", Yaml), null);
        var result = await new WorkflowPublisher(store, store, Options()).PublishAsync(draft.Id);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal("InvalidDefinition", error.Code);
        Assert.Equal(3, error.Line);
        Assert.Contains("invoke-child", error.Message);
        Assert.Null(await store.GetWorkflowVersionAsync("Caller", 1));
    }

    [Fact]
    public async Task RuntimeRefusesActualRequirementsThatDifferFromTheCheckedContract()
    {
        var store = new InMemoryWorkflowStateRepository();
        using var catalog = new WorkflowCatalog(store);
        var caller = new WorkflowBuilder<Data, object>([], "Caller").Invoke<Data>("RequiringChild", 1).Build();
        await catalog.RegisterWorkflowAsync(Workflow.Create<Data>("RequiringChild").Build());
        await catalog.RegisterWorkflowAsync(caller);
        var actual = RequiringChild();
        var mismatch = Assert.Throws<WorkflowEngineException>(() => ChildInputs.CheckContract(actual,
            caller.Steps[0], WorkflowDefinitionValidator.GetContract(actual)));
        Assert.Equal(WorkflowErrorKind.ArtifactMismatch, mismatch.Kind);
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow();
        services.AddSingleton<IWorkflowStateRepository>(store);
        services.AddSingleton<IWorkflowCatalog>(catalog);
        services.AddSingleton<IWorkflowDefinitionSource>(new ChangedChild(catalog, actual));
        await using var provider = services.BuildServiceProvider();
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => provider.GetRequiredService<IWorkflowClient>()
            .RunAsync(caller, new Data()));
        Assert.Equal(WorkflowErrorKind.ArtifactMismatch, error.Kind);
        Assert.Contains("scope requirements differ", error.Message);
        Assert.DoesNotContain((await store.QueryInstancesAsync(new())).Items, row => row.WorkflowName == "RequiringChild");
    }

    [Fact]
    public async Task ScopeFreeInvocationsRunWithoutExplicitContracts()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow();
        services.AddSingleton(new Probe());
        services.RegisterWorkflow<ScopeFreeDiChild, Data>();
        await using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<IWorkflowCatalog>();
        await catalog.RegisterWorkflowAsync(Workflow.Create<Data>("ScopeFree").Build());
        var caller = new WorkflowBuilder<Data, object>([], "Accepted")
            .Invoke<Data>("ScopeFree", 1)
            .Invoke<ScopeFreeDiChild, Data>().Build();
        var client = provider.GetRequiredService<IWorkflowClient>();
        var started = await client.RunAsync(caller, new Data());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var result = await client.AwaitAsync(started.InstanceId, WorkflowAwaitCondition.Finished, timeout.Token);
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
    }

    [Theory, InlineData(false), InlineData(true)]
    public Task RegistrationChecksNestedInvocationsEvenInsideASupplyingScope(bool sqlite) => WithStore(sqlite, async store =>
    {
        using var catalog = new WorkflowCatalog(store);
        await catalog.RegisterWorkflowAsync(RequiringChild());
        var caller = new WorkflowBuilder<Data, object>([], "Caller")
            .Scope<ProvidingScope>(null, body => body
                .Invoke<Data>("RequiringChild", 1, sourcePath: "caller.cs", sourceLine: 17).Named("invoke-child"))
            .Build();
        AssertRefusal(await Assert.ThrowsAsync<WorkflowEngineException>(() => catalog.RegisterWorkflowAsync(caller)));
        Assert.Empty((await store.QueryInstancesAsync(new())).Items);
    });

    private static void AssertRefusal(WorkflowEngineException error)
    {
        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
        Assert.Contains("caller.cs:17", error.Message);
        Assert.Contains("Caller", error.Message);
        Assert.Contains("invoke-child", error.Message);
        Assert.Contains("RequiringChild", error.Message);
    }

    private static WorkflowDefinition RequiringChild() => Workflow.Create<Data>("RequiringChild").RequiresScope<Value>().Build();
    private static WorkflowDefinition Caller(bool di)
    {
        var builder = Workflow.Create<Data>("Caller").Step<Effect>();
        return (di ? builder.Invoke<DiChild, Data>(sourcePath: "caller.cs", sourceLine: 17)
            : builder.Invoke<Data>("RequiringChild", 1, sourcePath: "caller.cs", sourceLine: 17))
            .Named("invoke-child").Build();
    }

    private static WorkflowPublisherOptions Options() => new() { SourceOptions = YamlTestContracts.Create(("Data", typeof(Data))) };
    private const string Yaml = """
        workflow: {name: Caller, dataType: Data}
        definitions:
          - kind: invokeWorkflow
            id: invoke-child
            workflow: {name: RequiringChild, version: 1}
        """;

    private static async Task WithStore(bool sqlite, Func<IWorkflowStateRepository, Task> test)
    {
        if (!sqlite) { await test(new InMemoryWorkflowStateRepository()); return; }
        var directory = Path.Combine(Path.GetTempPath(), "IxIFlow.ScopeInvocation", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var store = new SqliteWorkflowStateRepository("Data Source=" + Path.Combine(directory, "catalog.db"));
            await test(store);
        }
        finally { await SqliteTestFiles.DeleteAsync(directory); }
    }

    public sealed class Data;
    public sealed class Value;
    public sealed class ProvidingScope : IScopeActivity<Value>
    {
        public Task<Value> OpenAsync(ScopeOpening opening, IScopeContext context, CancellationToken token) => Task.FromResult(new Value());
        public Task CloseAsync(Value value, ScopeClosing closing, IScopeContext context, CancellationToken token) => Task.CompletedTask;
    }
    public sealed class Probe { public int Effects; }
    public sealed class Effect(Probe probe) : IAsyncActivity
    { public Task ExecuteAsync(IActivityContext context, CancellationToken token) { probe.Effects++; return Task.CompletedTask; } }
    [Workflow("RequiringChild")] public sealed class TypedChild : IWorkflow<Data>
    { public void Build(IWorkflowBuilder<Data> builder) => builder.RequiresScope<Value>(); }
    [Workflow("RequiringChild")] public sealed class DiChild(Probe probe) : IWorkflow<Data>
    { public void Build(IWorkflowBuilder<Data> builder) { GC.KeepAlive(probe); builder.RequiresScope<Value>(); } }
    [Workflow("ScopeFree")] public sealed class ScopeFreeDiChild(Probe probe) : IWorkflow<Data>
    { public void Build(IWorkflowBuilder<Data> builder) => GC.KeepAlive(probe); }
    [Workflow("Caller")] public sealed class DiParent(Probe probe) : IWorkflow<Data>
    {
        public void Build(IWorkflowBuilder<Data> builder)
        {
            GC.KeepAlive(probe);
            builder.Step<Effect>().Invoke<DiChild, Data>(sourcePath: "caller.cs", sourceLine: 17).Named("invoke-child");
        }
    }

    private sealed class ChangedChild(IWorkflowDefinitionSource original, WorkflowDefinition actual) : IWorkflowDefinitionSource
    {
        public Task<WorkflowDefinitionLease> AcquireWorkflowDefinitionAsync(string name, int version, ArtifactDigest? digest, CancellationToken token = default) =>
            original.AcquireWorkflowDefinitionAsync(name, version, digest, token);
        public Task<WorkflowDefinitionLease> ResolveWorkflowDefinitionAsync(string name, int version, CancellationToken token = default) =>
            name == actual.Name ? Task.FromResult(new WorkflowDefinitionLease(actual)) : original.ResolveWorkflowDefinitionAsync(name, version, token);
    }
}
