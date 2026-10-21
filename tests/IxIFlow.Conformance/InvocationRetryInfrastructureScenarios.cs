using System.Reflection;
using System.Runtime.ExceptionServices;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using Microsoft.Extensions.Time.Testing;
using static IxIFlow.Tests.Conformance.InvocationRetryRecoveryScenarios;

namespace IxIFlow.Tests.Conformance;

[Trait("Spec", "D-18")]
public sealed class InvocationRetryInfrastructureScenarios
{
    public static IEnumerable<object[]> Profiles => StoreProfiles.ProfileNames.SelectMany(profile =>
        new[] { false, true }.Select(retry => new object[] { profile, retry }));

    [Theory, MemberData(nameof(Profiles))]
    public async Task HostStartFailureKeepsTheCommittedInvocationAttemptDeadlineAndBudget(string profile, bool retry)
    {
        await using var fixture = await InvocationRetryProfile.Create(profile);
        var first = await fixture.Open(holdRetries: true);
        var child = Workflow.Create<Data>("InfrastructureRetryChild").Step<FailOnce>().Build();
        await fixture.Catalog.RegisterWorkflowAsync(child);
        var parent = new WorkflowBuilder<Data, object>([], "InfrastructureRetryInvocation")
            .Invoke<Data>(child.Name, 1, setup => setup
                .OnError<InvalidOperationException>(error => error.ThenRetry(new RetryPolicy(fixture.Delay, fixture.Delay, 1, 1)))
                .OnError<IOException>(error => { if (retry) error.ThenRetry(new RetryPolicy(3)); else error.ThenIgnore(); })).Build();
        await fixture.Catalog.RegisterWorkflowAsync(parent);
        var id = await first.Client.StartAsync(parent, new Data());
        var parked = await first.Parked(id);
        var before = Record(parked);
        var wakeAt = parked.RunnableAtUtc!.Value;
        await first.DisposeAsync();
        var catalog = DispatchProxy.Create<IWorkflowCatalog, CatalogFault>();
        var fault = (CatalogFault)(object)catalog;
        fault.Inner = fixture.Catalog;
        fault.Armed = true;
        var second = await fixture.Open(catalog: catalog);
        Assert.Equal(wakeAt, (await second.Store.GetWorkflowInstanceAsync(id))!.RunnableAtUtc);
        await fixture.Due(second.Store, id, InvocationRetryProfile.RetryDeadline(parked));
        var error = await Assert.ThrowsAsync<WorkflowEngineException>(() => second.Client.RecoverAsync(id));
        Assert.Equal(WorkflowErrorKind.Infrastructure, error.Kind);
        var interrupted = (await second.Store.GetWorkflowInstanceAsync(id))!;
        Assert.False(WorkflowStoreRules.IsTerminal(interrupted.Status));
        var record = Record(interrupted);
        Assert.Equal(before.Id, record.Id);
        Assert.Equal(before.RetryAfterUtc, record.RetryAfterUtc);
        Assert.Equal(before.Attempts[0].Id, Assert.Single(record.Attempts).Id);
        Assert.Equal(1, record.ScheduledRetries);
        Assert.Single((await second.Store.QueryInstancesAsync(new InstanceQuery(ParentInstanceId: id))).Items);
        fault.Armed = false;
        await second.Client.RecoverAsync(id);
        await Success(second.Client, id);
        var completed = Record((await second.Store.GetWorkflowInstanceAsync(id))!);
        Assert.Equal(2, completed.Attempts.Count);
        Assert.Equal(1, completed.ScheduledRetries);
        Assert.Equal(2, fixture.Ledger.Calls);
    }

    [Theory]
    [InlineData(StoreProfiles.LocalMemory)]
    [InlineData(StoreProfiles.LocalSqlite)]
    public async Task DeadlineWaitUsesTheStoreClockWithoutRequiringWorkflowCapabilities(string profile)
    {
        await using var fixture = await InvocationRetryProfile.Create(profile);
        var clock = Assert.IsType<FakeTimeProvider>(fixture.Clock);
        clock.Advance(TimeSpan.FromHours(1));
        var first = await fixture.Open(holdRetries: true);
        var child = Workflow.Create<Data>("ClockRetryChild").Step<FailOnce>().Build();
        await fixture.Catalog.RegisterWorkflowAsync(child);
        var parent = new WorkflowBuilder<Data, object>([], "ClockRetryInvocation")
            .Invoke<Data>(child.Name, 1, setup => setup.OnError(error =>
                error.ThenRetry(new RetryPolicy(fixture.Delay, fixture.Delay, 1, 1)))).Build();
        await fixture.Catalog.RegisterWorkflowAsync(parent);
        var id = await first.Client.StartAsync(parent, new Data());
        var parked = await first.Parked(id);
        await first.DisposeAsync();
        var second = await fixture.Open();
        // Exercise the server-clock path against a controllable store whose clock is ahead of the host.
        var waiting = InvocationRetryProfile.Due(TimeProvider.System, second.Store, id, parked.RunnableAtUtc!.Value);
        Assert.False(waiting.IsCompleted);
        await fixture.Due(second.Store, id, parked.RunnableAtUtc.Value);
        await waiting.WaitAsync(InvocationRetryProfile.Patience(fixture.Delay));
        Assert.DoesNotContain(await second.Store.GetRunnableInstancesAsync(new ClaimQuery(Guid.NewGuid(), new Dictionary<string, string>())),
            item => item.InstanceId == id);
        await second.Client.RecoverAsync(id);
        await Success(second.Client, id);
        Assert.Equal(2, Record((await second.Store.GetWorkflowInstanceAsync(id))!).Attempts.Count);
    }

    public class CatalogFault : DispatchProxy
    {
        internal IWorkflowCatalog Inner = null!;
        internal bool Armed;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (Armed && method!.Name == nameof(IWorkflowCatalog.GetWorkflowVersionAsync) && args?[0] as string == "InfrastructureRetryChild")
                throw new IOException("injected child-start catalog failure");
            try { return method!.Invoke(Inner, args); }
            catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException!).Throw(); throw; }
        }
    }
}
