using System.Collections.Concurrent;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

/// <summary>
/// The executor runs an instance against any host: commits, cancellation, ownership and child
/// workflows all go through <see cref="IExecutionHost"/> (spec A-01).
/// </summary>
[Trait("Feature", "durability")]
[Trait("Spec", "A-01")]
public sealed class WorkflowExecutorHostTests
{
    [Fact]
    public async Task ExecutorCommitsThroughTheHostWithoutAStore()
    {
        using var services = Services();
        var host = new RecordingHost(Probe(services));
        var definition = Workflow.Create<ExecutorData>("ExecutorWithoutStore")
            .Step<FirstActivity>()
            .Step<SecondActivity>()
            .Build();

        var result = await RunAsync(services, definition, host);

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        Assert.Equal(["first", "second"], Probe(services).Seen.Select(seen => seen.Activity));
        var first = Probe(services).Seen[0].CommitsBefore;
        var second = Probe(services).Seen[1].CommitsBefore;
        Assert.True(first > 0, "The first activity starts after a commit");
        Assert.True(second > first, "The second activity starts after the first one's commits");
        Assert.Equal(WorkflowStatus.Completed, host.Commits[^1]);
    }

    [Fact]
    public async Task ChildInvocationIsAskedOfTheHost()
    {
        using var services = Services();
        var host = new RecordingHost(Probe(services));
        var definition = Workflow.Create<ExecutorData>("ExecutorChild")
            .Step<FirstActivity>()
            .Invoke<ExecutorData>("ExecutorChildTarget", 1)
            .Build();

        var result = await RunAsync(services, definition, host);

        Assert.Equal(WorkflowExecutionStatus.Success, result.Status);
        var invocation = Assert.Single(host.Invocations);
        Assert.Equal("ExecutorChildTarget", invocation.WorkflowName);
    }

    [Fact]
    [Trait("Spec", "E-12")]
    public async Task ADrainBetweenAnItemsCompletionAndTheNextAdmissionLeavesTheRestToRecovery()
    {
        using var services = Services();
        var host = new RecordingHost(Probe(services));
        Draining = host.Draining;
        // The host starts draining as the join decides on the first item, before the next is admitted.
        var definition = Workflow.Create<ExecutorData>("DrainedBetweenItems")
            .Step<FirstActivity>()
            .ForEach(_ => new[] { 1, 2 }, (body, _) => body.Step<SecondActivity>().Result(_ => 1),
                setup => setup.Parallel(maxConcurrency: 1).WaitConditionally<int>((_, _) =>
                {
                    Draining?.Cancel();
                    return false;
                }))
            .Build();

        var result = await RunAsync(services, definition, host);

        // Nothing is active and nothing waits, but an item is still to start: the run stops to continue on
        // recovery, rather than finding nothing to do.
        Assert.Equal(WorkflowExecutionStatus.Running, result.Status);
        Assert.Single(Probe(services).Seen, seen => seen.Activity == "second");
    }

    [Fact]
    public async Task LostOwnershipStopsTheRunWithoutAnotherCommit()
    {
        using var services = Services();
        var host = new RecordingHost(Probe(services)) { LoseOwnershipAtCommit = 2 };
        var definition = Workflow.Create<ExecutorData>("ExecutorLosesOwnership")
            .Step<FirstActivity>()
            .Step<SecondActivity>()
            .Build();

        var result = await RunAsync(services, definition, host);

        Assert.Equal(WorkflowExecutionStatus.Running, result.Status);
        Assert.Single(host.Commits);
        Assert.DoesNotContain(Probe(services).Seen, seen => seen.Activity == "second");
    }

    [Fact]
    public async Task CancellationReportedByTheHostRunsFinally()
    {
        using var services = Services();
        var probe = Probe(services);
        var host = new RecordingHost(probe)
        {
            CancellationWhen = () => probe.Seen.Any(seen => seen.Activity == "first")
        };
        var definition = Workflow.Create<ExecutorData>("ExecutorCancellation")
            .Step<StartActivity>()
            .Try(body => body
                .Step<FirstActivity>()
                .Step<SecondActivity>())
            .Finally(cleanup => cleanup.Step<CleanupActivity>())
            .Build();

        var result = await RunAsync(services, definition, host);

        Assert.Equal(WorkflowExecutionStatus.Cancelled, result.Status);
        Assert.Equal(["start", "first", "cleanup"], probe.Seen.Select(seen => seen.Activity));
    }

    private static Task<WorkflowExecutionResult> RunAsync(IServiceProvider services,
        WorkflowDefinition definition, RecordingHost host)
    {
        var time = TimeProvider.System;
        var now = time.GetUtcNow().UtcDateTime;
        var data = new ExecutorData();
        var instance = new WorkflowInstance
        {
            InstanceId = Guid.NewGuid().ToString("N"),
            WorkflowName = definition.Name,
            WorkflowVersion = definition.Version,
            WorkflowDataType = typeof(ExecutorData).AssemblyQualifiedName!,
            WorkflowDataJson = JsonSerializer.Serialize(data),
            Status = WorkflowStatus.Running,
            CreatedAt = now,
            StartedAt = now
        };
        var checkpoint = new ExecutionCheckpoint
        {
            DefinitionFingerprint = new WorkflowScopeCatalog(definition).Fingerprint,
            Continuations = [new ContinuationState { Stack = [new ScopePosition { ScopeId = "root" }] }]
        };
        var executor = new WorkflowExecutor(services, services.GetRequiredService<IActivityExecutor>(), time);
        return executor.RunAsync(definition, instance, checkpoint, data, host, CancellationToken.None);
    }

