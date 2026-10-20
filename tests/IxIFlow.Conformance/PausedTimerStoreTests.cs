using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

[Trait("Spec", "G-09")]
public sealed class PausedTimerStoreTests
{
    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task LostContinueAcknowledgementKeepsDeadlineScheduling(string profile)
    {
        var time = new ManualTime();
        await using var environment = await StoreProfiles.CreateAsync(profile, time);
        var store = environment.NewRepository();
        var lose = false;
        var faulty = new FaultRepository(store) { LoseAcknowledgement = i => lose && i.Status == WorkflowStatus.Running };
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow(o => o.Debugging = true);
        services.AddSingleton<IWorkflowStateRepository>(faulty);
        services.AddSingleton<TimeProvider>(time);
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IWorkflowClient>();
        var ops = provider.GetRequiredService<IWorkflowOperations>();
        var definition = Workflow.Create<Go>("LostContinueAck").Step<Noop>().Delay(TimeSpan.FromMinutes(5)).Build();
        var id = (await client.RunAsync(definition, new Go())).InstanceId;
        var original = Assert.Single(await store.GetOpenWaitsAsync(new(InstanceId: id, Kind: WaitKind.Delay)));
        await ops.PauseAsync(id, "pause");
        await Advance(profile, time);
        lose = true;
        var continued = await ops.ContinueAsync(id, "continue");
        Assert.True(continued.Status == WorkflowExecutionStatus.Suspended, continued.ErrorMessage + "\n" + continued.ErrorStackTrace);
        Assert.Equal(original.DeadlineUtc, (await store.GetWaitAsync(id, original.WaitId))!.DeadlineUtc);
        Assert.Equal((await store.GetWaitAsync(id, original.WaitId))!.DeadlineUtc, (await store.GetWorkflowInstanceAsync(id))!.RunnableAtUtc);
    }

    private static async Task Advance(string profile, ManualTime time)
    {
        if (profile is StoreProfiles.LocalMemory or StoreProfiles.LocalSqlite) time.Advance(TimeSpan.FromSeconds(3));
        else await Task.Delay(30);
    }
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
    public sealed class Go;
    public sealed class Noop : IAsyncActivity
    { public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask; }
}
