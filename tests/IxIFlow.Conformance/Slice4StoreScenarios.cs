using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "durability")]
public sealed class Slice4StoreScenarios
{
    [Theory]
    [Trait("Spec", "C-11")]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task RunnableTimeKeepsTicksOrTheDocumentedPostgreSqlMicroseconds(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();
        var id = Guid.NewGuid().ToString("N");
        var wake = TimeSpan.FromMinutes(1) + TimeSpan.FromTicks(12345);

        var instance = new WorkflowInstance
        {
            InstanceId = id, WorkflowName = "Precision", Status = WorkflowStatus.Suspended,
            ExecutionStateJson = StoreTestSupport.CheckpointJson
        };

        var grant = await StoreTestSupport.LeaseAsync(store, id);

        var commit = CheckpointCommit.Create(
            instance, grant.Epoch, 0, wakeAfter: wake,
            openedWaits: [new("clock", WaitKind.Delay, "", "", TimeSpan.FromMinutes(2))]);

        Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(commit)).Status);
        var origin = (await store.GetWaitAsync(id, "clock"))!.OpenedAtUtc;
        var expected = (origin + wake).Ticks;

        // PostgreSQL timestamptz stores whole microseconds; Npgsql truncates the remaining ticks.
        if (profile.StartsWith(StoreProfiles.DistributedPostgreSql, StringComparison.Ordinal))
            expected -= expected % 10;

        Assert.Equal(expected, (await store.GetWorkflowInstanceAsync(id))!.RunnableAtUtc!.Value.Ticks);
    }

    [Fact]
    [Trait("Spec", "C-02")]
    public async Task MemoryContendersReachTheStoreAndExactlyOneClaimsTheInstance()
    {
        var store = new InMemoryWorkflowStateRepository();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = 0;
        var query = new ClaimQuery(Guid.Empty, new Dictionary<string, string>());

        var contenders = Enumerable.Range(0, 32).Select(index => Task.Run(async () =>
        {
            if (Interlocked.Increment(ref waiting) == 32)
                ready.TrySetResult();

            await start.Task;

            return await store.TryAcquireExecutionLeaseAsync("contended", "owner-" + index, TimeSpan.FromMinutes(1), query);
        })).ToArray();

        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        start.SetResult();
        var grants = await Task.WhenAll(contenders).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(grants, grant => grant != null);
        Assert.Null(await store.TryAcquireExecutionLeaseAsync("contended", "outsider", TimeSpan.FromMinutes(1), query));
    }

    [Theory]
    [Trait("Spec", "F-27")]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task StoredPreReleaseRowsRefuseClientAndInspectionOperations(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var repository = database.NewRepository();

        await using var services = new ServiceCollection().AddLogging().AddSingleton(repository)
            .AddIxIFlow().Services.BuildServiceProvider();

        var client = services.GetRequiredService<IWorkflowClient>();
        var operations = services.GetRequiredService<IWorkflowOperations>();
        var definition = new WorkflowBuilder<Data, object>([], "OldCheckpoint").WaitFor<string>("answer").Build();
        await services.GetRequiredService<IWorkflowCatalog>().RegisterWorkflowAsync(definition);
        var id = (await client.RunAsync(definition, new Data())).InstanceId;
        var reply = Assert.Single(await client.GetOpenWaitsAsync(new(InstanceId: id))).Token;
        var old = JsonNode.Parse((await repository.GetWorkflowInstanceAsync(id))!.ExecutionStateJson)!;
        old["Runtime"] = "structured-v2";
        old["SchemaVersion"] = 14;
        var json = old.ToJsonString();

        if (repository is InMemoryWorkflowStateRepository memory)
        {
            var rows = (ConcurrentDictionary<string, WorkflowInstance>)typeof(InMemoryWorkflowStateRepository)
                .GetField("_instances", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(memory)!;

            rows[id].ExecutionStateJson = json;
        }
        else
        {
            await using var connection = database.NewConnection();

            var payload = await connection.QuerySingleAsync<string>(
                database.Profile == StoreProfiles.DistributedSqlServer
                    ? "SELECT StateJson FROM dbo.IxIFlowWorkflowInstances WHERE InstanceId=@Id"
                    : "SELECT state_json FROM ixiflow_workflow_instances WHERE instance_id=@Id", new { Id = id });

            var envelope = JsonNode.Parse(payload)!;
            envelope["ExecutionStateJson"] = JsonNode.Parse(json);

            await connection.ExecuteAsync(
                database.Profile == StoreProfiles.DistributedSqlServer
                    ? "UPDATE dbo.IxIFlowWorkflowInstances SET StateJson=@Json WHERE InstanceId=@Id"
                    : "UPDATE ixiflow_workflow_instances SET state_json=@Json WHERE instance_id=@Id",
                new { Json = envelope.ToJsonString(), Id = id });
        }

        Func<Task>[] reads =
        [
            async () => await client.RecoverAsync(id),
            async () => await client.ResumeAsync(id, "value"),
            async () => await client.ResumeAsync(id, "answer", "value"),
            async () => await client.ResumeAsync(id, "answer", "value", "old-resume"),
            async () => await client.ResolveAsync(id, "invocation", ActivityResolution.Completed(new Dictionary<string, object?>())),
            async () => await client.GetSnapshotAsync(id),
            async () => await operations.InspectAsync(id),
            async () => await client.GetStatusAsync(id),
            async () => await client.GetTraceAsync(id),
            async () => await client.GetPendingActivitiesAsync(id),
            async () => await client.GetOpenWaitsAsync(new(InstanceId: id)),
            async () => await client.GetOpenWaitsAsync(new(Key: "answer")),
            async () => await client.AwaitAsync(id),
            async () => await client.AwaitAsync(id, WorkflowAwaitCondition.Finished),
            async () => await client.ReleaseAsync(id),
            async () => await client.CancelAsync(id, new() { ReasonCode = "unsupported-version" }),
            async () => await client.ReplyAsync(reply, "answer"),
            async () => await client.ReplyAsync(reply, "answer", "old-reply"),
            async () => await client.PublishAsync("answer", "value", "old-publish"),
            async () => await client.RunAsync(definition, new Data(), new() { InstanceId = id }),
            async () => await client.StartAsync(definition, new Data(), new() { InstanceId = id }),
            async () => await client.StartAsync(definition.Name, definition.Version, new Data(), new() { InstanceId = id }),
            async () => await client.StartAsync(definition.Name, new Data(), new() { InstanceId = id })
        ];

        foreach (var read in reads)
        {
            var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => read().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(error.Kind == WorkflowErrorKind.ArtifactUnavailable, error.ToString());
            Assert.Contains("Unsupported execution checkpoint version '14'", error.Message);
            Assert.Contains("no migration", error.Message);
        }
    }

    [Theory]
    [Trait("Spec", "B-10")]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task CheckpointBudgetEqualsTheCommittedTrunkAndPartUtf8Bytes(string profile)
    {
        await using var database = await StoreProfiles.CreateAsync(profile);
        var store = database.NewRepository();

        var checkpoint = new ExecutionCheckpoint
        {
            StructureHash = "byte-budget", Demands = [new("tag", "\u00e9")],
            Continuations = [new() { Id = "root", Stack = [new() { ScopeId = "root" }] }]
        };

        var stored = StoredCheckpoint.Split(checkpoint, StoredJson.Inline("{\"Text\":\"\u00e9\u4e16\u754c\"}"));

        var instance = new WorkflowInstance
        {
            InstanceId = Guid.NewGuid().ToString("N"), WorkflowName = "ByteBudget", Status = WorkflowStatus.Suspended,
            ExecutionStateJson = JsonSerializer.Serialize(checkpoint), WorkflowDataJson = stored.Parts[CheckpointParts.DataKey]
        };

        await store.SeedAsync(instance);

        if (database.Profile == StoreProfiles.LocalMemory)
        {
            Assert.Equal(
                Encoding.UTF8.GetByteCount(stored.UpdateTrunk) +
                stored.Parts.Values.Sum(part => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(part))), stored.Bytes);

            return;
        }

        await using var connection = database.NewConnection();

        var trunk = await connection.QuerySingleAsync<string>(
            database.Profile == StoreProfiles.DistributedSqlServer
                ? "SELECT StateJson FROM dbo.IxIFlowWorkflowInstances WHERE InstanceId=@Id"
                : "SELECT state_json FROM ixiflow_workflow_instances WHERE instance_id=@Id", new { Id = instance.InstanceId });

        var parts = await connection.QueryAsync<string>(
            database.Profile == StoreProfiles.DistributedSqlServer
                ? "SELECT Json FROM dbo.IxIFlowWorkflowParts WHERE InstanceId=@Id"
                : "SELECT json FROM ixiflow_workflow_parts WHERE instance_id=@Id", new { Id = instance.InstanceId });

        using var document = JsonDocument.Parse(trunk);
        var committedTrunk = document.RootElement.GetProperty("ExecutionStateJson").GetRawText();
        var measured = CheckpointInspection.CheckpointBytes((await store.GetWorkflowInstanceAsync(instance.InstanceId))!);
        var committed = Encoding.UTF8.GetByteCount(committedTrunk) + parts.Sum(Encoding.UTF8.GetByteCount);

        Assert.True(
            committed == measured, $"Committed {committed}, measured {measured}; trunk {committedTrunk}; parts {string.Join(";", parts)}");
    }

    public sealed class Data;
}