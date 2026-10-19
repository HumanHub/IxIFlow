using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.Conformance;

[Trait("Spec", "G-08")]
public sealed class DataEditScenarios
{
    [Theory]
    [MemberData(nameof(StoreProfiles.All), MemberType = typeof(StoreProfiles))]
    public async Task EditDataAuditAndReceiptSurviveRepositoryAndHostRecreation(string profile)
    {
        await using var store = await StoreProfiles.CreateAsync(profile);
        var registry = new WorkflowVersionRegistry();
        var definition = Workflow.Create<Data>("ConformanceEdit").Step<Noop>().WaitFor<Go>("go").Step<Noop>().Build();
        var repository = store.NewRepository(); var first = Services(repository, registry);
        string id; long revision; DataEditTarget target; DataEditResult receipt;
        var patches = new[] { new DataPatch(["Number"], JsonSerializer.SerializeToElement(7)) };
        try
        {
            id = (await first.GetRequiredService<IWorkflowClient>().RunAsync(definition, new Data())).InstanceId;
            var operations = first.GetRequiredService<IWorkflowOperations>();
            await operations.PauseAsync(id, "pauser");
            var paused = (await repository.GetWorkflowInstanceAsync(id))!;
            revision = paused.Revision; target = new(WorkflowDebugRules.Checkpoint(paused).PauseGeneration);
            receipt = await operations.EditDataAsync(id, "edit", revision, target, patches, "editor");
        }
        finally
        {
            await first.GetRequiredService<WorkflowHost>().StopAsync(CancellationToken.None);
            await first.DisposeAsync();
            if (repository is IAsyncDisposable disposable) await disposable.DisposeAsync();
        }
        await using var second = Services(store.NewRepository(), registry);
        var secondStore = second.GetRequiredService<IWorkflowStateRepository>();
        var operations2 = second.GetRequiredService<IWorkflowOperations>();
        var saved = (await secondStore.GetWorkflowInstanceAsync(id))!;
        Assert.Equal(WorkflowStatus.Paused, saved.Status); Assert.Equal(revision + 1, saved.Revision);
        Assert.Equal(7, JsonSerializer.Deserialize<Data>(saved.WorkflowDataJson.InlineText)!.Number);
        Assert.Null(saved.RunnableAtUtc);
        Assert.Equal(receipt, await operations2.EditDataAsync(id, "edit", revision, target, patches, "editor"));
        var command = (await secondStore.GetDebugStatesAsync([id]))[id].Commands.Single(c => c.Id == "edit");
        Assert.Equal(receipt.Revision, command.AcknowledgedRevision); Assert.Equal(receipt, command.EditResult);
        var audit = Assert.IsType<DataEditedTrace>(Assert.Single(await secondStore.GetTraceAsync(id),
            e => e.Entry.EntryType == TraceEntryType.DataEdited).Entry.Payload);
        Assert.Equal("editor", audit.Actor); Assert.Equal("edit", audit.EditId); Assert.Equal(receipt.Revision, audit.Revision);
        Assert.Equal(["Number"], Assert.Single(audit.Changes).Path);
        await operations2.ContinueAsync(id, "continuer");
        var done = await second.GetRequiredService<IWorkflowClient>().ResumeAsync(id, "go", new Go());
        Assert.Equal(WorkflowExecutionStatus.Success, done.Status); Assert.Equal(7, Assert.IsType<Data>(done.WorkflowData).Number);
        Assert.Equal(receipt, await operations2.EditDataAsync(id, "edit", revision, target, patches, "editor"));
    }

    private static ServiceProvider Services(IWorkflowStateRepository repository, WorkflowVersionRegistry registry)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddIxIFlow(o => { o.Debugging = true; o.DataEditAuditKey = Enumerable.Repeat((byte)73, 32).ToArray(); });
        services.AddSingleton(repository); services.AddSingleton<IWorkflowVersionRegistry>(registry);
        return services.BuildServiceProvider();
    }
    public sealed class Data { public int Number { get; set; } }
    public sealed class Go;
    public sealed class Noop : IAsyncActivity
    {
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
