using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

public sealed class ReplyTokenScenarios
{
    [Theory, MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task HostsCanAnswerEachOthersReplyCapabilities(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var registry = new WorkflowVersionRegistry();
        // SQLite permits one repository owner. Separate hosts use that same owning repository;
        // the other profiles use independently opened repositories on the shared store.
        var firstRepository = store.NewRepository();
        var secondRepository = profile == StoreProfiles.LocalSqlite ? firstRepository : store.NewRepository();
        await using var first = Host(firstRepository, registry);
        await using var second = Host(secondRepository, registry);
        var left = first.GetRequiredService<IWorkflowClient>();
        var right = second.GetRequiredService<IWorkflowClient>();
        var definition = Workflow.Create<Data>("CrossHostReply").Step<OperationsHttpFactory.Noop>().WaitFor<Answer>("answer").Build();
        foreach (var (issuer, replier) in new[] { (left, right), (right, left) })
        {
            var started = await issuer.RunAsync(definition, new Data());
            var wait = Assert.Single(await issuer.GetOpenWaitsAsync(new(InstanceId: started.InstanceId)));
            Assert.Equal(EventOutcome.Consumed, (await replier.ReplyAsync(wait.Token, new Answer())).Event!.Outcome);
        }
    }

    private static ServiceProvider Host(IWorkflowStateRepository repository, IWorkflowVersionRegistry registry)
    {
        var services = new ServiceCollection(); services.AddLogging();
        services.AddIxIFlow();
        services.AddTransient<OperationsHttpFactory.Noop>(); services.AddSingleton(repository); services.AddSingleton(registry);
        services.AddSingleton(new InProcessInstanceGate());
        return services.BuildServiceProvider();
    }
    public sealed class Data;
    public sealed class Answer;
}
