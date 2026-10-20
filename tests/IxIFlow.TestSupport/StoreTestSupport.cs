using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;

namespace IxIFlow.Tests.Infrastructure;

/// <summary>Valid checkpoint and leased setup operations shared by store contract tests.</summary>
public static class StoreTestSupport
{
    /// <summary>Raw diagnostics of the instance's current incarnation, through the store's incarnation-bound read.</summary>
    public static async Task<IReadOnlyList<WorkflowDiagnostic>> GetDiagnosticsAsync(this IWorkflowStateRepository store,
        string instanceId) =>
        await store.GetWorkflowInstanceSummaryAsync(instanceId) is { } summary
            ? await store.GetDiagnosticsForIncarnationAsync(instanceId, summary.Incarnation)
            : [];

    public static string CheckpointJson { get; } = JsonSerializer.Serialize(new ExecutionCheckpoint
    {
        StructureHash = "store-contract",
        Continuations = [new ContinuationState { Id = "root", Stack = [new ScopePosition { ScopeId = "root" }] }]
    });

    private sealed class Owner
    {
        public readonly SemaphoreSlim Gate = new(1);
        public readonly string Token = Guid.NewGuid().ToString("N");
        public ExecutionLeaseGrant? Grant;
    }

    private static readonly ConditionalWeakTable<IWorkflowStateRepository, ConcurrentDictionary<string, Owner>> Owners = new();

    /// <summary>Retains the test owner while active, or claims a new epoch after release or expiry.</summary>
    public static async Task<ExecutionLeaseGrant> LeaseAsync(IWorkflowStateRepository store, string instanceId)
    {
        var owner = Owners.GetOrCreateValue(store).GetOrAdd(instanceId, _ => new Owner());
        await owner.Gate.WaitAsync();
        try
        {
            var duration = TimeSpan.FromMinutes(5);
            if (owner.Grant != null && await store.RenewExecutionLeaseAsync(instanceId, owner.Token, duration))
                return owner.Grant;
            owner.Grant = await store.TryAcquireExecutionLeaseAsync(instanceId, owner.Token, duration);
            return owner.Grant ?? throw new InvalidOperationException($"The test could not claim '{instanceId}'");
        }
        finally { owner.Gate.Release(); }
    }

    /// <summary>Seeds or updates a store fixture through its normal commit contract.</summary>
    public static async Task SeedAsync(this IWorkflowStateRepository store, WorkflowInstance instance,
        TimeSpan? wakeAfter = null)
    {
        var grant = await LeaseAsync(store, instance.InstanceId);
        var commit = CheckpointCommit.Create(instance, grant.Epoch, instance.Revision, wakeAfter: wakeAfter);
        var result = await store.CommitWorkflowInstanceAsync(commit);
        Assert.Equal(WorkflowCommitStatus.Applied, result.Status);
        instance.Revision = result.Revision;
        await ReleaseAsync(store, instance.InstanceId);
    }

    /// <summary>Ends fixture ownership before a host or another owner takes over.</summary>
    public static Task<bool> ReleaseAsync(IWorkflowStateRepository store, string instanceId) =>
        store.ReleaseExecutionLeaseAsync(instanceId, Owners.GetOrCreateValue(store)[instanceId].Token);
}
