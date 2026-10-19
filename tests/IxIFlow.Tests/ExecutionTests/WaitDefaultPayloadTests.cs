using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "waits")]
[Trait("Spec", "D-14")]
public sealed class WaitDefaultPayloadTests : IClassFixture<SqliteTestDatabase<WaitDefaultPayloadTests>>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoveryListsTheDeclaredDefaultBesideTheEventSchema(bool sqlite)
    {
        await using var services = Services(sqlite);
        var client = services.GetRequiredService<IWorkflowClient>();
        var started = await client.RunAsync(Define(new Approval { Approved = true }), new Data());
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        Assert.False(Assert.IsType<Data>(started.WorkflowData).Approved);

        var wait = Assert.Single(await client.GetOpenWaitsAsync(new WaitQuery(InstanceId: started.InstanceId)));
        Assert.True(JsonSerializer.Deserialize<Approval>(wait.DefaultPayloadJson!)!.Approved);
        Assert.Contains("\"Approved\"", wait.EventSchema);
        Assert.Equal(wait.DefaultPayloadJson, (await ((IWorkflowInbox)services.GetRequiredService<IWorkflowStateRepository>())
            .GetWaitAsync(started.InstanceId, wait.WaitId))!.DefaultPayloadJson);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TokenRepliesUseOnlyTheSubmittedPayload(bool declareDefault, bool approved)
    {
        await using var services = Services();
        var client = services.GetRequiredService<IWorkflowClient>();
        var started = await client.RunAsync(Define(declareDefault ? new Approval { Approved = true } : null), new Data());
        var wait = Assert.Single(await client.GetOpenWaitsAsync(new WaitQuery(InstanceId: started.InstanceId)));
        Assert.Equal(declareDefault, wait.DefaultPayloadJson != null);
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);

        var answered = await client.ReplyAsync(wait.Token, new Approval { Approved = approved });
        EventAssert.Consumed(answered);
        Assert.Equal(WorkflowExecutionStatus.Success, answered.Status);
        Assert.Equal(approved, Assert.IsType<Data>(answered.WorkflowData).Approved);
        Assert.Empty(await client.GetOpenWaitsAsync(new WaitQuery(InstanceId: started.InstanceId)));
        EventAssert.Rejected(await client.ReplyAsync(wait.Token, new Approval()), EventRejection.WaitAnswered);
    }

    [Fact]
    public void ADefaultOfTheWrongTypeFailsTheBuild()
    {
        var steps = new List<WorkflowStep>();
        var builder = new WorkflowBuilder<Data, object>(steps, "WrongWaitDefault")
            .WaitFor<Approval>("approve").WithId("approval", "wrong-default.cs", 12);
        Assert.Single(steps).DefaultPayload = "wrong";

        var error = EngineAssert.Fails(WorkflowErrorKind.InvalidDefinition, () => builder.Build());
        Assert.Contains("default payload", error.Message);
        Assert.Contains("String", error.Message);
        Assert.Contains(nameof(Approval), error.Message);
        Assert.Contains("wrong-default.cs:12", error.Message);
    }

    [Fact]
    public void DefaultPayloadsDoNotChangeTheStructureHash()
    {
        var absent = new WorkflowScopeCatalog(Define(null)).StructureHash;
        Assert.Equal(absent, new WorkflowScopeCatalog(Define(new Approval { Approved = true })).StructureHash);
        Assert.Equal(absent, new WorkflowScopeCatalog(Define(new Approval { Approved = false })).StructureHash);
    }

    [Fact]
    public async Task SqliteKeepsTheDefaultWhenTheStoreIsReopened()
    {
        string instanceId;
        string? json;
        await using (var services = Services(sqlite: true))
        {
            var client = services.GetRequiredService<IWorkflowClient>();
            instanceId = (await client.RunAsync(Define(new Approval { Approved = true }), new Data())).InstanceId;
            json = Assert.Single(await client.GetOpenWaitsAsync(new WaitQuery(InstanceId: instanceId))).DefaultPayloadJson;
        }

        await using var reopened = Services(sqlite: true);
        var wait = Assert.Single(await reopened.GetRequiredService<IWorkflowClient>()
            .GetOpenWaitsAsync(new WaitQuery(InstanceId: instanceId)));
        Assert.Equal(json, wait.DefaultPayloadJson);
        Assert.True(JsonSerializer.Deserialize<Approval>(wait.DefaultPayloadJson!)!.Approved);
    }

    private static WorkflowDefinition Define(Approval? payload) => new WorkflowBuilder<Data, object>([], "WaitDefault")
        .WaitFor<Approval>("approve", configure: wait =>
        {
            if (payload != null) wait.DefaultPayload(payload);
            wait.Output(answer => answer.Approved).To(ctx => ctx.WorkflowData.Approved);
        }).Build();

    private static ServiceProvider Services(bool sqlite = false)
    {
        var services = new ServiceCollection().AddLogging();
        var builder = services.AddIxIFlow();
        if (sqlite) builder.UseSqlite(SqliteTestDatabase<WaitDefaultPayloadTests>.ConnectionString);
        return services.BuildServiceProvider();
    }

    public sealed class Data
    {
        public bool Approved { get; set; }
    }

    public sealed class Approval
    {
        public bool Approved { get; set; }
    }
}
