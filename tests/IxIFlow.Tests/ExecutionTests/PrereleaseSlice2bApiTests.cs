using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "guard")]
public sealed class PrereleaseSlice2bApiTests : IClassFixture<SqliteTestDatabase<PrereleaseSlice2bApiTests>>
{
    [Fact]
    public void RemainingStatusesKeepTheirPersistedNumbers()
    {
        Assert.Equal([0, 1, 2, 3, 4, 5, 7, 8, 9, 10], Enum.GetValues<WorkflowStatus>().Select(value => (int)value));
        Assert.Equal([0, 1, 2, 3, 4, 6, 7, 8, 9, 10, 11], Enum.GetValues<WorkflowExecutionStatus>().Select(value => (int)value));
        Assert.Equal(7, (int)WorkflowStatus.Terminated);
        Assert.Equal(6, (int)WorkflowExecutionStatus.TimedOut);

        foreach (var status in Enum.GetValues<WorkflowStatus>())
            Assert.Equal(((int)status).ToString(), JsonSerializer.Serialize(status));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    [InlineData(11)]
    [InlineData(999)]
    public void UndefinedWorkflowStatusesAreRefusedOnWritesAndReads(int number)
    {
        var instance = Instance();
        instance.Status = (WorkflowStatus)number;

        Assert.Equal(WorkflowErrorKind.InvalidRequest,
            Assert.Throws<WorkflowEngineException>(() => CheckpointCommit.Create(instance, 1, 0)).Kind);

        Assert.Equal(WorkflowErrorKind.InvalidRequest,
            Assert.Throws<WorkflowEngineException>(() => JsonSerializer.Serialize(instance)).Kind);

        Assert.Equal(WorkflowErrorKind.InvalidState,
            Assert.Throws<WorkflowEngineException>(() => JsonSerializer.Deserialize<WorkflowInstance>($$"""{"Status":{{number}}}""")).Kind);

        Assert.Equal(WorkflowErrorKind.InvalidState,
            Assert.Throws<WorkflowEngineException>(() => CheckpointParts.Summarize($$"""{"Status":{{number}}}""", null, 1)).Kind);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(12)]
    public void UndefinedExecutionStatusesAreRefusedOnTheWire(int number)
    {
        Assert.Equal(WorkflowErrorKind.InvalidState,
            Assert.Throws<WorkflowEngineException>(() => JsonSerializer.Deserialize<WorkflowExecutionStatus>(number.ToString())).Kind);

        Assert.Equal(WorkflowErrorKind.InvalidRequest,
            Assert.Throws<WorkflowEngineException>(() => JsonSerializer.Serialize((WorkflowExecutionStatus)number)).Kind);
    }

    [Fact]
    public void ImportedCommitsRefuseUndefinedStatusMetadataAndPayloads()
    {
        var commit = CheckpointCommit.Create(Instance(), 1, 0);
        var fields = JsonSerializer.Deserialize(JsonSerializer.Serialize(commit), CheckpointCommitContext.Default.CheckpointCommitFields)!;

        Assert.Equal(WorkflowErrorKind.InvalidRequest, Assert.Throws<WorkflowEngineException>(() =>
            Rebuild(fields with { Instance = fields.Instance with { Status = (WorkflowStatus)6 } })).Kind);

        var payload = JsonNode.Parse(fields.Payload)!;
        payload["Status"] = 6;

        Assert.Equal(WorkflowErrorKind.InvalidRequest, Assert.Throws<WorkflowEngineException>(() =>
            Rebuild(fields with { Payload = payload.ToJsonString() })).Kind);

        var wire = JsonNode.Parse(JsonSerializer.Serialize(commit))!;
        wire["Payload"] = payload.ToJsonString();

        Assert.Equal(WorkflowErrorKind.InvalidState, Assert.Throws<WorkflowEngineException>(() =>
            JsonSerializer.Deserialize<CheckpointCommit>(wire.ToJsonString())).Kind);
    }

    [Fact]
    public void ReplayRefusesUndefinedCommitStatusBeforeCopyingTheTrace()
    {
        var definition = IxIFlow.Builders.Workflow.Create<object>("replay-status").Build();

        var entry = new StoredTraceEntry(1, new())
            { Revision = 1, CommitEndSequence = 1, CommitStatus = (WorkflowStatus)6 };

        Assert.Equal(WorkflowErrorKind.InvalidState, Assert.Throws<WorkflowEngineException>(() =>
            TraceReplay.Load(definition, [entry])).Kind);
    }

    private static CheckpointCommit Rebuild(CheckpointCommitFields fields) => CheckpointCommit.FromFields(
        fields.Instance, fields.Payload, fields.Parts, fields.DeletedParts, fields.ReplacesParts, fields.Incarnation,
        fields.Epoch, fields.CommitId, fields.ExpectedRevision, fields.ClearsCrashCount, fields.Parks, fields.Settlements,
        fields.InboxReadThrough, fields.OpenedWaits, fields.ArmedWaits, fields.ClosedWaits, fields.WakeAfter, fields.Stopped,
        fields.ChildStarts, fields.ChildCancellations, fields.ParentNotice, fields.TraceEntries, fields.Diagnostics,
        fields.RetainedDiagnosticIds, fields.PayloadReferences, fields.PreservesPayloadPins, fields.PayloadBodies);

    [Theory]
    [InlineData("instance")]
    [InlineData("summary")]
    [InlineData("trace")]
    public async Task MemoryReadersAndTheGuardRefuseUndefinedStoredStatuses(string target)
    {
        var store = new InMemoryWorkflowStateRepository();
        var instance = await SeedAsync(store);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;

        if (target == "instance")
        {
            var rows = (System.Collections.Concurrent.ConcurrentDictionary<string, WorkflowInstance>)
                typeof(InMemoryWorkflowStateRepository).GetField("_instances", flags)!.GetValue(store)!;

            rows[instance.InstanceId].Status = (WorkflowStatus)6;
        }
        else if (target == "summary")
        {
            var rows = (Dictionary<string, InstanceSummary>)typeof(InMemoryWorkflowStateRepository)
                .GetField("_summaries", flags)!.GetValue(store)!;

            rows[instance.InstanceId] = rows[instance.InstanceId] with { Status = (WorkflowStatus)6 };
        }
        else
        {
            var rows = (Dictionary<string, List<(string Json, long Revision, long? End, WorkflowStatus? Status)>>)
                typeof(InMemoryWorkflowStateRepository).GetField("_traces", flags)!.GetValue(store)!;

            var row = rows[instance.InstanceId][0];
            rows[instance.InstanceId][0] = (row.Json, row.Revision, row.End, (WorkflowStatus)6);
        }

        var guarded = GuardedStateRepository.Wrap(store);

        var error = target switch
        {
            "instance" => await Assert.ThrowsAsync<WorkflowEngineException>(() => guarded.GetWorkflowInstanceAsync(instance.InstanceId)),
            "summary" => await Assert.ThrowsAsync<WorkflowEngineException>(() =>
                guarded.QueryInstancesAsync(new(InstanceId: instance.InstanceId))),
            _ => await Assert.ThrowsAsync<WorkflowEngineException>(() => guarded.GetTraceAsync(instance.InstanceId))
        };

        Assert.Equal(WorkflowErrorKind.InvalidState, error.Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoreWritesRefuseUndefinedCommitMetadata(bool sqlite)
    {
        await using var relational = sqlite ? new SqliteWorkflowStateRepository(ConnectionString) : null;
        IWorkflowStateRepository store = relational is null ? new InMemoryWorkflowStateRepository() : relational;
        var commit = CheckpointCommit.Create(Instance(), 1, 0);

        typeof(CheckpointCommit).GetField("<Instance>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(commit, commit.Instance with { Status = (WorkflowStatus)6 });

        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => store.CommitWorkflowInstanceAsync(commit));
        Assert.Equal(WorkflowErrorKind.InvalidRequest, error.Kind);
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("column")]
    [InlineData("trace")]
    public async Task SqliteReadersRefuseUndefinedPersistedStatuses(string target)
    {
        await using var store = new SqliteWorkflowStateRepository(ConnectionString);
        var instance = await SeedAsync(store);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();

        command.CommandText = target switch
        {
            "payload" => "UPDATE ixiflow_workflow_instances SET state_json = json_set(state_json, '$.Status', 6) WHERE instance_id = $id",
            "column" => "UPDATE ixiflow_workflow_instances SET status = 'Compensating' WHERE instance_id = $id",
            _ => "UPDATE ixiflow_workflow_traces SET commit_status = 6 WHERE instance_id = $id"
        };

        command.Parameters.AddWithValue("$id", instance.InstanceId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());

        var error = target switch
        {
            "payload" => await Assert.ThrowsAsync<WorkflowEngineException>(() => store.GetWorkflowInstanceAsync(instance.InstanceId)),
            "column" => await Assert.ThrowsAsync<WorkflowEngineException>(() =>
                store.QueryInstancesAsync(new(InstanceId: instance.InstanceId))),
            _ => await Assert.ThrowsAsync<WorkflowEngineException>(() => store.GetTraceAsync(instance.InstanceId))
        };

        Assert.Equal(WorkflowErrorKind.InvalidState, error.Kind);

        if (target == "payload")
            Assert.Equal(WorkflowErrorKind.InvalidState, (await Assert.ThrowsAsync<WorkflowEngineException>(() =>
                store.GetWorkflowInstanceSummaryAsync(instance.InstanceId))).Kind);

        if (target == "column")
            Assert.Equal(WorkflowErrorKind.InvalidState, (await Assert.ThrowsAsync<WorkflowEngineException>(() =>
                store.GetWorkflowInstanceAsync(instance.InstanceId))).Kind);
    }

    [Fact]
    public async Task CancelledReadsReachMemoryAndTheHostedClient()
    {
        var store = new InMemoryWorkflowStateRepository();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IWorkflowStateRepository>(store);
        services.AddIxIFlow();
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IWorkflowClient>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetStatusAsync("absent", cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetTraceAsync("absent", cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetCatalogRevisionAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ListPublishersAsync(cancellation.Token));
        var operations = provider.GetRequiredService<IWorkflowOperations>();
        var incarnation = Guid.NewGuid();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            operations.SubmitResolutionAsync("absent", "invocation", ActivityResolution.Failed("observed"), incarnation,
                cancellation.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            operations.SubmitReleaseAsync("absent", "actor", incarnation, cancellation.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            operations.SubmitCancellationAsync("absent", new(), incarnation, cancellation.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            operations.SubmitTerminationAsync("absent", "stop", null, "actor", incarnation, cancellation.Token));
    }

    [Fact]
    public async Task SqliteInboxReadCancellationInterruptsTheWriterGateWait()
    {
        await using var store = new SqliteWorkflowStateRepository(ConnectionString);
        await store.GetPendingEventsAsync("absent", 0, 10);

        var gate = (SemaphoreSlim)typeof(SqliteWorkflowStateRepository)
            .GetField("_writer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!;

        await gate.WaitAsync();

        try
        {
            using var cancellation = new CancellationTokenSource();
            var read = store.GetPendingEventsAsync("absent", 0, 10, cancellation.Token);
            Assert.False(read.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public async Task TracePagingStopsWhenTheCallerCancelsBetweenPages()
    {
        using var cancellation = new CancellationTokenSource();
        var store = DispatchProxy.Create<IWorkflowStateRepository, TraceReadProbe>();
        var probe = (TraceReadProbe)(object)store;
        probe.Cancellation = cancellation;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(store);
        services.AddIxIFlow();
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IWorkflowClient>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetTraceAsync("paged", cancellation.Token));
        Assert.Equal(1, probe.Pages);
        Assert.Equal(cancellation.Token, probe.ReceivedToken);
    }

    public class TraceReadProbe : DispatchProxy
    {
        private readonly IWorkflowStateRepository _inner = new InMemoryWorkflowStateRepository();
        internal CancellationTokenSource Cancellation { get; set; } = null!;
        internal int Pages { get; private set; }
        internal CancellationToken ReceivedToken { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IWorkflowStateRepository.GetTraceAsync))
            {
                ReceivedToken = (CancellationToken)args![^1]!;
                Pages++;
                Cancellation.Cancel();

                return Task.FromResult<IReadOnlyList<StoredTraceEntry>>([new(1, new())]);
            }

            return targetMethod.Invoke(_inner, args);
        }
    }

    [Fact]
    public void StoreContractsAndOperationsPutOptionalCancellationLast()
    {
        Type[] contracts =
        [
            typeof(IWorkflowStateRepository), typeof(IWorkflowInbox), typeof(IWorkflowOutbox), typeof(IWorkflowCatalogStore),
            typeof(IWorkflowOperations)
        ];

        foreach (var method in contracts.SelectMany(type => type.GetMethods()).Where(method => method.Name.EndsWith("Async")))
        {
            var parameter = method.GetParameters().Last();
            Assert.Equal(typeof(CancellationToken), parameter.ParameterType);
            Assert.Equal("cancellationToken", parameter.Name);
            Assert.True(parameter.IsOptional, method.ToString());
        }

        Assert.DoesNotContain(
            typeof(InMemoryWorkflowStateRepository).GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            member => member is MethodInfo { IsVirtual: true, IsFinal: false });
    }

    private static string ConnectionString => SqliteTestDatabase<PrereleaseSlice2bApiTests>.ConnectionString;

    private static WorkflowInstance Instance() => new()
    {
        InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "slice2b", Status = WorkflowStatus.Running,
        WorkflowDataJson = StoredJson.Inline("{}"), ExecutionStateJson = StoreTestSupport.CheckpointJson
    };

    private static async Task<WorkflowInstance> SeedAsync(IWorkflowStateRepository store)
    {
        var instance = Instance();
        var epoch = (await StoreTestSupport.LeaseAsync(store, instance.InstanceId)).Epoch;
        var commit = CheckpointCommit.Create(instance, epoch, 0, traceEntries: [new() { EntryType = TraceEntryType.WorkflowStarted }]);
        Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(commit)).Status);

        return instance;
    }
}