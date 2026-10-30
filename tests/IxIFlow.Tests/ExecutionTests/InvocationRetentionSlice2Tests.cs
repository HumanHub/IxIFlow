using System.Collections.Concurrent;
using IxIFlow.ActivitySdk;
using IxIFlow.Builders;
using IxIFlow.Builders.Interfaces;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "children"), Trait("Spec", "A-08"), Trait("Spec", "D-18")]
public sealed class InvocationRetentionSlice2Tests : IClassFixture<SqliteTestDatabase<InvocationRetentionSlice2Tests>>
{
    [Fact]
    public Task MemoryKeepsLoopInvocationsBoundedAcrossCommits() => Verify(new InMemoryWorkflowStateRepository());

    [Fact, Trait("Category", "SqliteIntegration")]
    public async Task SqliteKeepsLoopInvocationsBoundedAcrossCommits()
    {
        await using var store = new SqliteWorkflowStateRepository(SqliteTestDatabase<InvocationRetentionSlice2Tests>.ConnectionString);
        await Verify(store);
    }

    [SqlServerFact, Trait("Category", "SqlIntegration")]
    public Task SqlServerKeepsLoopInvocationsBoundedAcrossCommits() =>
        Verify(new SqlServerWorkflowStateRepository(SqlTestDatabase<InvocationRetentionSlice2Tests>.ConnectionString));

    [PostgreSqlFact, Trait("Category", "PostgreSqlIntegration")]
    public Task PostgreSqlKeepsLoopInvocationsBoundedAcrossCommits() =>
        Verify(new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<InvocationRetentionSlice2Tests>.ConnectionString));

    private static async Task Verify(IWorkflowStateRepository store)
    {
        var probe = new Probe();
        var collection = new ServiceCollection().AddSingleton(store).AddSingleton(probe);
        collection.AddIxIFlow(options => options.VerifyCheckpointParts = true);
        collection.RegisterWorkflow<Child>();
        await using var services = collection.BuildServiceProvider();

        var definition = new WorkflowBuilder<ParentData, object>([], "InvocationRetention")
            .WhileDo(c => c.WorkflowData.Count < 40, body => body
                .Invoke<Child, ChildData>(setup => setup.Input(d => d.ParentId).From(c => c.WorkflowData.ParentId))
                .Assign(c => c.WorkflowData.Count, c => c.WorkflowData.Count + 1)).Build();

        var id = Guid.NewGuid().ToString("N");
        var client = services.GetRequiredService<IWorkflowClient>();

        var result = await client.RunAsync(definition,
            new ParentData { ParentId = id }, new() { InstanceId = id });

        if (result.Status == WorkflowExecutionStatus.Suspended)
            result = await client.AwaitAsync(id, WorkflowAwaitCondition.Finished).WaitAsync(TimeSpan.FromSeconds(120));

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(40, probe.Samples.Count);

        Assert.All(probe.Samples, sample =>
        {
            Assert.Equal(1, sample.Invocations);
            Assert.InRange(sample.Parts, 1, 4);
        });

        Assert.InRange(probe.Samples.Max(sample => sample.Trunk) - probe.Samples.Min(sample => sample.Trunk), 0, 1024);
        var saved = (await store.GetWorkflowInstanceAsync(id))!;
        Assert.Empty(ExecutionCheckpoint.Read(saved.ExecutionStateJson).InvocationRecords);
        Assert.DoesNotContain(saved.SavedParts!.Keys, key => key.StartsWith("v/", StringComparison.Ordinal));
        var traces = await services.GetRequiredService<IWorkflowClient>().GetTraceAsync(id);
        Assert.Equal(40, traces.Count(entry => entry.EntryType == TraceEntryType.ActivityCompleted && entry.Payload is InvocationTrace));
    }

    [Theory]
    [InlineData("WaitingChild")]
    [InlineData("NeedsResolution")]
    [InlineData("Ended")]
    public void ActiveRecoveryResolutionAndRetryOccurrencesRetainTheirAttemptHistory(string phase)
    {
        var continuation = new ContinuationState
        {
            InvocationRecordId = "active", Status = phase switch
            {
                "NeedsResolution" => ContinuationStatus.WaitingResolution,
                "Ended" => ContinuationStatus.Active,
                _ => ContinuationStatus.Waiting
            }
        };

        var record = new InvocationRecordState
        {
            Id = "active", ContinuationId = continuation.Id,
            RetryAfterUtc = phase == "Ended" ? DateTime.UtcNow.AddMinutes(1) : null,
            Attempts =
            [
                new() { Number = 1, Phase = "Ended", Disposition = "Failed" }, new()
                {
                    Number = 2, Phase = phase,
                    Disposition = phase == "Ended" ? "Retry" : null, StartedAtUtc = phase == "WaitingChild" ? DateTime.UtcNow : null
                }
            ]
        };

        var checkpoint = new ExecutionCheckpoint { Continuations = [continuation], InvocationRecords = { [record.Id] = record } };
        InvocationRecords.PrepareCommit(checkpoint, new WorkflowInstance { Status = WorkflowStatus.NeedsResolution }, DateTime.UtcNow);
        Assert.Same(record, Assert.Single(checkpoint.InvocationRecords).Value);
        Assert.Equal(2, record.Attempts.Count);
        Assert.True(Assert.Single(checkpoint.InvocationIndex).Open);
    }

    public sealed class ParentData
    {
        public string ParentId { get; set; } = "";
        public int Count { get; set; }
    }

    public sealed class ChildData
    {
        public string ParentId { get; set; } = "";
    }

    public sealed class Probe
    {
        public ConcurrentBag<(int Invocations, int Parts, int Trunk)> Samples { get; } = [];
    }

    [Workflow("Slice2RetentionChild", Version = 1)]
    public sealed class Child : IWorkflow<ChildData>
    {
        public void Build(IWorkflowBuilder<ChildData> builder) =>
            builder.Step<InspectParent>(s => s.Input(a => a.ParentId).From(c => c.WorkflowData.ParentId));
    }

    public sealed class InspectParent(IWorkflowStateRepository store, Probe probe) : IAsyncActivity
    {
        public string ParentId { get; set; } = "";

        public async Task ExecuteAsync(IActivityContext context, CancellationToken token)
        {
            var saved = (await store.GetWorkflowInstanceAsync(ParentId))!;
            var checkpoint = ExecutionCheckpoint.Read(saved.ExecutionStateJson);
            probe.Samples.Add((checkpoint.InvocationRecords.Count, saved.SavedParts!.Count, saved.ExecutionStateJson.Length));
        }
    }
}