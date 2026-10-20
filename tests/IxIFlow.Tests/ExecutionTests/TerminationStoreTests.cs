using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "durability")]
public sealed class TerminationStoreTests : IClassFixture<SqliteTestDatabase<TerminationStoreTests>>
{
    [Fact]
    public Task Memory() => VerifyAsync(new InMemoryWorkflowStateRepository());

    [Fact]
    public async Task Sqlite()
    {
        await using var store = new SqliteWorkflowStateRepository(SqliteTestDatabase<TerminationStoreTests>.ConnectionString);
        await VerifyAsync(store);
    }

    [SqlServerFact]
    public Task SqlServer() => VerifyAsync(new SqlWorkflowStateRepository(SqlTestDatabase<TerminationStoreTests>.ConnectionString));

    [PostgreSqlFact]
    public Task PostgreSql() => VerifyAsync(new PostgreSqlWorkflowStateRepository(PostgreSqlTestDatabase<TerminationStoreTests>.ConnectionString));

    private static async Task VerifyAsync(IWorkflowStateRepository store)
    {
        var parent = await OperationsTests.SeedAsync(store, WorkflowStatus.Suspended);
        var child = await OperationsTests.SeedAsync(store, WorkflowStatus.NeedsResolution, parent.InstanceId);
        var additional = new List<string>();
        try
        {
            await store.RequestCancellationAsync(parent.InstanceId, new CancellationReason { ReasonCode = "graceful" });
            var cancellation = (await store.GetCancellationRequestAsync(parent.InstanceId))!;
            var instance = (await store.GetWorkflowInstanceAsync(parent.InstanceId))!;
            instance.CancellationReason = cancellation;
            var lease = await StoreTestSupport.LeaseAsync(store, instance.InstanceId);
            Assert.Equal(WorkflowCommitStatus.Applied, (await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, lease.Epoch, instance.Revision,
                wakeAfter: TimeSpan.Zero))).Status);
            instance = (await store.GetWorkflowInstanceAsync(parent.InstanceId))!;
            lease = await StoreTestSupport.LeaseAsync(store, instance.InstanceId);
            instance.Status = WorkflowStatus.Completed;
            var completion = CheckpointCommit.Create(instance, lease.Epoch, instance.Revision);
            var accepted = await store.RequestTerminationAsync(instance.InstanceId, new CancellationReason { ReasonCode = "hard", RequestedBy = "alice" });
            var repeated = await store.RequestTerminationAsync(instance.InstanceId, new CancellationReason { ReasonCode = "later", RequestedBy = "bob" });
            Assert.True(accepted.Accepted);
            Assert.True(accepted.Version > cancellation.CommandVersion);
            Assert.Equal(accepted, repeated);
            Assert.Equal(WorkflowCommitStatus.TerminationRequested, (await store.CommitWorkflowInstanceAsync(completion)).Status);
            Assert.False(await store.RenewExecutionLeaseAsync(instance.InstanceId, "wrong-owner", TimeSpan.FromSeconds(30)));
            await StoreTestSupport.ReleaseAsync(store, instance.InstanceId);
            var services = new ServiceCollection(); services.AddLogging(); services.AddIxIFlow(); services.AddSingleton(store);
            // The store remains owned by this fixture; the service scope does not dispose its externally supplied instance.
            await using var host = services.BuildServiceProvider();
            var stopped = await host.GetRequiredService<IWorkflowOperations>().TerminateAsync(instance.InstanceId, "repeat");
            Assert.Equal(WorkflowStatus.Terminated, stopped.Status);
            Assert.Null(stopped.StopKind);
            var saved = (await store.GetWorkflowInstanceAsync(instance.InstanceId))!;
            Assert.Equal(accepted.Version, saved.CancellationReason!.CommandVersion);
            Assert.Equal("alice", saved.CancellationReason.RequestedBy);
            Assert.Equal(WorkflowControlMode.Terminate, (await store.GetCancellationRequestAsync(child.InstanceId))!.Mode);
            Assert.Equal(WorkflowExecutionStatus.Terminated, (await host.GetRequiredService<IWorkflowClient>().RecoverAsync(child.InstanceId)).Status);
            var entry = Assert.Single(await store.GetTraceAsync(parent.InstanceId), item => item.Entry.EntryType == TraceEntryType.WorkflowTerminated);
            Assert.Equal("alice", Assert.IsType<CancellationTrace>(entry.Entry.Payload).Actor);
            Assert.False((await store.RequestTerminationAsync(parent.InstanceId, new CancellationReason())).Accepted);

            // The host may have discovered a running child which completes before the terminal
            // transaction reaches it. The store follows that child's retained detached descendants.
            var root = await OperationsTests.SeedAsync(store, WorkflowStatus.Suspended);
            additional.Add(root.InstanceId);
            var finishedChild = await OperationsTests.SeedAsync(store, WorkflowStatus.Completed, root.InstanceId);
            additional.Add(finishedChild.InstanceId);
            var detached = await OperationsTests.SeedAsync(store, WorkflowStatus.Suspended, finishedChild.InstanceId);
            additional.Add(detached.InstanceId);
            await store.RequestTerminationAsync(root.InstanceId, new CancellationReason { ReasonCode = "subtree" });
            var terminating = (await store.GetWorkflowInstanceAsync(root.InstanceId))!;
            terminating.Status = WorkflowStatus.Terminated;
            terminating.CancellationReason = (await store.GetCancellationRequestAsync(root.InstanceId))!;
            var terminal = CheckpointCommit.Create(terminating,
                (await StoreTestSupport.LeaseAsync(store, root.InstanceId)).Epoch, terminating.Revision,
                childCancellations: [new ChildCancellation(finishedChild.InstanceId, terminating.CancellationReason, null)]);
            var applied = await store.CommitWorkflowInstanceAsync(terminal);
            Assert.Equal(WorkflowCommitStatus.Applied, applied.Status);
            Assert.Contains(detached.InstanceId, applied.MadeRunnable.Select(notice => notice.InstanceId));
            Assert.Equal(WorkflowStatus.Completed, (await store.GetWorkflowInstanceAsync(finishedChild.InstanceId))!.Status);
            Assert.Equal(WorkflowControlMode.Terminate, (await store.GetCancellationRequestAsync(detached.InstanceId))!.Mode);
        }
        finally
        {
            foreach (var id in additional.AsEnumerable().Reverse()) await store.DeleteWorkflowInstanceAsync(id);
            await store.DeleteWorkflowInstanceAsync(child.InstanceId);
            await store.DeleteWorkflowInstanceAsync(parent.InstanceId);
        }
    }
}
