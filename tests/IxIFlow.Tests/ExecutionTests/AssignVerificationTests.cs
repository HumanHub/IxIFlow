using System.Text.Json;
using System.Text.Json.Serialization;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "control-flow")]
[Trait("Spec", "W-04")]
public sealed class AssignVerificationTests
{
    [Theory]
    [InlineData("object")]
    [InlineData("nested")]
    [InlineData("list")]
    [InlineData("array")]
    [InlineData("dictionary")]
    [InlineData("list-element")]
    [InlineData("array-element")]
    [InlineData("dictionary-element")]
    public void ReplacementSubtreesThatRetainInitializerItemsOnRecoveryAreRefused(string shape)
    {
        var changed = new UnsafeData { Child = new UnsafeChild { Items = [9] } };
        Assert.Equal(new[] { 1, 9 }, Assert.IsType<UnsafeData>(BranchData.Copy(changed)).Child.Items);
        var builder = new WorkflowBuilder<UnsafeData, object>([], "ReplacementRecovery");
        var error = Assert.Throws<WorkflowEngineException>(() =>
        {
            if (shape == "object") builder.Assign(ctx => ctx.WorkflowData.Child, ctx => new UnsafeChild { Items = new List<int> { 9 } });
            else if (shape == "nested") builder.Assign(ctx => ctx.WorkflowData.Nested, ctx => new UnsafeData());
            else if (shape == "list") builder.Assign(ctx => ctx.WorkflowData.Children, ctx => new List<UnsafeChild>());
            else if (shape == "array") builder.Assign(ctx => ctx.WorkflowData.Array, ctx => new UnsafeChild[0]);
            else if (shape == "list-element") builder.Assign(ctx => ctx.WorkflowData.Children[0], ctx => new UnsafeChild());
            else if (shape == "array-element") builder.Assign(ctx => ctx.WorkflowData.Array[0], ctx => new UnsafeChild());
            else if (shape == "dictionary-element") builder.Assign(ctx => ctx.WorkflowData.Keys["key"], ctx => new UnsafeChild());
            else builder.Assign(ctx => ctx.WorkflowData.Keys, ctx => new Dictionary<string, UnsafeChild>());
            builder.WaitFor<Go>("saved").Build();
        });
        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
        Assert.Contains("ASSIGN002", error.Message);
        Assert.Contains("Items", error.Message);
    }

    [Fact]
    public void APropertyConverterDefinesTheRestorationOfItsOpaqueReplacement()
    {
        Workflow.Create<ConvertedData>("ConvertedReplacement")
            .Assign(ctx => ctx.WorkflowData.Child, ctx => new UnsafeChild { Items = new List<int> { 9 } }).Build();
        var data = new ConvertedData { Child = new UnsafeChild { Items = [9] } };
        Assert.Equal(new[] { 9 }, Assert.IsType<ConvertedData>(BranchData.Copy(data)).Child.Items);
    }

