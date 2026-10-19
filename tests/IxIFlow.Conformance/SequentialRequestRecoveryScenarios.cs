using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace IxIFlow.Tests.Conformance;

[Trait("Feature", "waits"), Trait("Spec", "C-18"), Trait("Spec", "D-16"), Trait("Spec", "J-03")]
public sealed class SequentialRequestRecoveryScenarios
{
    [Fact]
    public async Task SqliteReopenedOwnerResendsTheSameTokenAndConsumesTheAnswerOnce()
    {
        await using var profile = await StoreProfiles.CreateAsync(StoreProfiles.LocalSqlite);
        var time = new FakeTimeProvider();
        var registry = new WorkflowVersionRegistry();
        var probe = new Probe();
        var definition = Workflow.Create<Data>("ReopenedRequest")
            .Request<Send>("answer")
            .WaitFor<Answer>("answer", configure: wait => wait.Output(answer => answer.Value).To(ctx => ctx.WorkflowData.Value))
            .Step<After>().Build();
        await registry.RegisterWorkflowAsync(definition);
        var id = Guid.NewGuid().ToString("N");
        var firstStore = profile.NewRepository(time);
        var fault = new FaultRepository(firstStore) { FailCommit = _ => probe.Tokens.Count > 0 };
        await using (var first = Services(fault, registry, probe, time))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => first.GetRequiredService<IWorkflowClient>()
                .RunAsync(definition, new Data(), new WorkflowOptions { InstanceId = id }));
            Assert.Single(probe.Tokens);
        }
        // The first SQLite owner is disposed before another repository opens the same file.
        await ((IAsyncDisposable)firstStore).DisposeAsync();
        var secondStore = profile.NewRepository(time);
        await using var second = Services(secondStore, registry, probe, time);
        var client = second.GetRequiredService<IWorkflowClient>();
        var recovered = await client.RecoverAsync(id);
        Assert.True(recovered.Status == WorkflowExecutionStatus.Suspended, recovered.ErrorMessage);
        Assert.Equal(2, probe.Tokens.Count);
        Assert.Equal(probe.Tokens[0], probe.Tokens[1]);
        var answered = await client.ReplyAsync(probe.Tokens[0], new Answer { Value = 37 }, "answer-once");
        EventAssert.Consumed(answered);
        Assert.Equal(37, Assert.IsType<Data>(answered.WorkflowData).Value);
        Assert.Equal(1, probe.Downstream);
        var repeated = await client.ReplyAsync(probe.Tokens[1], new Answer { Value = 37 }, "answer-once");
        Assert.Equal(1, probe.Downstream);
        Assert.Equal(WorkflowExecutionStatus.Success, repeated.Status);
        Assert.Empty((await secondStore.GetPendingEventsAsync(id, 0, 100)).Events);
        var trace = await secondStore.GetTraceAsync(id);
        Assert.Single(trace, e => e.Entry.EntryType == TraceEntryType.EventReceived);
        Assert.Equal(37, System.Text.Json.JsonDocument.Parse((await secondStore.GetWorkflowInstanceAsync(id))!
            .WorkflowDataJson).RootElement.GetProperty("Value").GetInt32());
    }

    private static ServiceProvider Services(IWorkflowStateRepository store, IWorkflowVersionRegistry registry, Probe probe, TimeProvider time)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddIxIFlow();
        services.AddSingleton(store);
        services.AddSingleton(registry);
        services.AddSingleton(probe);
        services.AddSingleton(time);
        services.AddTransient<Send>();
        services.AddTransient<After>();
        return services.BuildServiceProvider();
    }
    public sealed class Data { public int Value { get; set; } }
    public sealed class Answer { public int Value { get; set; } }
    public sealed class Probe { public List<string> Tokens { get; } = []; public int Downstream; }
    public sealed class Send(Probe probe) : IRequestActivity<Answer>, IRecoverableActivity<bool>
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { probe.Tokens.Add(context.Reply!.Token); return Task.CompletedTask; }
        public bool CaptureRecoveryState(IActivityContext context) => true;
        public Task<ActivityRecoveryResult<bool>> RecoverAsync(bool state, IActivityContext context,
            CancellationToken cancellationToken = default) => Task.FromResult<ActivityRecoveryResult<bool>>(new ActivityRecoveryResult<bool>.Execute());
    }
    public sealed class After(Probe probe) : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref probe.Downstream); return Task.CompletedTask; }
    }
}
