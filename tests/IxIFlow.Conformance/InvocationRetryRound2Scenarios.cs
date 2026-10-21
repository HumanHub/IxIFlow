using System.Data.Common;
using System.Reflection;
using System.Runtime.ExceptionServices;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;
using static IxIFlow.Tests.Conformance.InvocationRetryRecoveryScenarios;

namespace IxIFlow.Tests.Conformance;

[Trait("Spec", "D-18")]
public sealed class InvocationRetryRound2Scenarios
{
    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task DefinitionResolutionInterruptionRetainsCommittedInvocationSchedule(string profile)
    {
        await using var fixture = await InvocationRetryProfile.Create(profile);
        var first = await fixture.Open(holdRetries: true);
        var parent = await FailingParent(fixture, fixture.Delay, retries: 1);
        var id = await first.Client.StartAsync(parent, new Data());
        var parked = await Parked(first, id, 1);
        var before = Record(parked);
        await first.DisposeAsync();
        var catalog = DispatchProxy.Create<IWorkflowCatalog, ResolutionFault>();
        var fault = (ResolutionFault)(object)catalog;
        fault.Inner = fixture.Catalog;
        fault.ChildName = "Round2Child";
        fault.Broken = true;
        var second = await fixture.Open(catalog: catalog);
        await fixture.Due(second.Store, id, InvocationRetryProfile.RetryDeadline(parked));
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => second.Client.RecoverAsync(id));
        Assert.Equal(WorkflowErrorKind.Infrastructure, error.Kind);
        var saved = (await second.Store.GetWorkflowInstanceAsync(id))!;
        Assert.False(WorkflowStoreRules.IsTerminal(saved.Status));
        var interrupted = Record(saved);
        Assert.Equal(before.Id, interrupted.Id);
        Assert.Equal(before.RetryAfterUtc, interrupted.RetryAfterUtc);
        Assert.Equal(parked.RunnableAtUtc, saved.RunnableAtUtc);
        Assert.Equal(before.ScheduledRetries, interrupted.ScheduledRetries);
        Assert.Equal(before.Attempts[0].Id, Assert.Single(interrupted.Attempts).Id);
        Assert.Single((await second.Store.QueryInstancesAsync(new InstanceQuery(ParentInstanceId: id))).Items);
        fault.Broken = false;
        await second.Client.RecoverAsync(id);
        await Success(second.Client, id);
        var final = Record((await second.Store.GetWorkflowInstanceAsync(id))!);
        Assert.Equal(2, final.Attempts.Count);
        Assert.Equal(1, final.ScheduledRetries);
    }

    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task TerminatingInvocationBackoffPreservesHistoryAndReplayFacts(string profile)
    {
        await using var fixture = await InvocationRetryProfile.Create(profile);
        fixture.Ledger.Failures = int.MaxValue;
        var host = await fixture.Open(holdRetries: true);
        var parent = await FailingParent(fixture, fixture.Delay, retries: 2);
        var id = await host.Client.StartAsync(parent, new Data());
        var first = await Parked(host, id, 1);
        await fixture.Due(host.Store, id, InvocationRetryProfile.RetryDeadline(first));
        host.ReleaseRetry();
        var advancing = host.Client.RecoverAsync(id);
        var parked = await Parked(host, id, 2);
        var before = Record(parked);
        await host.DisposeAsync();
        await advancing;
        host = await fixture.Open();
        await host.Operations.TerminateAsync(id, "during-backoff");
        var inspection = Assert.Single((await host.Operations.InspectAsync(id))!.Invocations);
        Assert.Equal("Terminated", inspection.Disposition);
        Assert.Null(inspection.RetryAfterUtc);
        Assert.Equal(2, inspection.Attempts.Count);
        Assert.Equal("Retry", inspection.Attempts[0].Disposition);
        Assert.Equal(before.Attempts[0].RetryAfterUtc, inspection.Attempts[0].RetryAfterUtc);
        Assert.NotNull(inspection.Attempts[0].RetryAfterUtc);
        var attempt = inspection.Attempts[1];
        Assert.Equal("Terminated", attempt.Disposition);
        Assert.Null(attempt.RetryAfterUtc);
        Assert.Equal(before.Attempts[1].EndedAtUtc, attempt.EndedAtUtc);
        var replay = TraceReplay.Load(parent, await host.Store.GetTraceAsync(id));
        while (replay.StepForward()) { }
        foreach (var recorded in inspection.Attempts)
        {
            Assert.Equal(WorkflowStatus.Failed, recorded.ChildStatus);
            Assert.Equal(0, recorded.PolicyIndex);
            Assert.Equal(StepErrorAction.Retry, recorded.Action);
            var fact = Assert.Single(replay.Current.Invocations, fact => fact.PolicyAttemptId == recorded.PolicyAttemptId);
            Assert.Equal(recorded.Disposition, fact.Disposition);
            Assert.Equal(recorded.ChildInstanceId, fact.ChildInstanceId);
            Assert.Equal(recorded.ChildStatus, fact.ChildStatus);
            Assert.Equal(recorded.PolicyIndex, fact.PolicyIndex);
            Assert.Equal(recorded.Action, fact.Action);
            Assert.Equal(recorded.RetryAfterUtc, fact.RetryAfterUtc);
        }
        Assert.All((await host.Store.QueryInstancesAsync(new InstanceQuery(ParentInstanceId: id))).Items,
            child => Assert.Equal(WorkflowStatus.Failed, child.Status));
    }

    public static IEnumerable<object[]> TraceProfiles => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { "disabled", "truncated", "activities", "budget" }.Select(mode => new object[] { profile, mode }));

    [Theory, MemberData(nameof(TraceProfiles))]
    public async Task TerminationInvocationTracingUsesSettingsAndLimitsWithoutRecordPayloads(string profile, string mode)
    {
        await using var fixture = await InvocationRetryProfile.Create(profile);
        var payloads = new UnavailableRecordPayloads();
        var host = await fixture.Open(services =>
        {
            services.AddIxIFlow(options =>
            {
                options.PayloadThresholdBytes = 128;
                if (mode == "truncated") options.MaxTraceEntriesPerCommit = 1;
                if (mode == "activities") options.TraceDetail = TraceDetail.Activities;
                if (mode == "budget") options.MaxTraceEntriesPerInstance = 10_000;
            });
            services.AddSingleton<IPayloadStore>(payloads);
        });
        payloads.Inner = host.Store.PayloadStore;
        var child = new WorkflowBuilder<Data, object>([], "TerminationRound2Child").WaitFor<Signal>("finish").Build();
        await fixture.Catalog.RegisterWorkflowAsync(child);
        var parent = new WorkflowBuilder<Data, object>([], "TerminationRound2Invocation").Parallel(paths => paths
            .Do(body => body.Invoke<Data>(child.Name, 1))
            .Do(body => body.Invoke<Data>(child.Name, 1))
            .Do(body => body.Invoke<Data>(child.Name, 1))).Build();
        var started = await host.Client.RunAsync(parent, new Data(), new() { EnableTracing = mode != "disabled" });
        await host.Services.GetRequiredService<WorkflowHost>().StopAsync(CancellationToken.None);
        var before = (await host.Store.GetWorkflowInstanceAsync(started.InstanceId))!;
        var budget = ExecutionCheckpoint.ReadTrunk(before.ExecutionStateJson).TraceEntriesRecorded + 1;
        var records = before.SavedParts!.Where(part => part.Key.StartsWith("v/")).ToArray();
        Assert.Equal(3, records.Length);
        Assert.All(records, part => Assert.NotNull(part.Value.Reference));
        payloads.Broken = records.Select(part => part.Value.Reference!).ToHashSet();
        // The host cannot resolve the parent or child and cannot hydrate invocation records.
        var coldCatalog = new WorkflowCatalog();
        await host.DisposeAsync();
        var cold = await fixture.Open(services =>
        {
            services.AddIxIFlow(options =>
            {
                if (mode == "truncated") options.MaxTraceEntriesPerCommit = 1;
                if (mode == "activities") options.TraceDetail = TraceDetail.Activities;
                if (mode == "budget") options.MaxTraceEntriesPerInstance = budget;
            });
            services.AddSingleton<IPayloadStore>(payloads);
        }, catalog: coldCatalog);
        payloads.Inner = cold.Store.PayloadStore;
        before = (await cold.Store.GetWorkflowInstanceAsync(started.InstanceId))!;
        await cold.Operations.TerminateAsync(started.InstanceId, "unavailable-records");
        var after = (await cold.Store.GetWorkflowInstanceAsync(started.InstanceId))!;
        Assert.Equal(WorkflowStatus.Terminated, after.Status);
        Assert.Equal(0, payloads.BrokenReads);
        Assert.Equal(before.SavedParts, after.SavedParts);
        var traces = await cold.Store.GetTraceAsync(started.InstanceId);
        var audit = Assert.Single(traces, trace => trace.Entry.EntryType == TraceEntryType.WorkflowTerminated);
        // Child shutdown may append refused-completion audits after this commit.
        var termination = traces.Where(trace => trace.CommitEndSequence == audit.CommitEndSequence).ToArray();
        Assert.Single(termination, trace => trace.Entry.EntryType == TraceEntryType.WorkflowTerminated);
        if (mode == "disabled") Assert.Single(termination);
        else if (mode == "truncated")
        {
            Assert.Equal(2, termination.Length); // one capacity slot plus the mandatory audit
            var marker = Assert.IsType<TraceTruncation>(Assert.Single(termination,
                trace => trace.Entry.EntryType == TraceEntryType.Truncated).Entry.Payload);
            Assert.Equal(3, marker.DroppedCount);
        }
        else Assert.Equal(3, termination.Count(trace => trace.Entry.Payload is InvocationTrace { Disposition: "Terminated" }));
        if (mode == "budget")
        {
            Assert.Equal(budget, Assert.IsType<TraceBudget>(Assert.Single(termination,
                trace => trace.Entry.EntryType == TraceEntryType.TraceBudgetReached).Entry.Payload).Limit);
            var checkpoint = ExecutionCheckpoint.ReadTrunk(after.ExecutionStateJson);
            Assert.True(checkpoint.TraceBudgetExhausted);
            Assert.Equal(budget, checkpoint.TraceEntriesRecorded);
        }
        payloads.Broken.Clear();
        await coldCatalog.RegisterWorkflowAsync(parent);
        await coldCatalog.RegisterWorkflowAsync(child);
        var inspection = (await cold.Operations.InspectAsync(started.InstanceId))!;
        Assert.Equal(3, inspection.Invocations.Count);
        Assert.All(inspection.Invocations, invocation => Assert.Equal("Terminated", Assert.Single(invocation.Attempts).Disposition));
    }

    private static async Task<WorkflowDefinition> FailingParent(InvocationRetryProfile fixture, TimeSpan delay, int retries)
    {
        var child = Workflow.Create<Data>("Round2Child").Step<FailOnce>().Build();
        await fixture.Catalog.RegisterWorkflowAsync(child);
        var parent = new WorkflowBuilder<Data, object>([], "Round2Invocation")
            .Invoke<Data>(child.Name, 1, setup => setup.OnError(error => error.ThenRetry(new RetryPolicy(delay, delay, retries, 1)))).Build();
        await fixture.Catalog.RegisterWorkflowAsync(parent);
        return parent;
    }

    private static Task<WorkflowInstance> Parked(InvocationRetryProfile.Host host, string id, int retries) =>
        host.Parked(id, retries);

    public class ResolutionFault : DispatchProxy
    {
        internal IWorkflowCatalog Inner = null!;
        internal string ChildName = "";
        internal bool Broken;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (Broken && method!.Name == nameof(IWorkflowCatalog.ResolveWorkflowDefinitionAsync) && (string)args![0]! == ChildName)
                return Task.FromException<WorkflowDefinitionLease>(new IOException("injected definition lookup interruption"));
            try { return method!.Invoke(Inner, args); }
            catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException!).Throw(); throw; }
        }
    }

    private sealed class UnavailableRecordPayloads : IPayloadStore
    {
        internal IPayloadStore Inner = null!;
        internal HashSet<PayloadReference> Broken = [];
        internal int BrokenReads;
        public ValueTask<PayloadReference> PutAsync(ReadOnlyMemory<byte> body, IReadOnlyCollection<PayloadReference>? dependencies = null, CancellationToken ct = default) => Inner.PutAsync(body, dependencies, ct);
        public ValueTask<ReadOnlyMemory<byte>?> GetAsync(PayloadReference reference, CancellationToken ct = default)
        {
            if (!Broken.Contains(reference)) return Inner.GetAsync(reference, ct);
            Interlocked.Increment(ref BrokenReads);
            return new((ReadOnlyMemory<byte>?)null);
        }
        public ValueTask<bool> CollectAsync(PayloadReference reference, TimeSpan grace, CancellationToken ct = default) => Inner.CollectAsync(reference, grace, ct);
        public ValueTask<IReadOnlyList<PayloadReference>> FindCollectionCandidatesAsync(int limit, TimeSpan grace, CancellationToken ct = default) => Inner.FindCollectionCandidatesAsync(limit, grace, ct);
        public ValueTask InTransactionAsync(Func<IPayloadTransaction, CancellationToken, ValueTask> action, CancellationToken ct = default) => Inner.InTransactionAsync(action, ct);
        public IPayloadTransaction Enlist(DbTransaction transaction) => Inner.Enlist(transaction);
    }
}
