using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "scopes"), Trait("Spec", "F-26")]
public sealed class ScopeMetadataRegistrationReviewTests
{
    [Fact]
    public void InvocationInventoryIsRequiredInTheVersionContract()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(Metadata(Child())))!;
        Assert.True(json.AsObject().Remove(nameof(WorkflowVersion.Invocations)));
        Assert.Throws<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize<WorkflowVersion>(json.ToJsonString()));
    }

    [Theory, InlineData(false), InlineData(true)]
    public Task MetadataCannotOmitItsInvocationInventory(bool sqlite) => WithStore(sqlite, async store =>
    {
        var caller = Metadata(Caller());
        foreach (var incomplete in new[] { caller with { Invocations = null! } })
        {
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => store.RegisterWorkflowVersionAsync(incomplete));
            Assert.Equal(WorkflowErrorKind.InvalidRequest, error.Kind);
            Assert.Null(await store.GetWorkflowVersionAsync(caller.Name, 1));
        }
        var child = Metadata(Child());
        Assert.Equal(WorkflowErrorKind.InvalidRequest, (await Assert.ThrowsAsync<WorkflowEngineException>(() =>
            store.RegisterWorkflowVersionAsync(child with { Invocations = null! }))).Kind);
        Assert.Equal(0, await store.GetCatalogRevisionAsync());
    });

    [Theory]
    [InlineData(false, false), InlineData(false, true)]
    [InlineData(true, false), InlineData(true, true)]
    public Task FalseEmptyInvocationMetadataCannotAcquireAnExecutableCaller(bool sqlite, bool childFirst) => WithStore(sqlite, async store =>
    {
        var caller = Caller();
        using var catalog = new WorkflowCatalog(store);
        if (childFirst) await catalog.RegisterWorkflowAsync(Child());
        await store.RegisterWorkflowVersionAsync(Metadata(caller) with { Invocations = [] });
        if (!childFirst) await catalog.RegisterWorkflowAsync(Child());
        var revision = await store.GetCatalogRevisionAsync();
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => catalog.RegisterWorkflowAsync(caller));
        Assert.Equal(WorkflowErrorKind.InvalidState, error.Kind);
        Assert.Null(await catalog.GetWorkflowDefinitionAsync(caller.Name, 1));
        Assert.Equal(revision, await store.GetCatalogRevisionAsync());
    });

    [Theory]
    [InlineData(false, "MCD"), InlineData(false, "MDC"), InlineData(false, "CMD")]
    [InlineData(false, "CDM"), InlineData(false, "DMC"), InlineData(false, "DCM")]
    [InlineData(true, "MCD"), InlineData(true, "MDC"), InlineData(true, "CMD")]
    [InlineData(true, "CDM"), InlineData(true, "DMC"), InlineData(true, "DCM")]
    public Task MetadataDefinitionAndRequiringChildAreCheckedInEveryOrder(bool sqlite, string order) => WithStore(sqlite, async store =>
    {
        using var catalog = new WorkflowCatalog(store);
        var caller = Caller();
        var child = Child();
        var hasCaller = false;
        var hasChild = false;
        foreach (var action in order)
        {
            Task Register() => action switch
            {
                'M' => store.RegisterWorkflowVersionAsync(Metadata(caller)),
                'D' => catalog.RegisterWorkflowAsync(caller),
                _ => catalog.RegisterWorkflowAsync(child)
            };
            if (action == 'C' ? hasCaller : hasChild)
            {
                var revision = await store.GetCatalogRevisionAsync();
                var error = await Assert.ThrowsAsync<WorkflowEngineException>(Register);
                Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
                Assert.Contains("caller.cs:17", error.Message);
                Assert.Contains("invoke-child", error.Message);
                Assert.Equal(revision, await store.GetCatalogRevisionAsync());
                if (action == 'D') Assert.Null(await catalog.GetWorkflowDefinitionAsync(caller.Name, 1));
                return;
            }
            await Register();
            if (action == 'C') hasChild = true;
            else hasCaller = true;
        }
        Assert.Fail("The caller and its requiring child were both accepted");
    });

    [Theory, InlineData(false), InlineData(true)]
    public Task AttachingDefinitionsAndReregisteringMetadataPreserveInvocationIdentities(bool sqlite) => WithStore(sqlite, async store =>
    {
        var caller = Caller();
        var metadata = Metadata(caller);
        var invocation = Assert.Single(metadata.Invocations);
        await store.RegisterWorkflowVersionAsync(metadata with { Invocations = [invocation with { Step = "forged-step" }] });
        using var catalog = new WorkflowCatalog(store);
        Assert.Equal(WorkflowErrorKind.InvalidState, (await Assert.ThrowsAsync<WorkflowEngineException>(() =>
            catalog.RegisterWorkflowAsync(caller))).Kind);
        Assert.Null(await catalog.GetWorkflowDefinitionAsync(caller.Name, 1));
        foreach (var changed in new[]
        {
            metadata,
            metadata with { Invocations = [invocation with { Name = "Other" }], Dependencies = [new("Other", 1)] },
            metadata with { Invocations = [invocation with { Version = 2 }], Dependencies = [new(invocation.Name, 2)] }
        })
            Assert.Equal(WorkflowErrorKind.InvalidState, (await Assert.ThrowsAsync<WorkflowEngineException>(() =>
                store.RegisterWorkflowVersionAsync(changed))).Kind);
    });

    [Theory, InlineData(false), InlineData(true)]
    public Task PresentationPositionsCanChangeWhenAttachingOrReregistering(bool sqlite) => WithStore(sqlite, async store =>
    {
        var caller = Caller();
        var metadata = Metadata(caller);
        await store.RegisterWorkflowVersionAsync(metadata with
        { Invocations = [Assert.Single(metadata.Invocations) with { SourceLocation = "moved.cs:99" }] });
        using var catalog = new WorkflowCatalog(store);
        await catalog.RegisterWorkflowAsync(caller);
        await catalog.RegisterWorkflowAsync(caller);
        await store.RegisterWorkflowVersionAsync(metadata);
        Assert.Same(caller, await catalog.GetWorkflowDefinitionAsync(caller.Name, 1));
    });

    private static WorkflowDefinition Caller() => new WorkflowBuilder<Data, object>([], "MetadataCaller")
        .Invoke<Data>("MetadataChild", 1, sourcePath: "caller.cs", sourceLine: 17).Named("invoke-child").Build();
    private static WorkflowDefinition Child() => Workflow.Create<Data>("MetadataChild").RequiresScope<Value>().Build();
    private static WorkflowVersion Metadata(WorkflowDefinition definition)
    {
        var contract = WorkflowDefinitionValidator.GetContract(definition);
        return new(definition.Name, definition.Version, WorkflowDefinitionValidator.Inspect(definition), contract.ToJson(), contract.Hash,
            Descriptor: WorkflowDescriptor.Of(definition), Dependencies: WorkflowDefinitionValidator.GetDependencies(definition), Invocations: WorkflowDefinitionValidator.GetInvocations(definition));
    }
    private static async Task WithStore(bool sqlite, Func<IWorkflowStateRepository, Task> test)
    {
        if (!sqlite) { await test(new InMemoryWorkflowStateRepository()); return; }
        var directory = Path.Combine(Path.GetTempPath(), "IxIFlow.ScopeMetadataReview", Guid.NewGuid().ToString("N"));
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
}