    private static ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.AddSingleton<ExecutorProbe>();
        services.AddTransient<StartActivity>();
        services.AddTransient<FirstActivity>();
        services.AddTransient<SecondActivity>();
        services.AddTransient<CleanupActivity>();
        return services.BuildServiceProvider();
    }

    private static ExecutorProbe Probe(IServiceProvider services) => services.GetRequiredService<ExecutorProbe>();

    // The drain a definition's condition starts; read through a static, as a definition may not capture a host.
    private static CancellationTokenSource? Draining;

    /// <summary>A host with no store: it records what the executor asks of it.</summary>
    private sealed class RecordingHost(ExecutorProbe probe) : IExecutionHost
    {
        private readonly List<WorkflowStatus> _commits = [];

        public IReadOnlyList<WorkflowStatus> Commits => _commits;
        public List<WorkflowStep> Invocations { get; } = [];
        public int LoseOwnershipAtCommit { get; init; }
        public Func<bool> CancellationWhen { get; init; } = () => false;

        public bool PersistsState => true;
        public CancellationTokenSource Draining { get; } = new();
        public CancellationToken Drain => Draining.Token;
        public long Epoch => 1;
        public ExecutionLimits Limits { get; } = new() { MaxCheckpointBytes = long.MaxValue };
        public bool ChildStartIsCheckpointed => true;
        public bool CanPark => false;
        public Task? OwnershipLoss => null;
        public Task<CancellationReason>? CancellationSignal => null;
        public CancellationReason? ObservedCancellation { get; private set; }

        public void ThrowIfOwnershipLost()
        {
        }

        public Task<CheckpointReply> CommitAsync(CheckpointCommit commit)
        {
            if (_commits.Count + 1 == LoseOwnershipAtCommit)
                return Task.FromResult(new CheckpointReply(CheckpointReplyKind.Conflict,
                    Error: new InvalidOperationException("Another owner committed")));
            _commits.Add(commit.ReadInstance().Status);
            probe.Commits = _commits.Count;
            // Like a store, the commit reply reports a pending cancellation request.
            return Task.FromResult(new CheckpointReply(CheckpointReplyKind.Acknowledged, _commits.Count,
                PendingCancellation: CancellationWhen()
                    ? new CancellationReason { ReasonCode = "host-cancelled" }
                    : null));
        }

        public void ObserveCancellation(CancellationReason reason) => ObservedCancellation ??= reason;

        public Task<WorkflowInvocationTransitionResult> InvokeChildAsync(WorkflowStep step,
            WorkflowDefinition definition, WorkflowInstance instance, object workflowData, object? previous,
            string invocationId, ItemValues items, CancellationToken cancellationToken)
        {
            Invocations.Add(step);
            return Task.FromResult(new WorkflowInvocationTransitionResult(invocationId, new ExecutorData(), false));
        }

        public Task<WorkflowInstance?> ReadChildAsync(string childInstanceId) =>
            throw new NotSupportedException("No child is saved in this test");

        public Task<WorkflowExecutionResult> RecoverChildAsync(string childInstanceId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("No child is recovered in this test");

        public Task<WorkflowExecutionResult> CancelChildAsync(string childInstanceId, CancellationReason reason,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("No child is cancelled in this test");

        public Task<WorkflowExecutionResult> ResumeChildAsync(string childInstanceId, string? key, object @event,
            string eventId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("No child is resumed in this test");

        public long ObservedInboxSequence => 0;
        public Task? InboxChanged => null;

        // No event is sent in these tests: the inbox is always empty.
        public Task<InboxPage> ReadInboxAsync(long afterSequence, int limit) =>
            Task.FromResult(new InboxPage([], afterSequence, new Dictionary<string, long>()));

        public Task<StoredWait?> ReadWaitAsync(string waitId) => Task.FromResult<StoredWait?>(null);
    }

    public sealed class ExecutorProbe
    {
        private readonly ConcurrentQueue<(string Activity, int CommitsBefore)> _seen = new();

        public int Commits { get; set; }
        public IReadOnlyList<(string Activity, int CommitsBefore)> Seen => _seen.ToArray();
        public void Record(string activity) => _seen.Enqueue((activity, Commits));
    }

    public sealed class ExecutorData
    {
        public string Value { get; set; } = "";
    }

    public sealed class StartActivity(ExecutorProbe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.Record("start");
            return Task.CompletedTask;
        }
    }

    public sealed class FirstActivity(ExecutorProbe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.Record("first");
            return Task.CompletedTask;
        }
    }

    public sealed class SecondActivity(ExecutorProbe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.Record("second");
            return Task.CompletedTask;
        }
    }

    public sealed class CleanupActivity(ExecutorProbe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        {
            probe.Record("cleanup");
            return Task.CompletedTask;
        }
    }
}
