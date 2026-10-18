using IxIFlow.Core;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "durability")]
[Trait("Spec", "D-41")]
public sealed class RetentionDecoratorTests
{
    [Fact]
    public async Task FaultDecoratorKeepsRetentionAndTombstones()
    {
        var time = new FakeTimeProvider();
        IWorkflowStateRepository store = new FaultRepository(new InMemoryWorkflowStateRepository(time));
        await store.SaveWorkflowInstanceAsync(new WorkflowInstance
            { InstanceId = "retained", WorkflowName = "Retention", Status = WorkflowStatus.Completed });
        time.Advance(TimeSpan.FromDays(31));
        Assert.Equal(1, (await store.PurgeAsync(TimeSpan.FromDays(30), TimeSpan.FromDays(7), 10)).InstancesRemoved);
        var tombstone = (await store.GetTombstoneAsync("retained"))!;
        Assert.Equal(TimeSpan.FromDays(7), tombstone.ExpiresAtUtc - tombstone.RemovedAtUtc);
        Assert.Null(await store.GetWorkflowInstanceAsync("retained"));
    }
}
