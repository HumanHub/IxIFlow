using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Core;

namespace IxIFlow.Tests.Conformance;

[Trait("Spec", "F-18")]
[Trait("Feature", "registration")]
public sealed class WorkflowContractCatalogScenarios
{
    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task ContractsAreMandatoryDurableAndImmutable(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var contract = WorkflowContract.OfType(typeof(Data));
        var row = new WorkflowVersion("ContractCatalog", 1, "structure", ContractJson: contract.ToJson(), ContractHash: contract.Hash);
        await store.RegisterWorkflowVersionAsync(row);
        Assert.Equal(row, await store.GetWorkflowVersionAsync(row.Name, row.Version));
        await store.RegisterWorkflowVersionAsync(row);
        var changed = new WorkflowContract([], [], true);
        foreach (var (proposed, message) in new[]
        {
            (row with { ContractJson = changed.ToJson() }, "requires a canonical contract and matching contract hash"),
            (row with { ContractHash = changed.Hash }, "requires a canonical contract and matching contract hash"),
            (row with { ContractJson = changed.ToJson(), ContractHash = changed.Hash }, "immutable")
        })
        {
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => store.RegisterWorkflowVersionAsync(proposed));
            Assert.Equal(WorkflowErrorKind.InvalidRequest, error.Kind);
            Assert.Contains(message, error.Message);
            Assert.Equal(row.ContractJson, (await store.GetWorkflowVersionAsync(row.Name, row.Version))!.ContractJson);
            Assert.Equal(row.ContractHash, (await store.GetWorkflowVersionAsync(row.Name, row.Version))!.ContractHash);
        }
        await store.SetWorkflowVersionStateAsync(row.Name, row.Version, WorkflowVersionState.Deprecated);
        var saved = (await store.GetWorkflowVersionAsync(row.Name, row.Version))!;
        Assert.Equal(row.ContractJson, saved.ContractJson);
        Assert.Equal(row.ContractHash, saved.ContractHash);
        Assert.Equal(WorkflowVersionState.Deprecated, saved.State);
        var missing = await Assert.ThrowsAsync<WorkflowEngineException>(() => store.RegisterWorkflowVersionAsync(new("MissingContract", 1, "structure", null!, null!)));
        Assert.Equal(WorkflowErrorKind.InvalidRequest, missing.Kind);
    }

    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task CodeRegistrationRecordsTheEffectiveContract(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        using var catalog = new WorkflowCatalog(store);
        var definition = Workflow.Create<Data>("ContractRegistration").Input(data => data.Extra).Build();
        await catalog.RegisterWorkflowAsync(definition);
        var row = (await store.GetWorkflowVersionAsync(definition.Name, definition.Version))!;
        var contract = WorkflowDefinitionValidator.GetContract(definition);
        Assert.Equal(contract.ToJson(), row.ContractJson);
        Assert.Equal(contract.Hash, row.ContractHash);
        var changed = Workflow.Create<Data>(definition.Name).Input().Build();
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => catalog.RegisterWorkflowAsync(changed));
        Assert.Equal(WorkflowErrorKind.InvalidRequest, error.Kind);
        Assert.Contains("immutable", error.Message);
    }

    public sealed class Data
    {
        [WorkflowInput] public int Value { get; set; }
        public int Extra { get; set; }
    }
}