    [Fact]
    public async Task ReplacementObjectRestoresItsCollectionsAcrossASavedWait()
    {
        var definition = Workflow.Create<Data>("RestorableReplacement")
            .Assign(ctx => ctx.WorkflowData.Child, ctx => new Child { Items = new List<int> { 9 } })
            .WaitFor<Go>("saved").Build();
        var store = new InMemoryWorkflowStateRepository();
        var catalog = new WorkflowCatalog();
        await catalog.RegisterWorkflowAsync(definition);
        string id;
        await using (var first = Services(store, catalog))
        {
            var idle = await first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
            Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
            id = idle.InstanceId;
        }
        await using var second = Services(store, catalog);
        var result = await second.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "saved", new Go());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(new[] { 9 }, Assert.IsType<Data>(result.WorkflowData).Child.Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnreadableRecoveredItemBypassesTheEnclosingBusinessCatch(bool readIndex)
    {
        var definition = new WorkflowBuilder<Data, object>([], "UnreadableAssignItem")
            .Try(attempt => attempt.ForEach(ctx => ctx.WorkflowData.Numbers, (body, item) =>
            {
                var waiting = body.WaitFor<Go>("saved");
                if (readIndex) waiting.Assign(ctx => ctx.WorkflowData.Number, ctx => ctx.Index(item));
                else waiting.Assign(ctx => ctx.WorkflowData.Number, ctx => ctx.Item(item));
            }))
            .Catch<Exception>(handler => handler.Assign(ctx => ctx.WorkflowData.Caught, ctx => true))
            .Build();
        var original = new InMemoryWorkflowStateRepository();
        var catalog = new WorkflowCatalog();
        await catalog.RegisterWorkflowAsync(definition);
        string id;
        await using (var first = Services(original, catalog))
        {
            var idle = await first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data());
            Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
            id = idle.InstanceId;
        }
        var saved = (await original.GetWorkflowInstanceAsync(id))!;
        var checkpoint = ExecutionCheckpoint.Read(saved.ExecutionStateJson);
        var iteration = Assert.Single(checkpoint.Continuations.SelectMany(c => c.Stack), frame => frame.ForEach != null);
        iteration.ForEach!.Items[0] = StoredJson.Inline("\"unreadable integer\"");
        saved.ExecutionStateJson = JsonSerializer.Serialize(checkpoint);
        // A fresh store and host admit only the serialized checkpoint, without live item caches.
        var recovered = new InMemoryWorkflowStateRepository();
        saved.Revision = 0;
        var lease = await StoreTestSupport.LeaseAsync(recovered, id);
        var commit = CheckpointCommit.Create(saved, lease.Epoch, 0, openedWaits: checkpoint.Waits
            .Select(wait => new OpenedWait(wait.Id, wait.TableKind, wait.Key, wait.EventType, wait.Timeout)).ToArray());
        Assert.Equal(WorkflowCommitStatus.Applied, (await recovered.CommitWorkflowInstanceAsync(commit)).Status);
        await StoreTestSupport.ReleaseAsync(recovered, id);
        await using var second = Services(recovered, catalog);
        var result = await second.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "saved", new Go());
        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Equal(typeof(WorkflowEngineException).FullName, result.ErrorType);
        Assert.Contains("item", result.ErrorMessage);
        Assert.Contains("cannot be read back from a checkpoint", result.ErrorMessage);
        var final = (await recovered.GetWorkflowInstanceAsync(id))!;
        Assert.Equal(WorkflowStatus.Failed, final.Status);
        Assert.False(JsonSerializer.Deserialize<Data>(final.WorkflowDataJson.InlineText)!.Caught);
        var trace = await second.GetRequiredService<IWorkflowOperations>().GetTraceAsync(id);
        Assert.DoesNotContain(trace, entry => entry.Entry.EntryType == TraceEntryType.DataAssigned);
    }

    private static ServiceProvider Services(IWorkflowStateRepository store, IWorkflowCatalog catalog) =>
        new ServiceCollection().AddLogging().AddIxIFlow().Services
            .AddSingleton(store).AddSingleton(catalog).BuildServiceProvider();

    public sealed class UnsafeChild
    {
        [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)]
        public List<int> Items { get; set; } = [1];
    }

    public sealed class UnsafeData
    {
        public UnsafeChild Child { get; set; } = new();
        public UnsafeData? Nested { get; set; }
        public List<UnsafeChild> Children { get; set; } = [];
        public UnsafeChild[] Array { get; set; } = [];
        public Dictionary<string, UnsafeChild> Keys { get; set; } = [];
    }

    public sealed class Data
    {
        public Child Child { get; set; } = new();
        public int[] Numbers { get; set; } = [7];
        public int Number { get; set; }
        public bool Caught { get; set; }
    }

    public sealed class Child { public List<int> Items { get; set; } = [1]; }
    public sealed class ConvertedData
    {
        [JsonConverter(typeof(ChildConverter))]
        public UnsafeChild Child { get; set; } = new();
    }
    public sealed class ChildConverter : JsonConverter<UnsafeChild>
    {
        public override UnsafeChild Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            new() { Items = JsonSerializer.Deserialize<List<int>>(ref reader, options)! };
        public override void Write(Utf8JsonWriter writer, UnsafeChild value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.Items, options);
    }
    public sealed class Go { }
}
