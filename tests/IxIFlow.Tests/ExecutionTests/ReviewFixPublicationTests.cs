using IxIFlow.Authoring;
using IxIFlow.Core;
using IxIFlow.Templates;
using IxIFlow.Tests.Infrastructure;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "authoring")]
public sealed class ReviewFixPublicationTests
{
    public static TheoryData<string, int, bool> ReceiptReads
    {
        get
        {
            var data = new TheoryData<string, int, bool>();

            foreach (var kind in new[] { "workflow", "activity", "template" })
            foreach (var read in new[] { 1, 2, 3 })
            foreach (var stopping in new[] { false, true })
                data.Add(kind, read, stopping);

            return data;
        }
    }

    [Theory, MemberData(nameof(ReceiptReads))]
    public async Task HeldReceiptReadEndsItsWorkerAndHeartbeat(string kind, int read, bool stopping)
    {
        var clock = new TimerRecordingClock();
        var store = new InMemoryWorkflowStateRepository(clock);
        var draft = await store.SaveDefinitionAsync(new("heldreceipt", 1, kind, "invalid: ["), null);
        var catalog = PublicationServiceWaitTests.CatalogProbe.Wrap(store, out var probe);
        var entered = Signal();
        var release = Signal();
        var heartbeating = Signal();
        var reads = 0;
        var heartbeats = 0;
        CancellationToken receiptToken = default;

        var operation = kind switch
        {
            "workflow" => nameof(IWorkflowCatalogStore.GetWorkflowVersionAsync),
            "activity" => nameof(IWorkflowCatalogStore.GetPackageAsync),
            _ => nameof(IWorkflowCatalogStore.GetTemplateVersionAsync)
        };

        probe.Intercept = (method, args) =>
        {
            if (method.Name == nameof(IWorkflowCatalogStore.HeartbeatPublicationAsync))
            {
                Interlocked.Increment(ref heartbeats);
                heartbeating.TrySetResult();
            }

            // Both the missing-draft read and the service's final reconciliation must be bounded.
            if (read > 1 && method.Name == nameof(IWorkflowCatalogStore.GetDefinitionAsync))
                return Task.FromResult<WorkflowDraft?>(null);

            if (method.Name != operation || Interlocked.Increment(ref reads) != read)
                return null;

            receiptToken = (CancellationToken)args![^1]!;
            entered.TrySetResult();

            return kind switch
            {
                "workflow" => HoldAsync<WorkflowVersion>(),
                "activity" => HoldAsync<WorkflowPackage>(),
                _ => (object)HoldAsync<TemplateVersionEntry>()
            };
        };

        var options = new WorkflowPublisherOptions
        {
            PublicationHeartbeatInterval = TimeSpan.FromMilliseconds(10), PublicationStaleAfter = TimeSpan.FromMilliseconds(400)
        };

        using var service = new PublicationService(catalog, new WorkflowPublisher(catalog, store), options, clock);
        var attempt = await service.PublishAsync(draft.Id, TimeSpan.Zero);

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await clock.WaitForTimerAsync(timer => timer.Pending);
            clock.Advance(options.PublicationHeartbeatInterval);
            await heartbeating.Task.WaitAsync(TimeSpan.FromSeconds(5));

            if (!stopping)
            {
                await TestEventually.UntilAsync(
                    attempt.Id.ToString(), "receipt deadline ends the attempt",
                    () => store.GetPublicationAsync(attempt.Id, CatalogScope.Unrestricted),
                    item => item!.State == WorkflowPublicationState.Failed, timeout: TimeSpan.FromSeconds(5));
            }

            await service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(receiptToken.IsCancellationRequested);
            Assert.DoesNotContain(clock.Armings, timer => timer.Pending);
            var ended = Volatile.Read(ref heartbeats);
            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(ended, Volatile.Read(ref heartbeats));
            Assert.False(release.Task.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await service.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        }

        async Task<T?> HoldAsync<T>()
            where T : class
        {
            // Deliberately ignore cancellation: the owner's finite wait must still end.
            await release.Task;

            return null;
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}