using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using IxIFlow.Builders;
using IxIFlow.Core;
using IxIFlow.Core.Runtime;
using IxIFlow.Extensions;
using IxIFlow.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IxIFlow.Tests.ExecutionTests;

[Trait("Feature", "waits")]
public sealed class WaitInputMappingTests : IClassFixture<SqliteTestDatabase<WaitInputMappingTests>>
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MappingsOverlayANewEventOrACopyOfTheDefault(bool suspend, bool hasDefault)
    {
        var payload = hasDefault ? new Answer { Customer = "default", Note = "keep" } : null;
        await using var services = Services();
        var client = services.GetRequiredService<IWorkflowClient>();
        var builder = new WorkflowBuilder<Data, object>([], "WaitInputs").Step<Prepare>();
        void Configure(IxIFlow.Builders.Interfaces.ISuspendSetupBuilder<Data, Answer, Prepare> wait)
        {
            if (payload != null) wait.DefaultPayload(payload);
            wait.Input(answer => answer.Customer).From(ctx => ctx.WorkflowData.Customer);
            wait.Input(answer => answer.Value).From(ctx => ctx.PreviousStep.Value);
            wait.Output(answer => answer.Customer).To(ctx => ctx.WorkflowData.Customer);
        }
        var definition = (suspend ? builder.Suspend<Answer>("review", configure: Configure)
            : builder.WaitFor<Answer>("review", configure: Configure)).Build();
        var started = await client.RunAsync(definition, new Data { Customer = "Ada" });
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        var wait = Assert.Single(await client.GetOpenWaitsAsync(new(InstanceId: started.InstanceId)));
        var suggestion = JsonSerializer.Deserialize<Answer>(wait.DefaultPayloadJson!)!;
        Assert.Equal("Ada", suggestion.Customer);
        Assert.Equal(17, suggestion.Value);
        Assert.Equal(hasDefault ? "keep" : "", suggestion.Note);
        if (payload != null) Assert.Equal("default", payload.Customer);
        var answered = await client.ReplyAsync(wait.Token, new Answer { Customer = "submitted" });
        Assert.Equal(WorkflowExecutionStatus.Success, answered.Status);
        Assert.Equal("submitted", Assert.IsType<Data>(answered.WorkflowData).Customer);
    }

    [Fact]
    public async Task ReopenedAndRecoveredInstanceKeepsTheSuggestionWithoutEvaluatingAgain()
    {
        _reads = 0;
        _failReads = false;
        var definition = new WorkflowBuilder<Data, object>([], "SavedWaitInputs")
            .WaitFor<Answer>("review", configure: wait => wait.Input(answer => answer.Customer)
                .From(ctx => Observe(ctx.WorkflowData.Customer))).Build();
        var registry = new WorkflowVersionRegistry();
        string instanceId;
        string? json;
        await using (var services = Services(true, registry))
        {
            var client = services.GetRequiredService<IWorkflowClient>();
            instanceId = (await client.RunAsync(definition, new Data { Customer = "Ada" })).InstanceId;
            json = Assert.Single(await client.GetOpenWaitsAsync(new(InstanceId: instanceId))).DefaultPayloadJson;
            // Model an owner lost after it saved the wait: the checkpoint and waits table stay intact.
            var store = services.GetRequiredService<IWorkflowStateRepository>();
            var token = Guid.NewGuid().ToString("N");
            var lease = (await store.TryAcquireExecutionLeaseAsync(instanceId, token, TimeSpan.FromMinutes(1)))!;
            var instance = (await store.GetWorkflowInstanceAsync(instanceId))!;
            instance.Status = WorkflowStatus.Running;
            Assert.Equal(WorkflowCommitStatus.Applied,
                (await store.CommitWorkflowInstanceAsync(CheckpointCommit.Create(instance, lease.Epoch, instance.Revision))).Status);
            Assert.True(await store.ReleaseExecutionLeaseAsync(instanceId, token));
        }
        Assert.Equal(1, _reads);
        _failReads = true;
        await using var reopened = Services(true, registry);
        var recoveredClient = reopened.GetRequiredService<IWorkflowClient>();
        var recovered = await recoveredClient.RecoverAsync(instanceId);
        Assert.True(recovered.Status == WorkflowExecutionStatus.Suspended, recovered.ErrorMessage);
        var wait = Assert.Single(await recoveredClient.GetOpenWaitsAsync(new(InstanceId: instanceId)));
        Assert.Equal(json, wait.DefaultPayloadJson);
        Assert.Equal("Ada", JsonSerializer.Deserialize<Answer>(json!)!.Customer);
        Assert.Equal(1, _reads);
        Assert.Equal(WorkflowExecutionStatus.Success,
            (await recoveredClient.ReplyAsync(wait.Token, new Answer())).Status);
        Assert.Equal(1, _reads);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task RequestReplySuggestionUsesTheCompletedRequestAndIsSaved(bool sqlite, bool hasDefault, bool timeout)
    {
        await using var services = Services(sqlite);
        var client = services.GetRequiredService<IWorkflowClient>();
        var definition = new WorkflowBuilder<Data, object>([], "RequestSuggestion")
            .Request<Ask>("review")
            .WaitFor<Answer>("review", configure: wait =>
            {
                if (hasDefault) wait.DefaultPayload(new Answer { Note = "keep" });
                wait.Input(answer => answer.Customer).From(ctx => ctx.WorkflowData.Customer);
                wait.Input(answer => answer.Value).From(ctx => ctx.PreviousStep.Value);
                if (timeout) wait.TimeoutAfter(TimeSpan.FromDays(1)).OnTimeout(body => body.Step<Prepare>());
            }).Build();
        var started = await client.RunAsync(definition, new Data { Customer = "Ada" });
        Assert.Equal(WorkflowExecutionStatus.Suspended, started.Status);
        var inbox = (IWorkflowInbox)services.GetRequiredService<IWorkflowStateRepository>();
        var wait = Assert.Single(await inbox.GetOpenWaitsAsync(new(InstanceId: started.InstanceId, Kind: WaitKind.Reply)));
        var suggestion = JsonSerializer.Deserialize<Answer>(wait.DefaultPayloadJson!)!;
        Assert.Equal("Ada", suggestion.Customer);
        Assert.Equal(23, suggestion.Value);
        Assert.Equal(hasDefault ? "keep" : "", suggestion.Note);
        Assert.Equal(timeout, wait.DeadlineUtc != null);
        Assert.Equal(wait.DefaultPayloadJson, (await inbox.GetWaitAsync(started.InstanceId, wait.WaitId))!.DefaultPayloadJson);
        Assert.Equal(WorkflowExecutionStatus.Success,
            (await client.ReplyAsync(ReplyToken.Sign(TestReplyKeys.Ring, started.InstanceId, wait.WaitId), new Answer { Value = 99 })).Status);
    }

    [Fact]
    public void ChangedMappingChangesTheStructureHash()
    {
        var first = new WorkflowBuilder<Data, object>([], "Fingerprint").WaitFor<Answer>("review", configure: wait => wait
            .Input(answer => answer.Value).From(ctx => ctx.WorkflowData.Value)).Build();
        var second = new WorkflowBuilder<Data, object>([], "Fingerprint").WaitFor<Answer>("review", configure: wait => wait
            .Input(answer => answer.Value).From(ctx => ctx.WorkflowData.Value + 1)).Build();
        Assert.NotEqual(new WorkflowScopeCatalog(first).StructureHash, new WorkflowScopeCatalog(second).StructureHash);
    }

    [Fact]
    public void ImpureWaitInputFailsTheBuild()
    {
        var error = Assert.Throws<WorkflowEngineException>(() => new WorkflowBuilder<Data, object>([], "ImpureWait")
            .WaitFor<Answer>("review", configure: wait => wait.Input(answer => answer.Value)
                .From(_ => DateTime.UtcNow.Hour)).Build());
        Assert.Equal(WorkflowErrorKind.InvalidDefinition, error.Kind);
        Assert.Contains("DateTime.UtcNow", error.Message);
    }

    [Fact]
    public async Task MappingFailureFailsTheStepWithoutOpeningAWait()
    {
        await using var services = Services();
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(
            new WorkflowBuilder<Data, object>([], "FailedWaitInput").WaitFor<Answer>("review", configure: wait => wait
                .Input(answer => answer.Value).From(ctx => 10 / ctx.WorkflowData.Value)).WithId("failed-wait").Build(), new Data());
        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Contains("zero", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await services.GetRequiredService<IWorkflowClient>().GetOpenWaitsAsync(new(InstanceId: result.InstanceId)));
    }

    [Fact]
    public void RequiredSuggestionMembersMustBeMappedOrSuppliedByTheDefault()
    {
        Assert.Throws<WorkflowEngineException>(() => new WorkflowBuilder<Data, object>([], "MissingWaitInput")
            .WaitFor<RequiredAnswer>("review", configure: wait => wait.Input(answer => answer.Value).From(_ => 1)).Build());
        new WorkflowBuilder<Data, object>([], "CompleteWaitInput").WaitFor<RequiredAnswer>("review", configure: wait => wait
            .DefaultPayload(new RequiredAnswer { Customer = "default" })
            .Input(answer => answer.Value).From(_ => 1)).Build();
    }

    [Fact]
    public async Task ARequiredMemberMappedToNullFailsTheStep()
    {
        await using var services = Services();
        var result = await services.GetRequiredService<IWorkflowClient>().RunAsync(
            new WorkflowBuilder<Data, object>([], "NullSuggestion")
                .WaitFor<RequiredAnswer>("review", configure: wait => wait.Input(answer => answer.Customer)
                    .From(ctx => (string?)null)).Build(), new Data());
        Assert.Equal(WorkflowExecutionStatus.Faulted, result.Status);
        Assert.Contains("required", result.ErrorMessage!);
    }

    [Fact]
    public void WaitInputsNeedPublicSettersAndCompatibleTypes()
    {
        Assert.Throws<WorkflowEngineException>(() => new WorkflowBuilder<Data, object>([], "ReadOnlyWaitInput")
            .WaitFor<FixedAnswer>("review", configure: wait => wait.Input(answer => answer.Value).From(_ => 1)).Build());
        var steps = new List<WorkflowStep>();
        var builder = new WorkflowBuilder<Data, object>(steps, "WrongType")
            .WaitFor<Answer>("review", configure: wait => wait.Input(answer => answer.Value).From(_ => 1));
        steps[0].InputMappings[0].Expression = (System.Linq.Expressions.Expression<Func<string>>)(() => "wrong");
        Assert.Throws<WorkflowEngineException>(() => builder.Build());
    }

    private static int _reads;
    private static bool _failReads;
    private static string Observe(string value)
    {
        _reads++;
        if (_failReads) throw new InvalidOperationException("Suggestion evaluated again");
        return value;
    }

    private static ServiceProvider Services(bool sqlite = false, WorkflowVersionRegistry? registry = null)
    {
        var services = new ServiceCollection().AddLogging();
        var builder = services.AddIxIFlow(options => options.ReplyTokenKeys = TestReplyKeys.Ring);
        if (sqlite) builder.UseSqlite(SqliteTestDatabase<WaitInputMappingTests>.ConnectionString);
        if (registry != null) services.AddSingleton<IWorkflowVersionRegistry>(registry);
        return services.BuildServiceProvider();
    }

    public sealed class Data
    {
        public string Customer { get; set; } = "";
        public int Value { get; set; }
    }
    public sealed class Answer
    {
        public string Customer { get; set; } = "";
        public string Note { get; set; } = "";
        public int Value { get; set; }
    }
    public sealed class RequiredAnswer
    {
        [Required] public string? Customer { get; set; }
        public int Value { get; set; }
    }
    public sealed class FixedAnswer { public int Value { get; private set; } }
    public sealed class Prepare : IAsyncActivity
    {
        public int Value { get; set; }
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { Value = 17; return Task.CompletedTask; }
    }
    public sealed class Ask : IRequestActivity<Answer>
    {
        public int Value { get; set; }
        public Task ExecuteAsync(IActivityContext context, CancellationToken cancellationToken = default)
        { Value = 23; return Task.CompletedTask; }
    }
}
