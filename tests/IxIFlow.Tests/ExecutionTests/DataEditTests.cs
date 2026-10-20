using System.Text.Json;
using System.Text.Json.Serialization;
using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
[Trait("Spec", "G-08")]
public sealed class DataEditTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task RootEditStaysPausedAndIsSeenAfterContinue()
    {
        var store = new Store(); var registry = new WorkflowCatalog();
        await using var services = Services(store, registry);
        var id = await Paused(services, Waiting("RootEdit"));
        var before = (await store.GetWorkflowInstanceAsync(id))!;
        var target = Target(before);
        var receipt = await Ops(services).EditDataAsync(id, "root-edit", before.Revision, target,
            [Patch(["Number"], 12), Patch(["Name"], "changed")], "editor");
        var saved = (await store.GetWorkflowInstanceAsync(id))!;
        Assert.Equal(before.Revision + 1, receipt.Revision);
        Assert.Equal(WorkflowStatus.Paused, saved.Status);
        Assert.Equal(target.PauseGeneration, Target(saved).PauseGeneration);

        Assert.Equal(12, DataOf(saved).Number);
        Assert.Equal(WorkflowExecutionStatus.Suspended, (await Ops(services).ContinueAsync(id, "continue")).Status);
        var result = await Client(services).ResumeAsync(id, "go", new Go());
        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(12, Assert.IsType<Data>(result.WorkflowData).Number);
        Assert.Equal("changed", Assert.IsType<Data>(result.WorkflowData).Name);
    }

    [Theory]
    [InlineData("not-paused")]
    [InlineData("revision")]
    [InlineData("generation")]
    [InlineData("structure")]
    [InlineData("missing-branch")]
    public async Task PreconditionsRefuseWithoutChangingDataOrAudit(string reason)
    {
        var store = new Store(); var registry = new WorkflowCatalog(); await using var services = Services(store, registry);
        var definition = Waiting("Precondition" + reason);
        var idle = await Client(services).RunAsync(definition, new Data());
        if (reason != "not-paused") await Ops(services).PauseAsync(idle.InstanceId, "pause");
        var before = (await store.GetWorkflowInstanceAsync(idle.InstanceId))!;
        if (reason == "structure")
        {
            var changedRegistry = new WorkflowCatalog();
            await changedRegistry.RegisterWorkflowAsync(new WorkflowBuilder<Data, object>([], definition.Name)
                .WaitFor<Go>("go").Step<Noop>().Step<Noop>().Build());
            await using var changed = Services(store, changedRegistry);
            await Refused(changed, store, before, before.Revision, Target(before), [Patch(["Number"], 12)]);
            return;
        }
        await Refused(services, store, before, reason == "revision" ? before.Revision - 1 : before.Revision,
            Target(before) with { PauseGeneration = Target(before).PauseGeneration + (reason == "generation" ? 1 : 0),
                ContinuationId = reason == "missing-branch" ? "absent" : null }, [Patch(["Number"], 12)]);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-type")]
    [InlineData("setter")]
    [InlineData("required")]
    [InlineData("readonly")]
    [InlineData("dictionary-key")]
    [InlineData("partial")]
    [InlineData("converter")]
    public async Task InvalidPatchesAreAtomic(string reason)
    {
        var store = new Store(); await using var services = Services(store, new());
        var id = await Paused(services, Waiting("BadPatch" + reason));
        var before = (await store.GetWorkflowInstanceAsync(id))!;
        DataPatch[] patches = reason switch
        {
            "missing" => [Patch(["Absent"], 1)],
            "wrong-type" => [Patch(["Number"], "not-a-number")],
            "setter" => [Patch(["Number"], -1)],
            "required" => [Patch(["Name"], (string?)null) with { Remove = true }],
            "readonly" => [Patch(["ReadOnly"], 1)],
            "dictionary-key" => [Patch(["Map", "secret-key"], 1)],
            "partial" => [Patch(["Number"], 10), Patch(["Absent"], 1)],
            _ => [Patch(["Converted"], "refused-secret-value")]
        };
        await Refused(services, store, before, before.Revision, Target(before), patches);
    }

    [Fact]
    public async Task BranchEditRebuildsItsParentViewAndMergesAtTheJoin()
    {
        var store = new Store(); await using var services = Services(store, new());
        var definition = new WorkflowBuilder<Data, object>([], "BranchEdit").Parallel(p =>
            p.Do(b => b.WaitFor<Go>("left")).Do(b => b.WaitFor<Go>("right"))).Build();
        var id = await Paused(services, definition);
        var saved = (await store.GetWorkflowInstanceAsync(id))!;
        var state = ExecutionCheckpoint.Read(saved.ExecutionStateJson);
        var branch = state.Waits.Single(w => w.Key == "left").ContinuationId;
        await Ops(services).EditDataAsync(id, "branch-edit", saved.Revision, Target(saved, branch),
            [Patch(["Number"], 9)], "branch-editor");
        var edited = (await store.GetWorkflowInstanceAsync(id))!;
        Assert.Equal(0, DataOf(edited).Number);
        state = Read(edited); state.RootData = DataOf(edited);
        Assert.Equal(9, Assert.IsType<Data>(state.DataOf(state.ContinuationById(branch)!)).Number);
        await Ops(services).ContinueAsync(id, "continue");
        await Client(services).ResumeAsync(id, "left", new Go());
        var done = await Client(services).ResumeAsync(id, "right", new Go());
        Assert.Equal(WorkflowExecutionStatus.Success, done.Status);
        Assert.Equal(9, Assert.IsType<Data>(done.WorkflowData).Number);
    }

    [Fact]
    public async Task RootBaselineRebuildsEveryNestedDescendantAndRejectsAnUnrebuildableOne()
    {
        var store = new Store(); await using var services = Services(store, new());
        var definition = new WorkflowBuilder<Data, object>([], "Rebase").Parallel(p => p
            .Do(b => b.Step<SetNumber>(s => s.Output(a => a.Number).To(c => c.WorkflowData.Nested!.Number))
                .Parallel(inner => inner.Do(b => b.WaitFor<Go>("a")).Do(b => b.WaitFor<Go>("b"))))
            .Do(b => b.WaitFor<Go>("c"))).Build();
        var id = await Paused(services, definition);
        var before = (await store.GetWorkflowInstanceAsync(id))!;
        await Ops(services).EditDataAsync(id, "baseline", before.Revision, Target(before),
            [Patch(["Number"], 20), Patch(["Nested", "Number"], 30)], "baseline-editor");
        var saved = (await store.GetWorkflowInstanceAsync(id))!;
        var state = Read(saved); state.RootData = DataOf(saved);
        var descendants = state.Continuations.Where(c => c.ParentJoinId != null).ToArray();
        Assert.Equal(4, descendants.Length);
        Assert.All(descendants, c => Assert.Equal(20, Assert.IsType<Data>(state.DataOf(c)).Number));
        foreach (var key in new[] { "a", "b" })
            Assert.Equal(5, Assert.IsType<Data>(state.DataOf(state.ContinuationById(state.Waits.Single(w => w.Key == key).ContinuationId)!)).Nested!.Number);
        await Refused(services, store, saved, saved.Revision, Target(saved), [Patch(["Nested"], (object?)null)]);
        await Ops(services).ContinueAsync(id, "continue");
        foreach (var key in new[] { "a", "b" }) await Client(services).ResumeAsync(id, key, new Go());
        var done = await Client(services).ResumeAsync(id, "c", new Go());
        Assert.Equal(WorkflowExecutionStatus.Success, done.Status);
        Assert.Equal(20, Assert.IsType<Data>(done.WorkflowData).Number);
        Assert.Equal(5, Assert.IsType<Data>(done.WorkflowData).Nested!.Number);
    }

    [Fact]
    public async Task RootBaselineIsTheNewCombinerOriginal()
    {
        var store = new Store(); await using var services = Services(store, new());
        var definition = new WorkflowBuilder<Data, object>([], "CombinerBaseline").Parallel(p => p
            .Do(b => b.WaitFor<Go>("a")).Do(b => b.WaitFor<Go>("b"))).Build();
        var id = await Paused(services, definition);
        foreach (var key in new[] { "a", "b" })
        {
            var saved = (await store.GetWorkflowInstanceAsync(id))!;
            var branch = Read(saved).Waits.Single(w => w.Key == key).ContinuationId;
            await Ops(services).EditDataAsync(id, "branch-" + key, saved.Revision, Target(saved, branch),
                [Patch(["Combined"], key == "a" ? 2 : 3)], "editor");
        }
        var before = (await store.GetWorkflowInstanceAsync(id))!;
        await Ops(services).EditDataAsync(id, "new-original", before.Revision, Target(before), [Patch(["Combined"], 10)], "editor");
        await Ops(services).ContinueAsync(id, "continue");
        await Client(services).ResumeAsync(id, "a", new Go());
        var done = await Client(services).ResumeAsync(id, "b", new Go());
        Assert.Equal(15, Assert.IsType<Data>(done.WorkflowData).Combined);
    }

    [Fact]
    public async Task LostResponseRetryOnAnotherHostReturnsTheOriginalReceiptEvenAfterContinue()
    {
        var store = new Store(); var registry = new WorkflowCatalog();
        await using var first = Services(store, registry); await using var second = Services(store, new());
        var id = await Paused(first, Waiting("Receipt"));
        var before = (await store.GetWorkflowInstanceAsync(id))!; var target = Target(before); var patches = new[] { Patch(["Number"], 7) };
        store.LoseResponse = true;
        await Assert.ThrowsAsync<WorkflowEngineException>(() => Ops(first).EditDataAsync(id, "lost", before.Revision, target, patches, "editor"));
        store.LoseResponse = false;
        await Ops(first).ContinueAsync(id, "continue");
        await Client(first).ResumeAsync(id, "go", new Go());
        var latest = (await store.GetWorkflowInstanceAsync(id))!;
        var receipt = await Ops(second).EditDataAsync(id, "lost", before.Revision, target, patches, "editor");
        Assert.Equal(new DataEditResult(id, "lost", before.Revision + 1, target), receipt);
        Assert.Equal(latest.Revision, (await store.GetWorkflowInstanceAsync(id))!.Revision);
        Assert.Single(await store.GetTraceAsync(id), e => e.Entry.EntryType == TraceEntryType.DataEdited);
        Assert.Equal(receipt, (await store.GetDebugStatesAsync([id]))[id].Commands.Single(c => c.Id == "lost").EditResult);
        await Assert.ThrowsAsync<ArgumentException>(() => Ops(second).EditDataAsync(id, "lost", before.Revision, target,
            [Patch(["Number"], 8)], "editor"));
    }

    [Fact]
    public async Task ACrashBeforeEditCommitIsRecoveredByAnotherHost()
    {
        var store = new Store(); var registry = new WorkflowCatalog();
        await using var first = Services(store, registry); await using var second = Services(store, registry);
        var id = await Paused(first, Waiting("RecoverEdit")); var before = (await store.GetWorkflowInstanceAsync(id))!;
        store.FailEdit = true;
        await Assert.ThrowsAsync<WorkflowEngineException>(() => Ops(first).EditDataAsync(id, "pending", before.Revision,
            Target(before), [Patch(["Number"], 9)], "editor"));
        Assert.Equal(before.Revision, (await store.GetWorkflowInstanceAsync(id))!.Revision);
        store.FailEdit = false;
        Assert.Equal(WorkflowExecutionStatus.Paused, (await Client(second).RecoverAsync(id)).Status);
        var saved = (await store.GetWorkflowInstanceAsync(id))!;
        Assert.Equal(9, DataOf(saved).Number);
        Assert.Equal(before.Revision + 1, saved.Revision);
        Assert.NotNull((await store.GetDebugStatesAsync([id]))[id].Commands.Single(c => c.Id == "pending").EditResult);
    }

    [Fact]
    public async Task AConcurrentRetryRechecksTheReceiptBeforeItsNowStalePause()
    {
        var store = new Store(); var registry = new WorkflowCatalog();
        await using var first = Services(store, registry, independentGate: true); await using var second = Services(store, registry, independentGate: true);
        var id = await Paused(first, Waiting("ConcurrentReceipt")); var before = (await store.GetWorkflowInstanceAsync(id))!;
        var patches = new[] { Patch(["Number"], 7) }; var target = Target(before);
        store.HoldReads = 1;
        var retry = Ops(second).EditDataAsync(id, "concurrent", before.Revision, target, patches, "editor");
        await store.ReadStarted.Task.WaitAsync(Patience);
        var receipt = await Ops(first).EditDataAsync(id, "concurrent", before.Revision, target, patches, "editor");
        await Ops(first).ContinueAsync(id, "continue");
        await Client(first).ResumeAsync(id, "go", new Go());
        var completed = (await store.GetWorkflowInstanceAsync(id))!;
        store.ReleaseRead.TrySetResult();
        Assert.Equal(receipt, await retry.WaitAsync(Patience));
        Assert.Equal(completed.Revision, (await store.GetWorkflowInstanceAsync(id))!.Revision);
    }

    [Fact]
    public async Task AnotherLeasePreventsAnEditAndAnEditReleasesItsOwnLease()
    {
        var store = new Store(); await using var services = Services(store, new());
        var id = await Paused(services, Waiting("LeaseEdit")); var before = (await store.GetWorkflowInstanceAsync(id))!;
        Assert.NotNull(await store.TryAcquireExecutionLeaseAsync(id, "other-owner", TimeSpan.FromSeconds(30)));
        await Refused(services, store, before, before.Revision, Target(before), [Patch(["Number"], 7)]);
        await store.ReleaseExecutionLeaseAsync(id, "other-owner");
        await Ops(services).EditDataAsync(id, "leased", before.Revision, Target(before), [Patch(["Number"], 7)], "editor");
        Assert.NotNull(await store.TryAcquireExecutionLeaseAsync(id, "next-owner", TimeSpan.FromSeconds(30)));
        await store.ReleaseExecutionLeaseAsync(id, "next-owner");
    }

    [Fact]
    public async Task ContractNamesAndPopulatedObjectMembersAreEditable()
    {
        var store = new Store(); await using var services = Services(store, new());
        var id = await Paused(services, Waiting("ContractPaths")); var before = (await store.GetWorkflowInstanceAsync(id))!;
        await Ops(services).EditDataAsync(id, "contract", before.Revision, Target(before),
            [Patch(["wire-name"], 4), Patch(["Populated", "Number"], 8),
                Patch(["Map"], new Dictionary<string, int> { ["private-key-value"] = 3 })], "editor");
        var data = DataOf((await store.GetWorkflowInstanceAsync(id))!);
        Assert.Equal(4, data.Alias); Assert.Equal(8, data.Populated.Number); Assert.Equal(3, data.Map["private-key-value"]);
        var audit = Assert.Single(await store.GetTraceAsync(id), e => e.Entry.EntryType == TraceEntryType.DataEdited).Entry;
        Assert.DoesNotContain("private-key-value", JsonSerializer.Serialize(audit));
    }

    [Fact]
    public async Task CheckpointLimitRefusesBeforeCommandAcceptanceAndRecoveryAppliesAcceptedEdit()
    {
        var store = new Store(); var registry = new WorkflowCatalog();
        await using var first = Services(store, registry);
        var id = await Paused(first, Waiting("EditLimits")); var before = (await store.GetWorkflowInstanceAsync(id))!;
        await using var small = Services(store, registry, maxCheckpointBytes: 4096);
        await Refused(small, store, before, before.Revision, Target(before), [Patch(["Name"], new string('a', 8192))]);
        await using var second = Services(store, registry);
        store.FailEdit = true;
        await Assert.ThrowsAsync<WorkflowEngineException>(() => Ops(first).EditDataAsync(id, "recover", before.Revision,
            Target(before), [Patch(["Number"], 9)], "editor"));
        store.FailEdit = false;
        Assert.Equal(before.Revision, (await store.GetWorkflowInstanceAsync(id))!.Revision);
        Assert.Equal(WorkflowExecutionStatus.Paused, (await Client(second).RecoverAsync(id)).Status);
        Assert.Equal(9, DataOf((await store.GetWorkflowInstanceAsync(id))!).Number);
    }

    [Fact]
    public async Task ContinueAcceptedDuringEditKeepsItsWakeAndOrder()
    {
        var store = new Store(); var registry = new WorkflowCatalog(); await using var first = Services(store, registry);
        await using var second = Services(store, registry);
        var id = await Paused(first, Waiting("WakeEdit")); var before = (await store.GetWorkflowInstanceAsync(id))!;
        store.HoldEdit = true;
        var edit = Ops(first).EditDataAsync(id, "edit", before.Revision, Target(before), [Patch(["Number"], 8)], "editor");
        await store.Editing.Task.WaitAsync(Patience);
        Assert.Equal(DebugCommandStatus.Accepted, (await store.RequestDebugCommandAsync(id,
            new("continue", DebugCommandKind.Continue, Target(before).PauseGeneration, "continuer"))).Status);
        store.Release.TrySetResult(); await edit.WaitAsync(Patience);
        Assert.Contains(await store.GetRunnableInstancesAsync(100), i => i.InstanceId == id);
        Assert.Equal(WorkflowExecutionStatus.Suspended, (await Client(second).RecoverAsync(id)).Status);
        var commands = (await store.GetDebugStatesAsync([id]))[id].Commands.TakeLast(2).ToArray();
        Assert.Equal([DebugCommandKind.EditData, DebugCommandKind.Continue], commands.Select(c => c.Kind));
        Assert.True(commands[0].AcknowledgedRevision < commands[1].AcknowledgedRevision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuditIsMandatoryAndRecordsOnlyActorTimeRevisionEditTargetAndPaths(bool tracing)
    {
        var store = new Store(); await using var services = Services(store, new(), tracing: tracing);
        var id = await Paused(services, Waiting("Audit" + tracing)); var before = (await store.GetWorkflowInstanceAsync(id))!;
        var started = DateTime.UtcNow;
        await Ops(services).EditDataAsync(id, "audit", before.Revision, Target(before),
            [Patch(["Flag"], true), Patch(["Name"], "edited-secret")], "auditor");
        var entry = Assert.Single(await store.GetTraceAsync(id), e => e.Entry.EntryType == TraceEntryType.DataEdited).Entry;
        var audit = Assert.IsType<DataEditedTrace>(entry.Payload);
        Assert.Equal("auditor", audit.Actor); Assert.Equal("audit", audit.EditId);
        Assert.Equal(Target(before), audit.Target); Assert.Equal(before.Revision + 1, audit.Revision);
        Assert.InRange(entry.Timestamp, started, DateTime.UtcNow);
        Assert.Collection(audit.Paths, path => Assert.Equal(["Flag"], path), path => Assert.Equal(["Name"], path));
        Assert.Equal(["Actor", "EditId", "Target", "Revision", "Paths"],
            JsonSerializer.SerializeToElement(audit).EnumerateObject().Select(p => p.Name));
        var json = JsonSerializer.Serialize(entry);
        Assert.DoesNotContain("edited-secret", json); Assert.DoesNotContain("secret-key", json);
        Assert.DoesNotContain("keep", json); Assert.DoesNotContain("Value", json);
    }

    [Fact]
    public async Task SqliteReceiptSurvivesRepositoryAndHostRecreation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ixiflow-edit-{Guid.NewGuid():N}.db");
        var connection = $"Data Source={path};Pooling=False";
        string id; long revision; DataEditTarget target;
        var patches = new[] { Patch(["Number"], 6) };
        await using (var store = new SqliteWorkflowStateRepository(connection))
        await using (var first = Services(store, new()))
        {
            id = await Paused(first, Waiting("SqliteReceipt"));
            var before = (await store.GetWorkflowInstanceAsync(id))!; revision = before.Revision; target = Target(before);
            await Ops(first).EditDataAsync(id, "sqlite", revision, target, patches, "editor");
        }
        await using (var store = new SqliteWorkflowStateRepository(connection))
        await using (var second = Services(store, new()))
        {
            Assert.Equal(new DataEditResult(id, "sqlite", revision + 1, target),
                await Ops(second).EditDataAsync(id, "sqlite", revision, target, patches, "editor"));
            Assert.Equal(6, DataOf((await store.GetWorkflowInstanceAsync(id))!).Number);
            Assert.Single(await store.GetTraceAsync(id), e => e.Entry.EntryType == TraceEntryType.DataEdited);
        }
    }

    private static async Task Refused(ServiceProvider services, Store store, WorkflowInstance before, long revision,
        DataEditTarget target, DataPatch[] patches)
    {
        var commands = (await store.GetDebugStatesAsync([before.InstanceId]))[before.InstanceId].Commands.Count;
        var traces = (await store.GetTraceAsync(before.InstanceId)).Count;
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => Ops(services).EditDataAsync(before.InstanceId,
            "refused", revision, target, patches, "editor"));
        Assert.DoesNotContain("refused-secret-value", error.Message);
        var after = (await store.GetWorkflowInstanceAsync(before.InstanceId))!;
        Assert.Equal(before.Revision, after.Revision); Assert.Equal(before.WorkflowDataJson, after.WorkflowDataJson);
        Assert.Equal(before.ExecutionStateJson, after.ExecutionStateJson);
        Assert.Equal(commands, (await store.GetDebugStatesAsync([before.InstanceId]))[before.InstanceId].Commands.Count);
        Assert.Equal(traces, (await store.GetTraceAsync(before.InstanceId)).Count);
    }

    private static WorkflowDefinition Waiting(string name) => new WorkflowBuilder<Data, object>([], name).WaitFor<Go>("go").Step<Noop>().Build();
    private static DataPatch Patch<T>(string[] path, T value) => new(path, JsonSerializer.SerializeToElement(value));
    private static IWorkflowClient Client(IServiceProvider services) => services.GetRequiredService<IWorkflowClient>();
    private static IWorkflowOperations Ops(IServiceProvider services) => services.GetRequiredService<IWorkflowOperations>();
    private static Data DataOf(WorkflowInstance instance) => JsonSerializer.Deserialize<Data>(instance.WorkflowDataJson.InlineText)!;
    private static ExecutionCheckpoint Read(WorkflowInstance instance) => ExecutionCheckpoint.Read(instance.ExecutionStateJson);
    private static DataEditTarget Target(WorkflowInstance instance, string? branch = null) => new(WorkflowDebugRules.Checkpoint(instance).PauseGeneration, branch);
    private static async Task<string> Paused(ServiceProvider services, WorkflowDefinition definition)
    {
        var idle = await Client(services).RunAsync(definition, new Data(), new() { EnableTracing = services.GetRequiredService<TraceSetting>().Enabled });
        Assert.Equal(WorkflowExecutionStatus.Suspended, idle.Status);
        Assert.Equal(WorkflowExecutionStatus.Paused, (await Ops(services).PauseAsync(idle.InstanceId, "pause")).Status);
        return idle.InstanceId;
    }
    private static ServiceProvider Services(IWorkflowStateRepository store, WorkflowCatalog registry, bool tracing = true, bool independentGate = false, long? maxCheckpointBytes = null)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow(o => { o.Debugging = true;
            if (maxCheckpointBytes != null) o.MaxCheckpointBytes = maxCheckpointBytes.Value;
            o.MaxTraceEntriesPerCommit = 1; o.MaxTraceEntriesPerInstance = 1; });
        if (independentGate) services.AddSingleton(new InProcessInstanceGate());
        services.AddSingleton(new TraceSetting(tracing));
        services.AddSingleton(store); services.AddSingleton<IWorkflowCatalog>(registry);
        return services.BuildServiceProvider();
    }
    private sealed record TraceSetting(bool Enabled);
    public sealed class Data
    {
        private int _number;
        public int Number { get => _number; set => _number = value >= 0 ? value : throw new ArgumentException("negative value"); }
        [JsonRequired] public string Name { get; set; } = "keep";
        public bool Flag { get; set; }
        public Nested? Nested { get; set; } = new();
        [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)] public Nested Populated { get; } = new();
        [JsonPropertyName("wire-name")] public int Alias { get; set; }
        public Dictionary<string, int> Map { get; set; } = new() { ["secret-key"] = 0 };
        public int ReadOnly => 1;
        [JsonConverter(typeof(OnlyOk))] public string Converted { get; set; } = "ok";
        [Combine<AddOriginal>] public int Combined { get; set; }
    }
    public sealed class Nested { public int Number { get; set; } }
    public sealed class Go;
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public sealed class SetNumber : IAsyncActivity
    {
        [ActivityOutput] public int Number { get; set; }
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) { Number = 5; return Task.CompletedTask; }
    }
    public sealed class AddOriginal : ICombiner<int>
    {
        public int Combine(int original, IReadOnlyList<int> values) => original + values.Sum();
    }
    public sealed class OnlyOk : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString() is "ok" ? "ok" : throw new JsonException("refused-secret-value");
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
    }
    private sealed class Store : InMemoryWorkflowStateRepository
    {
        public bool HoldEdit, FailEdit, LoseResponse;
        public int HoldReads;
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Editing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async Task<IReadOnlyDictionary<string, WorkflowDebugState>> GetDebugStatesAsync(IReadOnlyCollection<string> ids)
        {
            var result = await base.GetDebugStatesAsync(ids);
            if (Interlocked.Exchange(ref HoldReads, 0) != 0)
            {
                ReadStarted.TrySetResult(); await ReleaseRead.Task.WaitAsync(Patience);
            }
            return result;
        }
        public override async Task<WorkflowCommitResult> CommitWorkflowInstanceAsync(CheckpointCommit commit)
        {
            var edit = commit.TraceEntries.Any(e => JsonSerializer.Deserialize<ExecutionTraceEntry>(e)!.EntryType == TraceEntryType.DataEdited);
            if (edit)
            {
                Editing.TrySetResult();
                if (HoldEdit) await Release.Task.WaitAsync(Patience);
                if (FailEdit) throw new IOException("Injected crash before edit commit");
            }
            var result = await base.CommitWorkflowInstanceAsync(commit);
            if (edit && LoseResponse) throw new IOException("Injected lost edit response");
            return result;
        }
    }
}
