using System.Text.Json.Serialization;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "control-flow")]
[Trait("Spec", "W-04")]
public sealed class AssignReviewTests
{
    [Fact]
    public void SeededPopulateCollectionWouldLoseTheAssignedElementOnRecoveryAndIsRefusedAtBuild()
    {
        var changed = new SeededPopulateData();
        changed.Items[0] = 9;
        var restored = Assert.IsType<SeededPopulateData>(BranchData.Copy(changed));
        Assert.Equal(new[] { 1, 9 }, restored.Items);
        var error = Assert.Throws<WorkflowEngineException>(() => Workflow.Create<SeededPopulateData>("UnsafeRecovery")
            .Assign(ctx => ctx.WorkflowData.Items[0], ctx => 9).WaitFor<Go>("saved").Build());
        Assert.Contains("ASSIGN002", error.Message);
        Assert.Contains("Items", error.Message);
        Assert.Matches(@"AssignReviewTests.cs:\d+", error.Message);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void SettablePopulateCollectionsAlsoRequireReplacementRestoration(bool dictionary)
    {
        var error = Assert.Throws<WorkflowEngineException>(() => dictionary
            ? Workflow.Create<SettablePopulateData>("UnsafeDictionary").Assign(ctx => ctx.WorkflowData.Keys["seed"], ctx => 9).Build()
            : Workflow.Create<SettablePopulateData>("UnsafeList").Assign(ctx => ctx.WorkflowData.Items[0], ctx => 9).Build());
        Assert.Contains("ASSIGN002", error.Message);
    }

    [Fact]
    public async Task SeededReplacementCollectionRetainsTheAssignedElementAcrossASavedWait()
    {
        var definition = Workflow.Create<Data>("SafeRecovery").Assign(ctx => ctx.WorkflowData.Items[0], ctx => 9)
            .WaitFor<Go>("saved").Build();
        var store = new InMemoryWorkflowStateRepository();
        var catalog = new WorkflowCatalog();
        await catalog.RegisterWorkflowAsync(definition);
        string id;
        await using (var first = Services(store, catalog))
        {
            var idle = await first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
            Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
            Assert.Equal(new[] { 9 }, Assert.IsType<Data>(idle.WorkflowData).Items);
            id = idle.InstanceId;
        }
        await using var second = Services(store, catalog);
        var result = await second.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "saved", new Go());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(new[] { 9 }, Assert.IsType<Data>(result.WorkflowData).Items);
    }

    [Theory]
    [InlineData("setter")] [InlineData("write-back")] [InlineData("value")]
    [InlineData("index")] [InlineData("key")]
    public async Task AuthoredEngineExceptionsFromEveryAssignmentStageReachBusinessCatch(string stage)
    {
        var definition = new WorkflowBuilder<Data, object>([], "AuthoredAssignFailure").Try(body =>
        {
            if (stage == "setter") body.Assign(ctx => ctx.WorkflowData.Reject, ctx => 9);
            else if (stage == "write-back") body.Assign(ctx => ctx.WorkflowData.Point.X, ctx => 9);
            else if (stage == "value") body.Assign(ctx => ctx.WorkflowData.Number, ctx => ThrowNumber());
            else if (stage == "index") body.Assign(ctx => ctx.WorkflowData.Items[ThrowNumber()], ctx => 9);
            else body.Assign(ctx => ctx.WorkflowData.Keys[ThrowKey()], ctx => 9);
        }).Catch<Exception, Fault>(body => body.Assign(ctx => ctx.WorkflowData.Caught, ctx => ctx.Fault.Message)).Build();
        await using var services = Services();
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal("authored", Assert.IsType<Data>(result.WorkflowData).Caught);
        var trace = await services.GetRequiredService<IWorkflowOperations>().GetTraceAsync(result.InstanceId);
        Assert.Single(trace, entry => entry.Entry.EntryType == TraceEntryType.DataAssigned);
    }

    public static int ThrowNumber() => throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "authored");
    public static string ThrowKey() => throw new WorkflowEngineException(WorkflowErrorKind.InvalidState, "authored");

    private static ServiceProvider Services(IWorkflowStateRepository? store = null, IWorkflowCatalog? catalog = null)
    {
        var services = new ServiceCollection().AddLogging().AddIxIFlow(options => options.PayloadThresholdBytes = 1).Services;
        if (store != null) services.AddSingleton(store);
        if (catalog != null) services.AddSingleton(catalog);
        return services.BuildServiceProvider();
    }

    public sealed class SeededPopulateData
    {
        [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)] public List<int> Items { get; } = [1];
    }
    public sealed class SettablePopulateData
    {
        [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)] public List<int> Items { get; set; } = [1];
        [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)] public Dictionary<string, int> Keys { get; set; } = new() { ["seed"] = 1 };
    }
    public sealed class Data
    {
        public int Number { get; set; }
        public string Caught { get; set; } = "";
        public List<int> Items { get; set; } = [1];
        public Dictionary<string, int> Keys { get; set; } = [];
        public int Reject { get => 0; set { if (value != 0) ThrowNumber(); } }
        public Point Point { get => new(); set { if (value.X != 0) ThrowNumber(); } }
    }
    public struct Point { public int X { get; set; } }
    public sealed class Go { }
    public sealed class Fault { public string Message { get; set; } = ""; }
}
